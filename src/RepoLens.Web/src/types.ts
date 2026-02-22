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
    kind: string;
}

export interface SearchResponse {
    query: string;
    totalResults: number;
    skip: number;
    take: number;
    availableKinds: string[];
    results: SearchResult[];
}

export interface SearchSuggestion {
    text: string;
    kind: string;
    filePath: string;
}

export interface SuggestResponse {
    prefix: string;
    suggestions: SearchSuggestion[];
}

export interface GraphStatsResponse {
    totalNodes: number;
    totalEdges: number;
    nodeTypeCounts: Record<string, number>;
    edgeTypeCounts: Record<string, number>;
    maxDepth: number;
    rootNodes: string[];
    leafNodes: string[];
}

export interface AnalysisProgress {
    repositoryId: string;
    stage: string;
    stageLabel: string;
    percentComplete: number;
    error?: string;
}
