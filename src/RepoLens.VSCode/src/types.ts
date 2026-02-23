/* ── API types matching the RepoLens backend DTOs ─────────── */

export interface AnalyzeRequest {
    repositoryUrl: string;
    gitHubToken?: string;
    openAiApiKey?: string;
    forceReanalyze?: boolean;
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
    summarySource: string;
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
    filePath?: string;
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

export interface PrImpactRequest {
    prNumber: number;
    gitHubToken?: string;
}

export interface PrImpactResponse {
    prNumber: number;
    totalFilesChanged: number;
    totalAdditions: number;
    totalDeletions: number;
    changedFiles: PrFileImpact[];
    affectedSymbols: PrSymbolImpact[];
    affectedEdges: PrEdgeImpact[];
    downstreamFiles: string[];
    languagesTouched: string[];
}

export interface PrFileImpact {
    filePath: string;
    status: string;
    additions: number;
    deletions: number;
    language: string | null;
    previousFilePath: string | null;
    symbolCount: number;
}

export interface PrSymbolImpact {
    name: string;
    kind: string;
    filePath: string;
    line: number;
    parentSymbol: string | null;
}

export interface PrEdgeImpact {
    source: string;
    target: string;
    relationship: string;
    impactSide: string;
}
