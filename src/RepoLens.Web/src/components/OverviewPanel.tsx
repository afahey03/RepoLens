import type { RepositoryOverview } from '../types';

interface Props {
    overview: RepositoryOverview;
}

function OverviewPanel({ overview }: Props) {
    return (
        <div className="overview">
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

            <div className="overview-card">
                <h3>Languages</h3>
                <div className="language-bar">
                    {Object.entries(overview.languageBreakdown).map(([lang, count]) => (
                        <span key={lang} className="language-tag">
                            {lang}: {count}
                        </span>
                    ))}
                </div>
            </div>

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

            <div className="overview-card">
                <h3>Top-Level Folders</h3>
                <ul className="folder-list">
                    {overview.topLevelFolders.map((folder) => (
                        <li key={folder}>{folder}</li>
                    ))}
                </ul>
            </div>

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
