import { useState, useRef, useCallback } from 'react';
import { analyzeRepository, getOverview, getArchitecture, getGraphStats, getProgress } from './api';
import type { RepositoryOverview, ArchitectureResponse, GraphStatsResponse, AnalysisProgress } from './types';
import OverviewPanel from './components/OverviewPanel';
import ArchitectureGraph from './components/ArchitectureGraph';
import SearchPanel from './components/SearchPanel';
import PrImpactPanel from './components/PrImpactPanel';
import './App.css';

type Tab = 'overview' | 'architecture' | 'search' | 'pr-impact';

function App() {
    const [repoUrl, setRepoUrl] = useState('');
    const [gitHubToken, setGitHubToken] = useState('');
    const [openAiKey, setOpenAiKey] = useState('');
    const [showSettings, setShowSettings] = useState(false);
    const [repoId, setRepoId] = useState<string | null>(null);
    const [loading, setLoading] = useState(false);
    const [progress, setProgress] = useState<AnalysisProgress | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [activeTab, setActiveTab] = useState<Tab>('overview');

    const [overview, setOverview] = useState<RepositoryOverview | null>(null);
    const [architecture, setArchitecture] = useState<ArchitectureResponse | null>(null);
    const [graphStats, setGraphStats] = useState<GraphStatsResponse | null>(null);

    const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

    const stopPolling = useCallback(() => {
        if (pollRef.current) {
            clearInterval(pollRef.current);
            pollRef.current = null;
        }
    }, []);

    const handleAnalyze = async (e: React.FormEvent, forceReanalyze = false) => {
        e.preventDefault();
        if (!repoUrl.trim()) return;

        setLoading(true);
        setError(null);
        setProgress(null);
        setOverview(null);
        setArchitecture(null);
        setGraphStats(null);
        stopPolling();

        try {
            const result = await analyzeRepository(
                repoUrl.trim(),
                gitHubToken.trim() || undefined,
                forceReanalyze || undefined,
                openAiKey.trim() || undefined,
            );
            const id = result.repositoryId;
            setRepoId(id);

            if (result.status === 'completed') {
                // Cached — load all data immediately
                await loadResults(id);
                return;
            }

            // Status is "analyzing" — start polling for progress
            setProgress({
                repositoryId: id,
                stage: 'Queued',
                stageLabel: 'Queued',
                percentComplete: 0,
            });

            pollRef.current = setInterval(async () => {
                try {
                    const p = await getProgress(id);
                    setProgress(p);

                    if (p.stage === 'Completed') {
                        stopPolling();
                        await loadResults(id);
                    } else if (p.stage === 'Failed') {
                        stopPolling();
                        setError(p.error ?? 'Analysis failed');
                        setLoading(false);
                    }
                } catch {
                    // Transient fetch error — keep polling
                }
            }, 600);
        } catch (err) {
            setError(err instanceof Error ? err.message : 'Analysis failed');
            setLoading(false);
        }
    };

    const loadResults = async (id: string) => {
        try {
            setProgress({
                repositoryId: id,
                stage: 'Completed',
                stageLabel: 'Loading results...',
                percentComplete: 95,
            });

            const [ov, arch, stats] = await Promise.all([
                getOverview(id),
                getArchitecture(id),
                getGraphStats(id),
            ]);

            setOverview(ov);
            setArchitecture(arch);
            setGraphStats(stats);
            setActiveTab('overview');
        } catch (err) {
            setError(err instanceof Error ? err.message : 'Failed to load results');
        } finally {
            setLoading(false);
            setProgress(null);
        }
    };

    return (
        <div className="app">
            <div className="app-header">
                <img src="/logo.svg" alt="RepoLens" className="app-logo" />
                <div>
                    <h1>RepoLens</h1>
                    <p className="subtitle">Understand any GitHub repository in seconds.</p>
                </div>
            </div>

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

            <div className="token-section">
                <button
                    type="button"
                    className="token-toggle"
                    onClick={() => setShowSettings(!showSettings)}
                >
                    {showSettings ? 'Hide settings' : 'Settings'} {showSettings ? '\u25B2' : '\u25BC'}
                </button>
                {showSettings && (
                    <div className="token-input-wrapper">
                        <label className="settings-label">GitHub Token (private repos)</label>
                        <input
                            type="password"
                            className="token-input"
                            placeholder="GitHub Personal Access Token (optional)"
                            value={gitHubToken}
                            onChange={(e) => setGitHubToken(e.target.value)}
                            disabled={loading}
                            autoComplete="off"
                        />
                        <p className="token-hint">
                            Required for private repos. Generate at{' '}
                            <a href="https://github.com/settings/tokens" target="_blank" rel="noopener noreferrer">
                                GitHub Settings &rarr; Tokens
                            </a>
                            . Never stored — used only for this download.
                        </p>

                        <label className="settings-label" style={{ marginTop: '1rem' }}>OpenAI API Key (AI summaries)</label>
                        <input
                            type="password"
                            className="token-input"
                            placeholder="sk-... (optional)"
                            value={openAiKey}
                            onChange={(e) => setOpenAiKey(e.target.value)}
                            disabled={loading}
                            autoComplete="off"
                        />
                        <p className="token-hint">
                            Enables richer LLM-generated summaries. Get a key at{' '}
                            <a href="https://platform.openai.com/api-keys" target="_blank" rel="noopener noreferrer">
                                OpenAI &rarr; API Keys
                            </a>
                            . Never stored — used only for this analysis.
                        </p>
                    </div>
                )}
            </div>

            {loading && progress && (
                <div className="progress-container">
                    <div className="progress-bar">
                        <div
                            className="progress-fill"
                            style={{ width: `${progress.percentComplete}%` }}
                        />
                    </div>
                    <p className="progress-label">{progress.stageLabel}</p>
                </div>
            )}

            {error && <p className="error">{error}</p>}

            {repoId && !loading && (
                <>
                    <div className="results-toolbar">
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
                            <button
                                className={activeTab === 'pr-impact' ? 'active' : ''}
                                onClick={() => setActiveTab('pr-impact')}
                            >
                                PR Impact
                            </button>
                        </div>
                        <button
                            className="reanalyze-btn"
                            onClick={(e) => handleAnalyze(e, true)}
                            title="Re-download and incrementally re-analyze this repository"
                        >
                            &#x21bb; Re-analyze
                        </button>
                    </div>

                    {activeTab === 'overview' && overview && <OverviewPanel overview={overview} />}
                    {activeTab === 'architecture' && architecture && (
                        <ArchitectureGraph architecture={architecture} stats={graphStats} />
                    )}
                    {activeTab === 'search' && repoId && (
                        <SearchPanel repoId={repoId} />
                    )}
                    {activeTab === 'pr-impact' && repoId && (
                        <PrImpactPanel repoId={repoId} gitHubToken={gitHubToken.trim() || undefined} />
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
