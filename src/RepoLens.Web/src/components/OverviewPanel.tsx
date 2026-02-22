import type { RepositoryOverview } from '../types';

interface Props {
    overview: RepositoryOverview;
}

const complexityColors: Record<string, string> = {
    Tiny: '#4caf50',
    Small: '#8bc34a',
    Medium: '#ff9800',
    Large: '#f44336',
    Huge: '#9c27b0',
};

function OverviewPanel({ overview }: Props) {
    const totalLangLines = Object.values(overview.languageLineBreakdown ?? {}).reduce(
        (s, n) => s + n,
        0,
    );

    return (
        <div className="overview">
            {/* Summary banner */}
            {overview.summary && (
                <div className="overview-card summary-card" style={{ gridColumn: '1 / -1' }}>
                    <h3>
                        Summary{' '}
                        {overview.summarySource === 'ai' && (
                            <span
                                className="complexity-badge"
                                style={{ background: '#8b5cf6' }}
                            >
                                ✨ AI
                            </span>
                        )}
                        <span
                            className="complexity-badge"
                            style={{
                                background: complexityColors[overview.complexity] ?? '#888',
                            }}
                        >
                            {overview.complexity}
                        </span>
                    </h3>
                    <p style={{ margin: 0, lineHeight: 1.6 }}>{overview.summary}</p>
                </div>
            )}

            {/* Core stats */}
            <div className="overview-card">
                <h3>Repository</h3>
                <div className="value">{overview.name}</div>
                <a href={overview.url} target="_blank" rel="noreferrer">
                    {overview.url}
                </a>
            </div>

            <div className="overview-card">
                <h3>Stats</h3>
                <div className="value">{overview.totalFiles.toLocaleString()} files</div>
                <div>{overview.totalLines.toLocaleString()} lines of code</div>
            </div>

            {/* Language breakdown by lines */}
            <div className="overview-card">
                <h3>Languages (by lines)</h3>
                {totalLangLines > 0 ? (
                    <>
                        <div className="lang-bar-stacked">
                            {Object.entries(overview.languageLineBreakdown)
                                .sort(([, a], [, b]) => b - a)
                                .map(([lang, lines]) => (
                                    <div
                                        key={lang}
                                        className="lang-segment"
                                        style={{ flex: lines }}
                                        title={`${lang}: ${lines.toLocaleString()} lines (${((lines / totalLangLines) * 100).toFixed(1)}%)`}
                                    />
                                ))}
                        </div>
                        <div className="language-bar" style={{ marginTop: '0.5rem' }}>
                            {Object.entries(overview.languageLineBreakdown)
                                .sort(([, a], [, b]) => b - a)
                                .map(([lang, lines]) => (
                                    <span key={lang} className="language-tag">
                                        {lang}: {lines.toLocaleString()} ({((lines / totalLangLines) * 100).toFixed(1)}%)
                                    </span>
                                ))}
                        </div>
                    </>
                ) : (
                    <div className="language-bar">
                        {Object.entries(overview.languageBreakdown).map(([lang, count]) => (
                            <span key={lang} className="language-tag">
                                {lang}: {count}
                            </span>
                        ))}
                    </div>
                )}
            </div>

            {/* Symbol counts */}
            {overview.symbolCounts && Object.keys(overview.symbolCounts).length > 0 && (
                <div className="overview-card">
                    <h3>Symbol Counts</h3>
                    <table className="symbol-table">
                        <tbody>
                            {Object.entries(overview.symbolCounts)
                                .sort(([, a], [, b]) => b - a)
                                .map(([kind, count]) => (
                                    <tr key={kind}>
                                        <td>{kind}</td>
                                        <td style={{ textAlign: 'right', fontWeight: 600 }}>
                                            {count.toLocaleString()}
                                        </td>
                                    </tr>
                                ))}
                        </tbody>
                    </table>
                </div>
            )}

            {/* Key types */}
            {overview.keyTypes && overview.keyTypes.length > 0 && (
                <div className="overview-card">
                    <h3>Key Types</h3>
                    <ul className="key-types-list">
                        {overview.keyTypes.map((t) => (
                            <li key={t.name + t.filePath}>
                                <strong>{t.name}</strong>{' '}
                                <span className="badge">{t.kind}</span>{' '}
                                <span className="member-count">{t.memberCount} members</span>
                                <div className="file-path">{t.filePath}</div>
                            </li>
                        ))}
                    </ul>
                </div>
            )}

            {/* Most connected modules */}
            {overview.mostConnectedModules && overview.mostConnectedModules.length > 0 && (
                <div className="overview-card">
                    <h3>Most Connected Modules</h3>
                    <ul className="connected-list">
                        {overview.mostConnectedModules.map((m) => (
                            <li key={m.name + m.filePath}>
                                <strong>{m.name}</strong>
                                <div>
                                    <span className="edge-in">⬅ {m.incomingEdges} in</span>{' '}
                                    <span className="edge-out">➡ {m.outgoingEdges} out</span>
                                </div>
                                <div className="file-path">{m.filePath}</div>
                            </li>
                        ))}
                    </ul>
                </div>
            )}

            {/* Frameworks */}
            <div className="overview-card">
                <h3>Frameworks</h3>
                {overview.detectedFrameworks.length > 0 ? (
                    <ul className="framework-list">
                        {overview.detectedFrameworks.map((fw) => (
                            <li key={fw}>{fw}</li>
                        ))}
                    </ul>
                ) : (
                    <div>None detected</div>
                )}
            </div>

            {/* External dependencies */}
            {overview.externalDependencies && overview.externalDependencies.length > 0 && (
                <div className="overview-card">
                    <h3>External Dependencies ({overview.externalDependencies.length})</h3>
                    <div className="dep-tags">
                        {overview.externalDependencies.map((dep) => (
                            <span key={dep} className="dep-tag">
                                {dep}
                            </span>
                        ))}
                    </div>
                </div>
            )}

            {/* Top-level folders */}
            <div className="overview-card">
                <h3>Top-Level Folders</h3>
                <ul className="folder-list">
                    {overview.topLevelFolders.map((folder) => (
                        <li key={folder}>{folder}</li>
                    ))}
                </ul>
            </div>

            {/* Entry points */}
            <div className="overview-card">
                <h3>Entry Points</h3>
                {overview.entryPoints.length > 0 ? (
                    <ul className="entry-list">
                        {overview.entryPoints.map((ep) => (
                            <li key={ep}>{ep}</li>
                        ))}
                    </ul>
                ) : (
                    <div>None detected</div>
                )}
            </div>
        </div>
    );
}

export default OverviewPanel;
