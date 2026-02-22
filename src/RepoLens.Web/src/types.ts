/* API types matching the backend DTOs */

export interface AnalyzeRequest {
    repositoryUrl: string;
}

export interface AnalyzeResponse {
    repositoryId: string;
    status: string;
}

export interface RepositoryOverview {
    name: string;
    url: string;
    languageBreakdown: Record<string, number>;
    totalFiles: number;
    totalLines: number;
    detectedFrameworks: string[];
    entryPoints: string[];
    topLevelFolders: string[];
}

export interface GraphNodeDto {
    id: string;
    name: string;
    type: string;
    metadata: Record<string, string>;
}

export interface GraphEdgeDto {
    source: string;
    target: string;
    relationship: string;
}

export interface ArchitectureResponse {
    nodes: GraphNodeDto[];
    edges: GraphEdgeDto[];
}

export interface SearchResult {
    filePath: string;
    symbol: string | null;
    snippet: string;
    score: number;
    line: number;
}

export interface SearchResponse {
    query: string;
    totalResults: number;
    results: SearchResult[];
}
