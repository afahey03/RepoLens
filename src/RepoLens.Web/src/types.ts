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
    languageLineBreakdown: Record<string, number>;
    totalFiles: number;
    totalLines: number;
    detectedFrameworks: string[];
    entryPoints: string[];
    topLevelFolders: string[];
    symbolCounts: Record<string, number>;
    keyTypes: KeyTypeInfo[];
    mostConnectedModules: ConnectedModuleInfo[];
    externalDependencies: string[];
    summary: string;
    complexity: string;
}

export interface KeyTypeInfo {
    name: string;
    filePath: string;
    kind: string;
    memberCount: number;
}

export interface ConnectedModuleInfo {
    name: string;
    filePath: string;
    incomingEdges: number;
    outgoingEdges: number;
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
