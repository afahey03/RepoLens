import { useState } from 'react';
import { analyzePrImpact } from '../api';
import type { PrImpactResponse, PrSymbolImpact } from '../types';

interface PrImpactPanelProps {
    repoId: string;
    gitHubToken?: string;
}

const statusColors: Record<string, string> = {
    added: '#22c55e',
    removed: '#ef4444',
    modified: '#f59e0b',
    renamed: '#3b82f6',
};

const kindColors: Record<string, string> = {
    Class: '#3b82f6',
    Interface: '#8b5cf6',
    Method: '#22c55e',
    Property: '#f59e0b',
    Function: '#06b6d4',
    Import: '#94a3b8',
    Namespace: '#ec4899',
    Module: '#14b8a6',
    Variable: '#d97706',
};

export default function PrImpactPanel({ repoId, gitHubToken }: PrImpactPanelProps) {
    const [prNumber, setPrNumber] = useState('');
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [impact, setImpact] = useState<PrImpactResponse | null>(null);
    const [expandedFiles, setExpandedFiles] = useState<Set<string>>(new Set());
    const [showDownstream, setShowDownstream] = useState(false);

    const handleAnalyze = async (e: React.FormEvent) => {
        e.preventDefault();
        const num = parseInt(prNumber, 10);
        if (!num || num <= 0) return;

        setLoading(true);
        setError(null);
        setImpact(null);
        setExpandedFiles(new Set());

        try {
            const result = await analyzePrImpact(repoId, num, gitHubToken);
            setImpact(result);
        } catch (err: unknown) {
            const raw = err instanceof Error ? err.message : 'PR impact analysis failed';
            // Extract the server's plain-text message from the response if available
            setError(raw);
        } finally {
            setLoading(false);
        }
    };

    const toggleFile = (path: string) => {
        setExpandedFiles((prev) => {
            const next = new Set(prev);
            if (next.has(path)) next.delete(path);
            else next.add(path);
            return next;
        });
    };

    // Group affected symbols by file
    const symbolsByFile = new Map<string, PrSymbolImpact[]>();
    if (impact) {
        for (const sym of impact.affectedSymbols) {
            const list = symbolsByFile.get(sym.filePath) ?? [];
            list.push(sym);
            symbolsByFile.set(sym.filePath, list);
        }
    }

    return (
        <div className="pr-impact-panel">
            <form className="pr-input-form" onSubmit={handleAnalyze}>
                <input
                    type="number"
                    min="1"
                    placeholder="PR number (e.g. 42)"
                    value={prNumber}
                    onChange={(e) => setPrNumber(e.target.value)}
                    disabled={loading}
                    className="pr-number-input"
                />
                <button type="submit" disabled={loading || !prNumber.trim()}>
                    {loading ? 'Analyzing...' : 'Analyze PR Impact'}
                </button>
            </form>

            {error && <p className="error">{error}</p>}

            {impact && (
                <div className="pr-impact-results">
                    {/* Summary stats */}
                    <div className="pr-stats-grid">
                        <div className="pr-stat-card">
                            <span className="pr-stat-value">{impact.totalFilesChanged}</span>
                            <span className="pr-stat-label">Files Changed</span>
                        </div>
                        <div className="pr-stat-card">
                            <span className="pr-stat-value" style={{ color: '#22c55e' }}>
                                +{impact.totalAdditions}
                            </span>
                            <span className="pr-stat-label">Additions</span>
                        </div>
                        <div className="pr-stat-card">
                            <span className="pr-stat-value" style={{ color: '#ef4444' }}>
                                -{impact.totalDeletions}
                            </span>
                            <span className="pr-stat-label">Deletions</span>
                        </div>
                        <div className="pr-stat-card">
                            <span className="pr-stat-value">{impact.affectedSymbols.length}</span>
                            <span className="pr-stat-label">Symbols Affected</span>
                        </div>
                        <div className="pr-stat-card">
                            <span className="pr-stat-value">{impact.downstreamFiles.length}</span>
                            <span className="pr-stat-label">Downstream Files</span>
                        </div>
                        <div className="pr-stat-card">
                            <span className="pr-stat-value">{impact.affectedEdges.length}</span>
                            <span className="pr-stat-label">Edges Affected</span>
                        </div>
                    </div>

                    {/* Languages touched */}
                    {impact.languagesTouched.length > 0 && (
                        <div className="pr-section">
                            <h3>Languages Touched</h3>
                            <div className="pr-lang-tags">
                                {impact.languagesTouched.map((lang) => (
                                    <span key={lang} className="pr-lang-tag">
                                        {lang}
                                    </span>
                                ))}
                            </div>
                        </div>
                    )}

                    {/* Changed files */}
                    <div className="pr-section">
                        <h3>Changed Files</h3>
                        <div className="pr-file-list">
                            {impact.changedFiles.map((file) => {
                                const isExpanded = expandedFiles.has(file.filePath);
                                const fileSymbols = symbolsByFile.get(file.filePath) ?? [];
                                return (
                                    <div key={file.filePath} className="pr-file-item">
                                        <div
                                            className="pr-file-header"
                                            onClick={() => fileSymbols.length > 0 && toggleFile(file.filePath)}
                                            style={{
                                                cursor: fileSymbols.length > 0 ? 'pointer' : 'default',
                                            }}
                                        >
                                            <span
                                                className="pr-file-status"
                                                style={{
                                                    background:
                                                        statusColors[file.status] ?? '#888',
                                                }}
                                            >
                                                {file.status}
                                            </span>
                                            <span className="pr-file-path">
                                                {file.filePath}
                                                {file.previousFilePath && (
                                                    <span className="pr-file-rename">
                                                        {' '}
                                                        &larr; {file.previousFilePath}
                                                    </span>
                                                )}
                                            </span>
                                            <span className="pr-file-delta">
                                                <span style={{ color: '#22c55e' }}>
                                                    +{file.additions}
                                                </span>
                                                {' / '}
                                                <span style={{ color: '#ef4444' }}>
                                                    -{file.deletions}
                                                </span>
                                            </span>
                                            {file.language && (
                                                <span className="pr-file-lang">{file.language}</span>
                                            )}
                                            {fileSymbols.length > 0 && (
                                                <span className="pr-file-sym-count">
                                                    {fileSymbols.length} symbols{' '}
                                                    {isExpanded ? '\u25B2' : '\u25BC'}
                                                </span>
                                            )}
                                        </div>
                                        {isExpanded && fileSymbols.length > 0 && (
                                            <div className="pr-symbol-list">
                                                {fileSymbols.map((sym, i) => (
                                                    <div key={`${sym.name}-${i}`} className="pr-symbol-item">
                                                        <span
                                                            className="pr-symbol-kind"
                                                            style={{
                                                                background:
                                                                    kindColors[sym.kind] ?? '#888',
                                                            }}
                                                        >
                                                            {sym.kind}
                                                        </span>
                                                        <span className="pr-symbol-name">
                                                            {sym.parentSymbol && (
                                                                <span className="pr-symbol-parent">
                                                                    {sym.parentSymbol}.
                                                                </span>
                                                            )}
                                                            {sym.name}
                                                        </span>
                                                        <span className="pr-symbol-line">
                                                            L{sym.line}
                                                        </span>
                                                    </div>
                                                ))}
                                            </div>
                                        )}
                                    </div>
                                );
                            })}
                        </div>
                    </div>

                    {/* Downstream files */}
                    {impact.downstreamFiles.length > 0 && (
                        <div className="pr-section">
                            <h3
                                style={{ cursor: 'pointer' }}
                                onClick={() => setShowDownstream(!showDownstream)}
                            >
                                Downstream Dependencies ({impact.downstreamFiles.length}){' '}
                                {showDownstream ? '\u25B2' : '\u25BC'}
                            </h3>
                            {showDownstream && (
                                <div className="pr-downstream-list">
                                    {impact.downstreamFiles.map((f) => (
                                        <div key={f} className="pr-downstream-item">
                                            {f}
                                        </div>
                                    ))}
                                </div>
                            )}
                            <p className="pr-hint">
                                These files import or depend on files changed in this PR and may
                                need attention.
                            </p>
                        </div>
                    )}
                </div>
            )}

            {!impact && !loading && !error && (
                <div className="pr-empty-state">
                    <p>
                        Enter a PR number to analyze its impact on this repository's codebase.
                    </p>
                    <p className="pr-hint">
                        Shows which files, symbols, and dependencies are affected by a pull
                        request.
                    </p>
                </div>
            )}
        </div>
    );
}
