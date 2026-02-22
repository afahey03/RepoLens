import type {
    AnalyzeRequest,
    AnalyzeResponse,
    RepositoryOverview,
    ArchitectureResponse,
    SearchResponse,
} from './types';

const BASE_URL = '/api/repository';

export async function analyzeRepository(url: string): Promise<AnalyzeResponse> {
    const body: AnalyzeRequest = { repositoryUrl: url };
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

export async function searchRepository(repoId: string, query: string): Promise<SearchResponse> {
    const res = await fetch(`${BASE_URL}/${repoId}/search?q=${encodeURIComponent(query)}`);
    if (!res.ok) throw new Error(`Search failed: ${res.statusText}`);
    return res.json();
}
