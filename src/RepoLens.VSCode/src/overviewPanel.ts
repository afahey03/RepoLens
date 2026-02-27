import * as vscode from 'vscode';
import type { RepositoryOverview, GraphStatsResponse } from './types';
import { getWebviewContent } from './webviewUtil';

const LANGUAGE_COLORS: Record<string, string> = {
    'C#': '#178600',
    'TypeScript': '#3178c6',
    'JavaScript': '#f1e05a',
    'Python': '#3572A5',
    'Java': '#b07219',
    'Go': '#00ADD8',
    'C': '#555555',
    'C++': '#f34b7d',
    'Swift': '#F05138',
    'Rust': '#dea584',
    'Kotlin': '#A97BFF',
    'Scala': '#c22d40',
    'PHP': '#4F5D95',
    'Ruby': '#701516',
    'Dart': '#00B4AB',
    'Lua': '#000080',
    'Perl': '#0298c3',
    'R': '#198CE7',
    'Haskell': '#5e5086',
    'Elixir': '#6e4a7e',
    'SQL': '#e38c00',
    'HTML': '#e34c26',
    'CSS': '#563d7c',
    'JSON': '#292929',
    'YAML': '#cb171e',
    'Markdown': '#083fa1',
};

function color(lang: string): string {
    return LANGUAGE_COLORS[lang] ?? '#888';
}

export class OverviewPanel {
    private static current: OverviewPanel | undefined;
    private readonly panel: vscode.WebviewPanel;
    private disposed = false;

    private constructor(panel: vscode.WebviewPanel) {
        this.panel = panel;
        panel.onDidDispose(() => {
            this.disposed = true;
            OverviewPanel.current = undefined;
        });
    }

    static show(overview: RepositoryOverview, stats?: GraphStatsResponse): void {
        if (OverviewPanel.current && !OverviewPanel.current.disposed) {
            OverviewPanel.current.panel.reveal();
            OverviewPanel.current.render(overview, stats);
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            'repolens.overview',
            `RepoLens: ${overview.name}`,
            vscode.ViewColumn.One,
            { enableScripts: true }
        );

        OverviewPanel.current = new OverviewPanel(panel);
        OverviewPanel.current.render(overview, stats);
    }

    private render(ov: RepositoryOverview, stats?: GraphStatsResponse): void {
        // ── Language bar ──
        const sorted = Object.entries(ov.languageLineBreakdown).sort(([, a], [, b]) => b - a);
        const totalLangLines = sorted.reduce((sum, [, lines]) => sum + lines, 0) || 1;
        const barSegments = sorted
            .map(([lang, lines]) => {
                const pct = ((lines / totalLangLines) * 100).toFixed(1);
                return `<div style="width:${pct}%; background:${color(lang)};" title="${lang} ${pct}%"></div>`;
            })
            .join('');
        const langList = sorted
            .map(([lang, lines]) => {
                const pct = ((lines / totalLangLines) * 100).toFixed(1);
                return `<span><span class="bar" style="width:10px;background:${color(lang)};"></span>${lang}: ${lines.toLocaleString()} (${pct}%)</span>`;
            })
            .join(' &nbsp;&middot;&nbsp; ');

        // ── Key types ──
        const keyTypes = ov.keyTypes
            .slice(0, 10)
            .map(
                (kt) => `<tr>
                    <td><strong>${escHtml(kt.name)}</strong></td>
                    <td>${kt.kind}</td>
                    <td>${kt.memberCount} members</td>
                    <td class="muted">${escHtml(kt.filePath)}</td>
                </tr>`
            )
            .join('');

        // ── Connected modules ──
        const modules = ov.mostConnectedModules
            .slice(0, 10)
            .map(
                (m: import('./types').ConnectedModuleInfo) => `<tr>
                    <td><strong>${escHtml(m.name)}</strong></td>
                    <td class="muted">${escHtml(m.filePath)}</td>
                    <td>${m.incomingEdges} in / ${m.outgoingEdges} out</td>
                </tr>`
            )
            .join('');

        // ── Symbol counts ──
        const symbolRows = Object.entries(ov.symbolCounts)
            .sort(([, a], [, b]) => b - a)
            .map(([k, v]) => `<span class="badge">${k}: ${v.toLocaleString()}</span>`)
            .join(' ');

        // ── Graph stats ──
        let graphHtml = '';
        if (stats) {
            const types = Object.entries(stats.nodeTypeCounts)
                .map(([t, c]) => `<span class="badge">${t}: ${c}</span>`)
                .join(' ');
            graphHtml = `
            <div class="card">
                <h2>Dependency Graph</h2>
                <div class="grid grid-3">
                    <div><span class="stat-value">${stats.totalNodes.toLocaleString()}</span><br/><span class="stat-label">Nodes</span></div>
                    <div><span class="stat-value">${stats.totalEdges.toLocaleString()}</span><br/><span class="stat-label">Edges</span></div>
                    <div><span class="stat-value">${stats.maxDepth}</span><br/><span class="stat-label">Max Depth</span></div>
                </div>
                <div style="margin-top:8px;">${types}</div>
                <button id="openGraph" style="margin-top:12px;padding:6px 16px;border:1px solid var(--vscode-button-border,#555);background:var(--vscode-button-background,#0e639c);color:var(--vscode-button-foreground,#fff);border-radius:4px;cursor:pointer;font-size:0.9em;">View Architecture Graph</button>
            </div>`;
        }

        const body = `
        <h1>$(repo) ${escHtml(ov.name)}</h1>

        <div class="grid grid-3">
            <div class="card">
                <span class="stat-value">${ov.totalFiles.toLocaleString()}</span><br/>
                <span class="stat-label">Files</span>
            </div>
            <div class="card">
                <span class="stat-value">${ov.totalLines.toLocaleString()}</span><br/>
                <span class="stat-label">Lines of Code</span>
            </div>
            <div class="card">
                <span class="stat-value">${ov.complexity}</span><br/>
                <span class="stat-label">Complexity</span>
            </div>
        </div>

        <div class="card">
            <h2>Languages</h2>
            <div class="bar-container">${barSegments}</div>
            <div style="margin-top:6px;font-size:0.9em;">${langList}</div>
        </div>

        ${ov.summary ? `<div class="card"><h2>Summary</h2><p>${escHtml(ov.summary)}</p></div>` : ''}

        <div class="card">
            <h2>Symbols</h2>
            ${symbolRows}
        </div>

        ${ov.detectedFrameworks.length > 0 ? `
        <div class="card">
            <h2>Frameworks</h2>
            ${ov.detectedFrameworks.map((f) => `<span class="badge">${escHtml(f)}</span>`).join(' ')}
        </div>` : ''}

        ${keyTypes ? `
        <div class="card">
            <h2>Key Types</h2>
            <table>
                <tr><th>Name</th><th>Kind</th><th>Dependencies</th><th>File</th></tr>
                ${keyTypes}
            </table>
        </div>` : ''}

        ${modules ? `
        <div class="card">
            <h2>Connected Modules</h2>
            <table>
                <tr><th>Module</th><th>File</th><th>Edges</th></tr>
                ${modules}
            </table>
        </div>` : ''}

        ${graphHtml}

        ${ov.externalDependencies.length > 0 ? `
        <div class="card">
            <h2>External Dependencies</h2>
            ${ov.externalDependencies.slice(0, 40).map((d) => `<span class="badge">${escHtml(d)}</span>`).join(' ')}
            ${ov.externalDependencies.length > 40 ? `<br/><span class="muted">… and ${ov.externalDependencies.length - 40} more</span>` : ''}
        </div>` : ''}

        <div class="card muted" style="font-size:0.85em;">
            Summary source: ${ov.summarySource}
            ${ov.entryPoints.length > 0 ? ` &nbsp;|&nbsp; Entry points: ${ov.entryPoints.map(escHtml).join(', ')}` : ''}
        </div>
        `;

        const script = `
(function() {
    const vscode = acquireVsCodeApi();
    const btn = document.getElementById('openGraph');
    if (btn) {
        btn.addEventListener('click', () => {
            vscode.postMessage({ command: 'showArchitecture' });
        });
    }
})();
        `;

        this.panel.webview.html = getWebviewContent(this.panel.webview, `RepoLens: ${ov.name}`, body, script);

        this.panel.webview.onDidReceiveMessage((msg) => {
            if (msg.command === 'showArchitecture') {
                vscode.commands.executeCommand('repolens.showArchitecture');
            }
        });
    }
}

function escHtml(text: string): string {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
