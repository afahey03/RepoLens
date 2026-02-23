import * as vscode from 'vscode';
import type { RepositoryOverview, GraphStatsResponse } from './types';

/**
 * Shows a read-only tree of key repository info (languages, frameworks, stats).
 */
export class RepoInfoProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private _onDidChange = new vscode.EventEmitter<vscode.TreeItem | undefined>();
    readonly onDidChangeTreeData = this._onDidChange.event;

    private overview: RepositoryOverview | undefined;
    private stats: GraphStatsResponse | undefined;

    load(overview: RepositoryOverview, stats?: GraphStatsResponse): void {
        this.overview = overview;
        this.stats = stats;
        this._onDidChange.fire(undefined);
    }

    clear(): void {
        this.overview = undefined;
        this.stats = undefined;
        this._onDidChange.fire(undefined);
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: vscode.TreeItem): vscode.TreeItem[] {
        if (!this.overview) {
            return [new vscode.TreeItem('No repository analyzed yet')];
        }
        const ov = this.overview;

        if (!element) {
            // Root categories
            return [
                this.makeSection('$(info) Overview', 'overview'),
                this.makeSection('$(code) Languages', 'languages'),
                this.makeSection('$(symbol-class) Symbols', 'symbols'),
                this.makeSection('$(tools) Frameworks', 'frameworks'),
                this.makeSection('$(package) Dependencies', 'dependencies'),
                ...(this.stats ? [this.makeSection('$(type-hierarchy) Graph Stats', 'graphStats')] : []),
            ];
        }

        const ctx = (element as SectionItem).sectionId;

        if (ctx === 'overview') {
            return [
                this.leaf(`Name: ${ov.name}`),
                this.leaf(`Files: ${ov.totalFiles.toLocaleString()}`),
                this.leaf(`Lines: ${ov.totalLines.toLocaleString()}`),
                this.leaf(`Complexity: ${ov.complexity}`),
                this.leaf(`Summary source: ${ov.summarySource}`),
                ...(ov.entryPoints.length > 0 ? [this.leaf(`Entry points: ${ov.entryPoints.join(', ')}`)] : []),
            ];
        }

        if (ctx === 'languages') {
            const entries = Object.entries(ov.languageLineBreakdown).sort(([, a], [, b]) => b - a);
            const totalLangLines = entries.reduce((sum, [, lines]) => sum + lines, 0);
            return entries
                .map(([lang, lines]) => {
                    const pct = totalLangLines > 0 ? ((lines / totalLangLines) * 100).toFixed(1) : '0';
                    return this.leaf(`${lang}: ${lines.toLocaleString()} lines (${pct}%)`);
                });
        }

        if (ctx === 'symbols') {
            return Object.entries(ov.symbolCounts)
                .sort(([, a], [, b]) => b - a)
                .map(([kind, count]) => this.leaf(`${kind}: ${count.toLocaleString()}`));
        }

        if (ctx === 'frameworks') {
            if (ov.detectedFrameworks.length === 0) { return [this.leaf('(none detected)')]; }
            return ov.detectedFrameworks.map((f) => this.leaf(f));
        }

        if (ctx === 'dependencies') {
            if (ov.externalDependencies.length === 0) { return [this.leaf('(none detected)')]; }
            return ov.externalDependencies.slice(0, 50).map((d) => this.leaf(d));
        }

        if (ctx === 'graphStats' && this.stats) {
            const s = this.stats;
            return [
                this.leaf(`Nodes: ${s.totalNodes.toLocaleString()}`),
                this.leaf(`Edges: ${s.totalEdges.toLocaleString()}`),
                this.leaf(`Max depth: ${s.maxDepth}`),
                ...Object.entries(s.nodeTypeCounts).map(([t, c]) => this.leaf(`  ${t}: ${c}`)),
            ];
        }

        return [];
    }

    private makeSection(label: string, id: string): SectionItem {
        const item = new SectionItem(label, vscode.TreeItemCollapsibleState.Expanded);
        item.sectionId = id;
        return item;
    }

    private leaf(text: string): vscode.TreeItem {
        return new vscode.TreeItem(text, vscode.TreeItemCollapsibleState.None);
    }
}

class SectionItem extends vscode.TreeItem {
    sectionId = '';
}
