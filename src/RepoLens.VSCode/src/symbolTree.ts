import * as vscode from 'vscode';
import type { SearchResponse, SearchResult } from './types';
import { RepoLensApi } from './api';

/* ── Kind → ThemeIcon mapping ───────────────────────────── */
const kindIcon: Record<string, string> = {
    Class: 'symbol-class',
    Interface: 'symbol-interface',
    Method: 'symbol-method',
    Function: 'symbol-method',
    Property: 'symbol-property',
    Variable: 'symbol-variable',
    Namespace: 'symbol-namespace',
    Module: 'symbol-file',
    Import: 'package',
    File: 'file-code',
};

/**
 * Groups search results by file then shows symbols underneath.
 * Clicking a symbol navigates to its file:line in the editor.
 */
export class SymbolTreeProvider implements vscode.TreeDataProvider<SymbolTreeItem> {
    private _onDidChange = new vscode.EventEmitter<SymbolTreeItem | undefined>();
    readonly onDidChangeTreeData = this._onDidChange.event;

    private fileGroups: Map<string, SearchResult[]> = new Map();
    private repoId: string | undefined;
    private repoPath: string | undefined;

    constructor(private readonly api: RepoLensApi) { }

    /** Called after analysis completes. Loads all symbols and refreshes the tree. */
    async load(repoId: string, repoPath?: string): Promise<void> {
        this.repoId = repoId;
        this.repoPath = repoPath;
        this.fileGroups.clear();

        try {
            // Fetch a large batch of symbols (excluding imports for cleaner view)
            const kinds = ['Class', 'Interface', 'Function', 'Method', 'Property', 'Namespace', 'Module', 'Variable'];
            const res: SearchResponse = await this.api.search(repoId, '', kinds, 0, 2000);

            for (const r of res.results) {
                const key = r.filePath || '(unknown)';
                if (!this.fileGroups.has(key)) {
                    this.fileGroups.set(key, []);
                }
                this.fileGroups.get(key)!.push(r);
            }
        } catch (err: unknown) {
            const msg = err instanceof Error ? err.message : String(err);
            vscode.window.showWarningMessage(`RepoLens: could not load symbols — ${msg}`);
        }

        this._onDidChange.fire(undefined);
    }

    clear(): void {
        this.repoId = undefined;
        this.repoPath = undefined;
        this.fileGroups.clear();
        this._onDidChange.fire(undefined);
    }

    refresh(): void {
        if (this.repoId) {
            this.load(this.repoId, this.repoPath);
        }
    }

    getTreeItem(element: SymbolTreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: SymbolTreeItem): SymbolTreeItem[] {
        if (!this.repoId) {
            return [new SymbolTreeItem('Analyze a repository to see symbols', vscode.TreeItemCollapsibleState.None)];
        }

        if (!element) {
            // Root: file groups
            const items: SymbolTreeItem[] = [];
            for (const [filePath, results] of this.fileGroups) {
                const item = new SymbolTreeItem(
                    filePath,
                    vscode.TreeItemCollapsibleState.Collapsed,
                );
                item.iconPath = new vscode.ThemeIcon('file-code');
                item.description = `${results.length} symbols`;
                item.contextValue = 'file';
                item.filePath = filePath;
                item.repoPath = this.repoPath;
                items.push(item);
            }
            if (items.length === 0) {
                return [new SymbolTreeItem('No symbols found', vscode.TreeItemCollapsibleState.None)];
            }
            return items.sort((a, b) => a.label!.toString().localeCompare(b.label!.toString()));
        }

        // Children: symbols under a file
        if (element.filePath && this.fileGroups.has(element.filePath)) {
            return this.fileGroups.get(element.filePath)!.map((r) => {
                const item = new SymbolTreeItem(
                    r.symbol || r.snippet,
                    vscode.TreeItemCollapsibleState.None,
                );
                item.iconPath = new vscode.ThemeIcon(kindIcon[r.kind] || 'symbol-misc');
                item.description = `${r.kind} — line ${r.line}`;
                item.tooltip = `${r.symbol || r.snippet}\n${r.filePath}:${r.line}\nKind: ${r.kind}`;
                item.contextValue = 'symbol';
                item.filePath = r.filePath;
                item.repoPath = this.repoPath;
                item.line = r.line;

                // Navigate to file:line on click
                item.command = {
                    command: 'repolens.navigateToSymbol',
                    title: 'Go to Symbol',
                    arguments: [r.filePath, r.line, this.repoPath],
                };
                return item;
            });
        }

        return [];
    }
}

export class SymbolTreeItem extends vscode.TreeItem {
    filePath?: string;
    repoPath?: string;
    line?: number;
}
