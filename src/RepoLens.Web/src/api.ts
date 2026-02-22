import type {
    AnalyzeRequest,
    AnalyzeResponse,
    RepositoryOverview,
    ArchitectureResponse,
    SearchResponse,
    GraphStatsResponse,
    SuggestResponse,
    AnalysisProgress,
} from './types';

const BASE_URL = '/api/repository';

export async function analyzeRepository(url: string, gitHubToken?: string): Promise<AnalyzeResponse> {
    const body: AnalyzeRequest = { repositoryUrl: url };
    if (gitHubToken) body.gitHubToken = gitHubToken;
    const res = await fetch(`${BASE_URL}/analyze`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`Analysis failed: ${res.statusText}`);
    return res.json();
}

export async function getOverview(repoId: string): Promise<RepositoryOverview> {
    const res = await fetch(`${BASE_URL}/${repoId}/overview`);
    if (!res.ok) throw new Error(`Failed to fetch overview: ${res.statusText}`);
    return res.json();
}

export async function getArchitecture(repoId: string): Promise<ArchitectureResponse> {
    const res = await fetch(`${BASE_URL}/${repoId}/architecture`);
    if (!res.ok) throw new Error(`Failed to fetch architecture: ${res.statusText}`);
    return res.json();
}

export interface SearchOptions {
    kinds?: string[];
    skip?: number;
    take?: number;
}

export async function searchRepository(
    repoId: string,
    query: string,
    options: SearchOptions = {},
): Promise<SearchResponse> {
    const params = new URLSearchParams({ q: query });
    if (options.kinds && options.kinds.length > 0) {
        params.set('kinds', options.kinds.join(','));
    }
    if (options.skip !== undefined) params.set('skip', String(options.skip));
    if (options.take !== undefined) params.set('take', String(options.take));

    const res = await fetch(`${BASE_URL}/${repoId}/search?${params}`);
    if (!res.ok) throw new Error(`Search failed: ${res.statusText}`);
    return res.json();
}

export async function getSuggestions(repoId: string, prefix: string): Promise<SuggestResponse> {
    const res = await fetch(`${BASE_URL}/${repoId}/suggest?q=${encodeURIComponent(prefix)}`);
    if (!res.ok) throw new Error(`Suggest failed: ${res.statusText}`);
    return res.json();
}

export async function getGraphStats(repoId: string): Promise<GraphStatsResponse> {
    const res = await fetch(`${BASE_URL}/${repoId}/architecture/stats`);
    if (!res.ok) throw new Error(`Failed to fetch graph stats: ${res.statusText}`);
    return res.json();
}

export async function getProgress(repoId: string): Promise<AnalysisProgress> {
    const res = await fetch(`${BASE_URL}/${repoId}/progress`);
    if (!res.ok) throw new Error(`Failed to fetch progress: ${res.statusText}`);
    return res.json();
}
