import { useState } from 'react';
import type { SearchResponse } from '../types';

interface Props {
    onSearch: (query: string) => void;
    results: SearchResponse | null;
}

function SearchPanel({ onSearch, results }: Props) {
    const [query, setQuery] = useState('');

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        onSearch(query);
    };

    return (
        <div>
            <form className="search-form" onSubmit={handleSubmit}>
                <input
                    type="text"
                    placeholder="Search symbols, files, imports..."
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                />
                <button type="submit" disabled={!query.trim()}>
                    Search
                </button>
            </form>

            {results && (
                <div>
                    <p style={{ color: '#a1a1aa', marginBottom: '1rem' }}>
                        {results.totalResults} results for &ldquo;{results.query}&rdquo;
                    </p>
                    <div className="search-results">
                        {results.results.map((r, i) => (
                            <div key={i} className="search-result-card">
                                <div className="file-path">{r.filePath}</div>
                                {r.symbol && <div className="symbol-name">{r.symbol}</div>}
                                <div className="snippet">{r.snippet}</div>
                                <div className="score">Score: {r.score.toFixed(2)}</div>
                            </div>
                        ))}
                        {results.results.length === 0 && (
                            <div className="empty-state">No results found.</div>
                        )}
                    </div>
                </div>
            )}

            {!results && (
                <div className="empty-state">
                    <p>Enter a query to search across the repository.</p>
                </div>
            )}
        </div>
    );
}

export default SearchPanel;
