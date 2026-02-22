import { useState } from 'react';
import { analyzeRepository, getOverview, getArchitecture, getGraphStats, searchRepository } from './api';
import type { RepositoryOverview, ArchitectureResponse, SearchResponse, GraphStatsResponse } from './types';
import OverviewPanel from './components/OverviewPanel';
import ArchitectureGraph from './components/ArchitectureGraph';
import SearchPanel from './components/SearchPanel';
import './App.css';

type Tab = 'overview' | 'architecture' | 'search';

function App() {
    const [repoUrl, setRepoUrl] = useState('');
    const [repoId, setRepoId] = useState<string | null>(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [activeTab, setActiveTab] = useState<Tab>('overview');

    const [overview, setOverview] = useState<RepositoryOverview | null>(null);
    const [architecture, setArchitecture] = useState<ArchitectureResponse | null>(null);
    const [graphStats, setGraphStats] = useState<GraphStatsResponse | null>(null);
    const [searchResults, setSearchResults] = useState<SearchResponse | null>(null);

    const handleAnalyze = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!repoUrl.trim()) return;

        setLoading(true);
        setError(null);
        setOverview(null);
        setArchitecture(null);
        setGraphStats(null);
        setSearchResults(null);

        try {
            const result = await analyzeRepository(repoUrl.trim());
            setRepoId(result.repositoryId);

            // Load overview, architecture, and graph stats in parallel
            const [ov, arch, stats] = await Promise.all([
                getOverview(result.repositoryId),
                getArchitecture(result.repositoryId),
                getGraphStats(result.repositoryId),
            ]);

            setOverview(ov);
            setArchitecture(arch);
            setGraphStats(stats);
            setActiveTab('overview');
        } catch (err) {
            setError(err instanceof Error ? err.message : 'Analysis failed');
        } finally {
            setLoading(false);
        }
    };

    const handleSearch = async (query: string) => {
        if (!repoId || !query.trim()) return;

        try {
            const results = await searchRepository(repoId, query.trim());
            setSearchResults(results);
        } catch (err) {
            setError(err instanceof Error ? err.message : 'Search failed');
        }
    };

    return (
        <div className="app">
            <h1>RepoLens</h1>
            <p className="subtitle">Understand any GitHub repository in seconds.</p>

            <form className="url-form" onSubmit={handleAnalyze}>
                <input
                    type="url"
                    placeholder="https://github.com/owner/repo"
                    value={repoUrl}
                    onChange={(e) => setRepoUrl(e.target.value)}
                    disabled={loading}
                />
                <button type="submit" disabled={loading || !repoUrl.trim()}>
                    {loading ? 'Analyzing...' : 'Analyze'}
                </button>
            </form>

            {error && <p className="error">{error}</p>}

            {repoId && !loading && (
                <>
                    <div className="tabs">
                        <button
                            className={activeTab === 'overview' ? 'active' : ''}
                            onClick={() => setActiveTab('overview')}
                        >
                            Overview
                        </button>
                        <button
                            className={activeTab === 'architecture' ? 'active' : ''}
                            onClick={() => setActiveTab('architecture')}
                        >
                            Architecture
                        </button>
                        <button
                            className={activeTab === 'search' ? 'active' : ''}
                            onClick={() => setActiveTab('search')}
                        >
                            Search
                        </button>
                    </div>

                    {activeTab === 'overview' && overview && <OverviewPanel overview={overview} />}
                    {activeTab === 'architecture' && architecture && (
                        <ArchitectureGraph architecture={architecture} stats={graphStats} />
                    )}
                    {activeTab === 'search' && (
                        <SearchPanel onSearch={handleSearch} results={searchResults} />
                    )}
                </>
            )}

            {!repoId && !loading && (
                <div className="empty-state">
                    <p>Paste a public GitHub repository URL above to get started.</p>
                </div>
            )}
        </div>
    );
}

export default App;
