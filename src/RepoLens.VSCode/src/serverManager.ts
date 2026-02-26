import * as vscode from 'vscode';
import * as http from 'http';
import * as path from 'path';
import { ChildProcess, spawn } from 'child_process';

/**
 * Manages the RepoLens API server lifecycle.
 *
 * On first API call the extension checks whether the server is reachable.
 * If not, it attempts to start it automatically via `dotnet run` (requires
 * the .NET 8 SDK to be installed). The server is stopped when the extension
 * deactivates.
 *
 * The discovery order for the API project is:
 *   1. Explicit setting  `repolens.apiProjectPath`
 *   2. Adjacent to the extension source: `<workspace>/src/RepoLens.Api`
 *   3. Prompt the user to locate it manually
 */
export class ApiServerManager implements vscode.Disposable {
    private process: ChildProcess | undefined;
    private outputChannel: vscode.OutputChannel;
    private starting = false;

    constructor() {
        this.outputChannel = vscode.window.createOutputChannel('RepoLens API');
    }

    /* ── Public API ──────────────────────────────────────── */

    /**
     * Ensure the API is reachable. Starts it automatically if needed.
     * Returns `true` if the server is (now) available.
     */
    async ensureRunning(): Promise<boolean> {
        if (await this.ping()) { return true; }

        // Already trying to start
        if (this.starting) {
            return this.waitForReady(30_000);
        }

        const projectPath = await this.resolveProjectPath();
        if (!projectPath) { return false; }

        return this.startServer(projectPath);
    }

    dispose(): void {
        this.stopServer();
        this.outputChannel.dispose();
    }

    /* ── Server lifecycle ────────────────────────────────── */

    private async startServer(projectDir: string): Promise<boolean> {
        this.starting = true;

        // Verify dotnet SDK is available
        const hasDotnet = await this.hasDotnetSdk();
        if (!hasDotnet) {
            const action = await vscode.window.showErrorMessage(
                'RepoLens requires the .NET 8 SDK to auto-start the API server. Install it or start the server manually.',
                'Download .NET',
                'Enter server URL',
            );
            this.starting = false;
            if (action === 'Download .NET') {
                vscode.env.openExternal(vscode.Uri.parse('https://dotnet.microsoft.com/download/dotnet/8.0'));
            } else if (action === 'Enter server URL') {
                await this.promptForServerUrl();
            }
            return false;
        }

        return vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'RepoLens: Starting API server…', cancellable: true },
            async (progress, token) => {
                const baseUrl = this.getBaseUrl();
                const port = new URL(baseUrl).port || '5000';

                this.outputChannel.appendLine(`Starting API server from: ${projectDir}`);
                this.outputChannel.appendLine(`Port: ${port}`);

                this.process = spawn('dotnet', ['run', '--no-launch-profile', '--urls', `http://localhost:${port}`], {
                    cwd: projectDir,
                    shell: true,
                    stdio: ['ignore', 'pipe', 'pipe'],
                    env: { ...process.env, DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_URLS: `http://localhost:${port}` },
                });

                this.process.stdout?.on('data', (data: Buffer) => {
                    this.outputChannel.append(data.toString());
                });
                this.process.stderr?.on('data', (data: Buffer) => {
                    this.outputChannel.append(data.toString());
                });
                this.process.on('exit', (code) => {
                    this.outputChannel.appendLine(`API server exited with code ${code}`);
                    this.process = undefined;
                    this.starting = false;
                });

                token.onCancellationRequested(() => {
                    this.stopServer();
                    this.starting = false;
                });

                const ready = await this.waitForReady(30_000);
                this.starting = false;

                if (ready) {
                    this.outputChannel.appendLine('API server is ready.');
                    vscode.window.showInformationMessage('RepoLens API server started successfully.');
                } else if (!token.isCancellationRequested) {
                    this.outputChannel.show();
                    vscode.window.showErrorMessage(
                        'RepoLens API server failed to start. Check the "RepoLens API" output channel for details.',
                    );
                }

                return ready;
            },
        );
    }

    stopServer(): void {
        if (this.process) {
            this.outputChannel.appendLine('Stopping API server…');
            // On Windows, need to kill the tree since shell: true wraps in cmd
            try {
                if (this.process.pid) {
                    spawn('taskkill', ['/pid', String(this.process.pid), '/T', '/F'], { shell: true });
                }
            } catch {
                this.process.kill();
            }
            this.process = undefined;
        }
    }

    /* ── Discovery ───────────────────────────────────────── */

    private async resolveProjectPath(): Promise<string | undefined> {
        const config = vscode.workspace.getConfiguration('repolens');

        // 1. Explicit setting
        const explicit = config.get<string>('apiProjectPath', '').trim();
        if (explicit && await this.directoryExists(explicit)) {
            return explicit;
        }

        // 2. Search workspace folders for src/RepoLens.Api
        const folders = vscode.workspace.workspaceFolders ?? [];
        for (const f of folders) {
            const candidate = path.join(f.uri.fsPath, 'src', 'RepoLens.Api');
            if (await this.directoryExists(candidate)) {
                return candidate;
            }
            // Also try directly in workspace root
            const candidate2 = path.join(f.uri.fsPath, 'RepoLens.Api');
            if (await this.directoryExists(candidate2)) {
                return candidate2;
            }
        }

        // 3. Ask the user
        const action = await vscode.window.showWarningMessage(
            'RepoLens cannot find the API project. Would you like to locate it or enter a remote server URL?',
            'Browse for API project',
            'Enter server URL',
            'Cancel',
        );

        if (action === 'Browse for API project') {
            const uris = await vscode.window.showOpenDialog({
                canSelectFolders: true,
                canSelectFiles: false,
                openLabel: 'Select RepoLens.Api folder',
                title: 'Locate the RepoLens.Api project directory',
            });
            if (uris && uris.length > 0) {
                const selected = uris[0].fsPath;
                // Save for future use
                await config.update('apiProjectPath', selected, vscode.ConfigurationTarget.Global);
                return selected;
            }
        } else if (action === 'Enter server URL') {
            await this.promptForServerUrl();
        }

        return undefined;
    }

    /* ── Utilities ───────────────────────────────────────── */

    private getBaseUrl(): string {
        return vscode.workspace.getConfiguration('repolens').get<string>('apiBaseUrl', 'https://www.repositorylens.com');
    }

    private async promptForServerUrl(): Promise<void> {
        const url = await vscode.window.showInputBox({
            prompt: 'Enter the URL of a running RepoLens API server',
            placeHolder: 'http://localhost:5000',
            value: this.getBaseUrl(),
        });
        if (url) {
            await vscode.workspace.getConfiguration('repolens').update('apiBaseUrl', url.trim(), vscode.ConfigurationTarget.Global);
            vscode.window.showInformationMessage(`RepoLens API URL set to ${url.trim()}`);
        }
    }

    /** Health-check ping to the API. */
    ping(): Promise<boolean> {
        return new Promise((resolve) => {
            const base = this.getBaseUrl();
            try {
                const url = new URL('/api/repository/health', base);
                const req = http.get(url.href, { timeout: 2000 }, (res) => {
                    resolve(res.statusCode !== undefined && res.statusCode < 500);
                    res.resume(); // drain
                });
                req.on('error', () => resolve(false));
                req.on('timeout', () => { req.destroy(); resolve(false); });
            } catch {
                resolve(false);
            }
        });
    }

    /** Wait up to `timeoutMs` for the server to become reachable. */
    private async waitForReady(timeoutMs: number): Promise<boolean> {
        const start = Date.now();
        while (Date.now() - start < timeoutMs) {
            if (await this.ping()) { return true; }
            await new Promise((r) => setTimeout(r, 1000));
        }
        return false;
    }

    private hasDotnetSdk(): Promise<boolean> {
        return new Promise((resolve) => {
            const proc = spawn('dotnet', ['--version'], { shell: true, stdio: 'ignore' });
            proc.on('error', () => resolve(false));
            proc.on('exit', (code) => resolve(code === 0));
        });
    }

    private async directoryExists(dirPath: string): Promise<boolean> {
        try {
            const stat = await vscode.workspace.fs.stat(vscode.Uri.file(dirPath));
            return (stat.type & vscode.FileType.Directory) !== 0;
        } catch {
            return false;
        }
    }
}
