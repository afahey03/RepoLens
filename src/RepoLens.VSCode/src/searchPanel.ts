import * as vscode from 'vscode';
import { RepoLensApi } from './api';
import type { SearchResult } from './types';
import { getWebviewContent } from './webviewUtil';

/**
 * Webview panel with a search box, results list, and click-to-navigate.
 * Calls the RepoLens backend search / suggest endpoints.
 */
export class SearchPanel {
    private static current: SearchPanel | undefined;
    private readonly panel: vscode.WebviewPanel;
    private readonly api: RepoLensApi;
    private repoId: string;
    private repoPath: string;
    private disposed = false;

    private constructor(panel: vscode.WebviewPanel, api: RepoLensApi, repoId: string, repoPath: string) {
        this.panel = panel;
        this.api = api;
        this.repoId = repoId;
        this.repoPath = repoPath;

        panel.onDidDispose(() => {
            this.disposed = true;
            SearchPanel.current = undefined;
        });

        panel.webview.onDidReceiveMessage(async (msg) => {
            if (msg.command === 'search') {
                await this.doSearch(msg.query, msg.kinds);
            } else if (msg.command === 'navigate') {
                vscode.commands.executeCommand(
                    'repolens.navigateToSymbol',
                    msg.filePath,
                    msg.line || 1,
                    this.repoPath
                );
            }
        });
    }

    static show(api: RepoLensApi, repoId: string, repoPath: string): void {
        if (SearchPanel.current && !SearchPanel.current.disposed) {
            SearchPanel.current.repoId = repoId;
            SearchPanel.current.repoPath = repoPath;
            SearchPanel.current.panel.reveal();
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            'repolens.search',
            'RepoLens: Search',
            vscode.ViewColumn.One,
            { enableScripts: true, retainContextWhenHidden: true }
        );

        SearchPanel.current = new SearchPanel(panel, api, repoId, repoPath);
        SearchPanel.current.renderEmpty();
    }

    private renderEmpty(): void {
        const body = `
        <h1>Search Symbols</h1>
        <div style="display:flex;gap:8px;margin-bottom:12px;">
            <input id="q" type="search" placeholder="Search for classes, methods, functions…" autofocus />
            <button id="searchBtn">Search</button>
        </div>
        <div style="margin-bottom:12px;">
            <label class="muted" style="margin-right:6px;">Filter:</label>
            <label><input type="checkbox" class="kindFilter" value="Class" /> Class</label>
            <label><input type="checkbox" class="kindFilter" value="Interface" /> Interface</label>
            <label><input type="checkbox" class="kindFilter" value="Method" /> Method</label>
            <label><input type="checkbox" class="kindFilter" value="Function" /> Function</label>
            <label><input type="checkbox" class="kindFilter" value="Property" /> Property</label>
        </div>
        <div id="results"></div>
        `;

        const script = `
(function() {
    const vscode = acquireVsCodeApi();
    const qInput = document.getElementById('q');
    const searchBtn = document.getElementById('searchBtn');
    const resultsDiv = document.getElementById('results');

    function getKinds() {
        return Array.from(document.querySelectorAll('.kindFilter:checked')).map(c => c.value);
    }

    function doSearch() {
        const q = qInput.value.trim();
        if (!q) return;
        resultsDiv.innerHTML = '<p class="muted">Searching…</p>';
        vscode.postMessage({ command: 'search', query: q, kinds: getKinds() });
    }
    searchBtn.addEventListener('click', doSearch);
    qInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') doSearch(); });

    window.addEventListener('message', (e) => {
        const msg = e.data;
        if (msg.command === 'results') {
            const results = msg.results;
            if (!results || results.length === 0) {
                resultsDiv.innerHTML = '<p class="muted">No results found.</p>';
                return;
            }
            resultsDiv.innerHTML = '<table><tr><th>Symbol</th><th>Kind</th><th>File</th><th>Line</th><th>Score</th></tr>' +
                results.map(r =>
                    '<tr class="result-row" data-file="' + esc(r.filePath) + '" data-line="' + r.line + '" style="cursor:pointer;">' +
                    '<td><strong>' + esc(r.name) + '</strong></td>' +
                    '<td>' + esc(r.kind) + '</td>' +
                    '<td class="muted">' + esc(r.filePath) + '</td>' +
                    '<td>' + r.line + '</td>' +
                    '<td>' + r.score.toFixed(2) + '</td>' +
                    '</tr>'
                ).join('') +
                '</table>';

            document.querySelectorAll('.result-row').forEach(row => {
                row.addEventListener('click', () => {
                    vscode.postMessage({
                        command: 'navigate',
                        filePath: row.getAttribute('data-file'),
                        line: parseInt(row.getAttribute('data-line'), 10)
                    });
                });
            });
        }
    });

    function esc(s) { return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
})();
        `;

        this.panel.webview.html = getWebviewContent(this.panel.webview, 'Search', body, script);
    }

    private async doSearch(query: string, kinds: string[]): Promise<void> {
        try {
            const resp = await this.api.search(this.repoId, query, kinds.length > 0 ? kinds : undefined, 50);
            this.panel.webview.postMessage({ command: 'results', results: resp.results });
        } catch (err: any) {
            this.panel.webview.postMessage({ command: 'results', results: [] });
            vscode.window.showErrorMessage(`Search failed: ${err.message}`);
        }
    }
}
