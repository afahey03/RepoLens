import * as vscode from 'vscode';
import * as path from 'path';
import { RepoLensApi } from './api';
import { SymbolTreeProvider } from './symbolTree';
import { RepoInfoProvider } from './repoInfo';
import { OverviewPanel } from './overviewPanel';
import { ArchitecturePanel } from './architecturePanel';
import { SearchPanel } from './searchPanel';
import { ApiServerManager } from './serverManager';
import type { AnalyzeResponse, RepositoryOverview, ArchitectureResponse, GraphStatsResponse } from './types';

/** State kept for the currently-analyzed repository. */
interface RepoSession {
    repoId: string;
    repoUrl: string;
    repoPath: string;           // local clone path (may be empty for remote-only)
    overview?: RepositoryOverview;
    architecture?: ArchitectureResponse;
    stats?: GraphStatsResponse;
}

let session: RepoSession | undefined;
let serverManager: ApiServerManager | undefined;

export function activate(context: vscode.ExtensionContext): void {
    const api = new RepoLensApi();
    const symbolTree = new SymbolTreeProvider(api);
    const repoInfo = new RepoInfoProvider();
    serverManager = new ApiServerManager();
    context.subscriptions.push(serverManager);

    // ── Register tree views ──
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider('repolens.symbolTree', symbolTree),
        vscode.window.registerTreeDataProvider('repolens.info', repoInfo),
    );

    // ── Commands ──

    // Analyze a remote GitHub repo by URL
    context.subscriptions.push(
        vscode.commands.registerCommand('repolens.analyzeRepo', async () => {
            const url = await vscode.window.showInputBox({
                prompt: 'Enter a GitHub repository URL',
                placeHolder: 'https://github.com/owner/repo',
                validateInput: (v) => {
                    if (!v.trim()) { return 'URL is required'; }
                    if (!/^https?:\/\//i.test(v.trim())) { return 'Must be a valid HTTP(S) URL'; }
                    return undefined;
                },
            });
            if (!url) { return; }
            await analyzeRepository(api, symbolTree, repoInfo, url.trim());
        }),
    );

    // Analyze the currently opened workspace folder
    context.subscriptions.push(
        vscode.commands.registerCommand('repolens.analyzeWorkspace', async () => {
            const folder = await pickWorkspaceFolder();
            if (!folder) { return; }

            const gitUrl = await detectGitRemote(folder.uri.fsPath);
            if (!gitUrl) {
                // No remote detected — offer to enter manually
                const url = await vscode.window.showInputBox({
                    prompt: 'No git remote found. Enter a GitHub repository URL manually.',
                    placeHolder: 'https://github.com/owner/repo',
                });
                if (!url) { return; }
                await analyzeRepository(api, symbolTree, repoInfo, url.trim(), folder.uri.fsPath);
                return;
            }

            // Normalise SSH URLs → HTTPS
            const httpsUrl = normalizeGitUrl(gitUrl);
            await analyzeRepository(api, symbolTree, repoInfo, httpsUrl, folder.uri.fsPath);
        }),
    );

    // Show overview webview
    context.subscriptions.push(
        vscode.commands.registerCommand('repolens.showOverview', async () => {
            if (!session) { vscode.window.showWarningMessage('Analyze a repository first.'); return; }
            if (!session.overview) {
                try {
                    session.overview = await api.getOverview(session.repoId);
                } catch (err: any) {
                    vscode.window.showErrorMessage(`Failed to load overview: ${err.message}`);
                    return;
                }
            }
            OverviewPanel.show(session.overview!, session.stats);
        }),
    );

    // Show architecture graph
    context.subscriptions.push(
        vscode.commands.registerCommand('repolens.showArchitecture', async () => {
            if (!session) { vscode.window.showWarningMessage('Analyze a repository first.'); return; }
            if (!session.architecture) {
                try {
                    session.architecture = await api.getArchitecture(session.repoId);
                } catch (err: any) {
                    vscode.window.showErrorMessage(`Failed to load architecture: ${err.message}`);
                    return;
                }
            }
            ArchitecturePanel.show(session.architecture!);
        }),
    );

    // Show search panel
    context.subscriptions.push(
        vscode.commands.registerCommand('repolens.showSearch', () => {
            if (!session) { vscode.window.showWarningMessage('Analyze a repository first.'); return; }
            SearchPanel.show(api, session.repoId, session.repoPath);
        }),
    );

    // Refresh symbol tree
    context.subscriptions.push(
        vscode.commands.registerCommand('repolens.refreshSymbols', async () => {
            if (!session) { return; }
            await symbolTree.load(session.repoId, session.repoPath);
        }),
    );

    // Navigate to a symbol in the editor
    context.subscriptions.push(
        vscode.commands.registerCommand(
            'repolens.navigateToSymbol',
            async (filePath: string, line: number, repoPath?: string) => {
                const resolved = resolveFilePath(filePath, repoPath);
                if (!resolved) {
                    vscode.window.showWarningMessage(`Cannot resolve file: ${filePath}`);
                    return;
                }
                try {
                    const doc = await vscode.workspace.openTextDocument(resolved);
                    const editor = await vscode.window.showTextDocument(doc, { preview: true });
                    const lineIdx = Math.max(0, (line || 1) - 1);
                    const range = new vscode.Range(lineIdx, 0, lineIdx, 0);
                    editor.selection = new vscode.Selection(range.start, range.start);
                    editor.revealRange(range, vscode.TextEditorRevealType.InCenter);
                } catch {
                    vscode.window.showWarningMessage(`Could not open file: ${resolved}`);
                }
            },
        ),
    );

    // ── Auto-prompt: offer to analyze when a workspace with a git remote is open ──
    promptToAnalyzeWorkspace(api, symbolTree, repoInfo);
}

export function deactivate(): void {
    session = undefined;
    if (serverManager) {
        serverManager.stopServer();
        serverManager = undefined;
    }
}

// ─────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────

async function analyzeRepository(
    api: RepoLensApi,
    symbolTree: SymbolTreeProvider,
    repoInfo: RepoInfoProvider,
    repoUrl: string,
    localPath?: string,
): Promise<void> {
    const config = vscode.workspace.getConfiguration('repolens');
    const token = config.get<string>('gitHubToken') || undefined;
    const openAiKey = config.get<string>('openAiApiKey') || undefined;

    await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'RepoLens', cancellable: false },
        async (progress) => {
            // Ensure the API server is running (auto-start only for localhost)
            const autoStart = config.get<boolean>('autoStartServer', true);
            const apiUrl = config.get<string>('apiBaseUrl', 'https://www.repositorylens.com');
            const isLocal = apiUrl.includes('localhost') || apiUrl.includes('127.0.0.1');
            if (autoStart && isLocal && serverManager) {
                progress.report({ message: 'Connecting to API server…' });
                const running = await serverManager.ensureRunning();
                if (!running) {
                    vscode.window.showErrorMessage('RepoLens API server is not available. Start it manually or check settings.');
                    return;
                }
            }

            progress.report({ message: 'Starting analysis…' });

            let analyzeResp: AnalyzeResponse;
            try {
                analyzeResp = await api.analyzeRepository(repoUrl);
            } catch (err: any) {
                vscode.window.showErrorMessage(`Analysis failed: ${err.message}`);
                return;
            }

            const repoId = analyzeResp.repositoryId;

            // Poll for progress
            progress.report({ message: `Analyzing (${analyzeResp.status})…` });
            if (analyzeResp.status !== 'Completed') {
                await pollProgress(api, repoId, progress);
            }

            // Fetch overview + stats in parallel
            progress.report({ message: 'Loading results…' });
            let overview: RepositoryOverview | undefined;
            let stats: GraphStatsResponse | undefined;
            let architecture: ArchitectureResponse | undefined;

            try {
                [overview, stats, architecture] = await Promise.all([
                    api.getOverview(repoId),
                    api.getGraphStats(repoId).catch(() => undefined),
                    api.getArchitecture(repoId).catch(() => undefined),
                ]);
            } catch (err: any) {
                vscode.window.showErrorMessage(`Failed to load results: ${err.message}`);
                return;
            }

            // Store session
            session = {
                repoId,
                repoUrl,
                repoPath: localPath || '',
                overview,
                architecture,
                stats,
            };

            // Populate sidebar
            if (overview) {
                repoInfo.load(overview, stats);
            }
            await symbolTree.load(repoId, localPath || '');

            // Auto-show overview
            if (overview) {
                OverviewPanel.show(overview, stats);
            }

            vscode.window.showInformationMessage(
                `RepoLens: Analysis complete — ${overview?.totalFiles ?? '?'} files, ${overview?.totalLines ?? '?'} lines.`,
            );
        },
    );
}

async function pollProgress(
    api: RepoLensApi,
    repoId: string,
    progress: vscode.Progress<{ message?: string }>,
): Promise<void> {
    const start = Date.now();
    const maxMs = 5 * 60 * 1000; // 5 min timeout
    while (Date.now() - start < maxMs) {
        await sleep(1500);
        try {
            const p = await api.getProgress(repoId);
            progress.report({ message: `${p.stageLabel || p.stage} (${p.percentComplete}%)` });
            if (p.percentComplete >= 100) { return; }
        } catch {
            // progress endpoint may not exist yet
        }
    }
}

function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Resolves a relative file path from the repository into an absolute path
 * the user can open in the editor.
 */
function resolveFilePath(filePath: string, repoPath?: string): string | undefined {
    if (!filePath) { return undefined; }

    // If it's already absolute and exists, use it
    if (path.isAbsolute(filePath)) { return filePath; }

    // Try resolving relative to the repoPath (local clone)
    if (repoPath) {
        return path.resolve(repoPath, filePath);
    }

    // Try workspace folders
    const folders = vscode.workspace.workspaceFolders;
    if (folders && folders.length > 0) {
        return path.resolve(folders[0].uri.fsPath, filePath);
    }

    return undefined;
}

/**
 * Detect the remote URL from a local git repository.
 */
async function detectGitRemote(cwd: string): Promise<string | undefined> {
    try {
        const { execSync } = require('child_process');
        const result = execSync('git remote get-url origin', { cwd, encoding: 'utf8', timeout: 5000 });
        const url = result.trim();
        if (url) { return url; }
    } catch {
        // not a git repo or git not installed
    }
    return undefined;
}

/**
 * Normalize git remote URLs to HTTPS format.
 * Converts SSH (git@github.com:owner/repo.git) → https://github.com/owner/repo
 */
function normalizeGitUrl(url: string): string {
    let normalized = url.trim();
    // SSH: git@github.com:owner/repo.git
    const sshMatch = normalized.match(/^git@([^:]+):(.+?)(?:\.git)?$/);
    if (sshMatch) {
        return `https://${sshMatch[1]}/${sshMatch[2]}`;
    }
    // Strip trailing .git
    normalized = normalized.replace(/\.git$/, '');
    return normalized;
}

/**
 * Let the user pick a workspace folder if multiple are open.
 * Returns undefined if no folder is available or user cancelled.
 */
async function pickWorkspaceFolder(): Promise<vscode.WorkspaceFolder | undefined> {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
        vscode.window.showWarningMessage('No workspace folder open.');
        return undefined;
    }
    if (folders.length === 1) { return folders[0]; }

    const picked = await vscode.window.showQuickPick(
        folders.map((f) => ({ label: f.name, description: f.uri.fsPath, folder: f })),
        { placeHolder: 'Select workspace folder to analyze' }
    );
    return picked?.folder;
}

/**
 * On activation, if a workspace is open with a git remote, offer to analyze it.
 */
async function promptToAnalyzeWorkspace(
    api: RepoLensApi,
    symbolTree: SymbolTreeProvider,
    repoInfo: RepoInfoProvider,
): Promise<void> {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) { return; }

    const gitUrl = await detectGitRemote(folder.uri.fsPath);
    if (!gitUrl) { return; }

    const repoName = gitUrl.replace(/.*[/:]([^/]+\/[^/]+?)(?:\.git)?$/, '$1');
    const action = await vscode.window.showInformationMessage(
        `RepoLens detected repository "${repoName}". Analyze it?`,
        'Analyze',
        'Dismiss',
    );
    if (action === 'Analyze') {
        const httpsUrl = normalizeGitUrl(gitUrl);
        await analyzeRepository(api, symbolTree, repoInfo, httpsUrl, folder.uri.fsPath);
    }
}
