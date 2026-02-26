import * as https from 'https';
import * as http from 'http';
import * as vscode from 'vscode';
import type {
    AnalyzeResponse,
    RepositoryOverview,
    ArchitectureResponse,
    SearchResponse,
    GraphStatsResponse,
    AnalysisProgress,
    PrImpactResponse,
    SuggestResponse,
} from './types';

/**
 * HTTP client for the RepoLens backend API.
 * Reads the server URL from VS Code settings.
 */
export class RepoLensApi {
    private getBaseUrl(): string {
        const config = vscode.workspace.getConfiguration('repolens');
        return config.get<string>('apiBaseUrl', 'https://www.repositorylens.com');
    }

    private getToken(): string | undefined {
        const config = vscode.workspace.getConfiguration('repolens');
        const token = config.get<string>('gitHubToken', '');
        return token || undefined;
    }

    private getOpenAiKey(): string | undefined {
        const config = vscode.workspace.getConfiguration('repolens');
        const key = config.get<string>('openAiApiKey', '');
        return key || undefined;
    }

    /* ── Generic HTTP helpers ─────────────────────────────── */

    private request<T>(method: string, path: string, body?: unknown): Promise<T> {
        return new Promise((resolve, reject) => {
            const base = this.getBaseUrl();
            const url = new URL(path, base);
            const isHttps = url.protocol === 'https:';
            const lib = isHttps ? https : http;

            const payload = body ? JSON.stringify(body) : undefined;
            const options: http.RequestOptions = {
                method,
                hostname: url.hostname,
                port: url.port,
                path: url.pathname + url.search,
                headers: {
                    'Accept': 'application/json',
                    ...(payload ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) } : {}),
                },
            };

            const req = lib.request(options, (res) => {
                let data = '';
                res.on('data', (chunk: string) => (data += chunk));
                res.on('end', () => {
                    if (res.statusCode && res.statusCode >= 200 && res.statusCode < 300) {
                        try {
                            resolve(JSON.parse(data) as T);
                        } catch {
                            reject(new Error(`Invalid JSON response from ${path}`));
                        }
                    } else {
                        let msg = data;
                        try {
                            const parsed = JSON.parse(data);
                            if (typeof parsed === 'string') { msg = parsed; }
                        } catch { /* keep raw */ }
                        reject(new Error(`API ${res.statusCode}: ${msg}`));
                    }
                });
            });

            req.on('error', (err) => reject(new Error(`Cannot reach RepoLens API at ${base}: ${err.message}`)));
            if (payload) { req.write(payload); }
            req.end();
        });
    }

    private get<T>(path: string): Promise<T> {
        return this.request<T>('GET', path);
    }

    private post<T>(path: string, body: unknown): Promise<T> {
        return this.request<T>('POST', path, body);
    }

    /* ── API methods ──────────────────────────────────────── */

    async analyzeRepository(repoUrl: string, forceReanalyze = false): Promise<AnalyzeResponse> {
        const body: Record<string, unknown> = { repositoryUrl: repoUrl };
        const token = this.getToken();
        if (token) { body.gitHubToken = token; }
        const key = this.getOpenAiKey();
        if (key) { body.openAiApiKey = key; }
        if (forceReanalyze) { body.forceReanalyze = true; }
        return this.post<AnalyzeResponse>('/api/repository/analyze', body);
    }

    async getOverview(repoId: string): Promise<RepositoryOverview> {
        return this.get<RepositoryOverview>(`/api/repository/${repoId}/overview`);
    }

    async getArchitecture(repoId: string): Promise<ArchitectureResponse> {
        return this.get<ArchitectureResponse>(`/api/repository/${repoId}/architecture`);
    }

    async getGraphStats(repoId: string): Promise<GraphStatsResponse> {
        return this.get<GraphStatsResponse>(`/api/repository/${repoId}/architecture/stats`);
    }

    async search(repoId: string, query: string, kinds?: string[], skip = 0, take = 50): Promise<SearchResponse> {
        const params = new URLSearchParams({ q: query, skip: String(skip), take: String(take) });
        if (kinds && kinds.length > 0) { params.set('kinds', kinds.join(',')); }
        return this.get<SearchResponse>(`/api/repository/${repoId}/search?${params}`);
    }

    async suggest(repoId: string, prefix: string): Promise<SuggestResponse> {
        return this.get<SuggestResponse>(`/api/repository/${repoId}/suggest?q=${encodeURIComponent(prefix)}`);
    }

    async getProgress(repoId: string): Promise<AnalysisProgress> {
        return this.get<AnalysisProgress>(`/api/repository/${repoId}/progress`);
    }

    async analyzePrImpact(repoId: string, prNumber: number): Promise<PrImpactResponse> {
        const body: Record<string, unknown> = { prNumber };
        const token = this.getToken();
        if (token) { body.gitHubToken = token; }
        return this.post<PrImpactResponse>(`/api/repository/${repoId}/pr-impact`, body);
    }
}
