import { useState, useEffect, useRef, useCallback } from 'react';
import { searchRepository, getSuggestions } from '../api';
import type { SearchResponse, SearchSuggestion } from '../types';

interface Props {
    repoId: string;
}

const KIND_ICONS: Record<string, string> = {
    Class: '◆',
    Interface: '◇',
    Method: 'ƒ',
    Property: '●',
    Function: 'λ',
    Variable: 'x',
    Import: '↗',
    Namespace: '📦',
    Module: '▣',
    File: '📄',
};

const KIND_COLORS: Record<string, string> = {
    Class: '#818cf8',
    Interface: '#a78bfa',
    Method: '#34d399',
    Property: '#fbbf24',
    Function: '#22d3ee',
    Variable: '#f87171',
    Import: '#94a3b8',
    Namespace: '#fb923c',
    Module: '#60a5fa',
    File: '#71717a',
};

const PAGE_SIZE = 20;

function SearchPanel({ repoId }: Props) {
    const [query, setQuery] = useState('');
    const [results, setResults] = useState<SearchResponse | null>(null);
    const [suggestions, setSuggestions] = useState<SearchSuggestion[]>([]);
    const [showSuggestions, setShowSuggestions] = useState(false);
    const [selectedKinds, setSelectedKinds] = useState<Set<string>>(new Set());
    const [page, setPage] = useState(0);
    const [loading, setLoading] = useState(false);
    const [activeSuggestionIndex, setActiveSuggestionIndex] = useState(-1);

    const inputRef = useRef<HTMLInputElement>(null);
    const suggestionsRef = useRef<HTMLDivElement>(null);
    const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);
    const suggestDebounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

    // Perform search with current filters + pagination
    const doSearch = useCallback(async (q: string, kinds: Set<string>, pageNum: number) => {
        if (!q.trim()) {
            setResults(null);
            return;
        }
        setLoading(true);
        try {
            const res = await searchRepository(repoId, q.trim(), {
                kinds: kinds.size > 0 ? [...kinds] : undefined,
                skip: pageNum * PAGE_SIZE,
                take: PAGE_SIZE,
            });
            setResults(res);
        } catch {
            // Silently handle — error state could be added later
        } finally {
            setLoading(false);
        }
    }, [repoId]);

    // Debounced live search (300ms)
    useEffect(() => {
        if (debounceRef.current) clearTimeout(debounceRef.current);
        if (!query.trim()) {
            setResults(null);
            return;
        }
        debounceRef.current = setTimeout(() => {
            setPage(0);
            doSearch(query, selectedKinds, 0);
        }, 300);
        return () => { if (debounceRef.current) clearTimeout(debounceRef.current); };
    }, [query, selectedKinds, doSearch]);

    // Fetch suggestions as user types (150ms debounce)
    useEffect(() => {
        if (suggestDebounceRef.current) clearTimeout(suggestDebounceRef.current);
        if (query.trim().length < 2) {
            setSuggestions([]);
            return;
        }
        suggestDebounceRef.current = setTimeout(async () => {
            try {
                const res = await getSuggestions(repoId, query.trim());
                setSuggestions(res.suggestions);
            } catch {
                setSuggestions([]);
            }
        }, 150);
        return () => { if (suggestDebounceRef.current) clearTimeout(suggestDebounceRef.current); };
    }, [query, repoId]);

    // Re-search when page changes
    useEffect(() => {
        if (page > 0 && query.trim()) {
            doSearch(query, selectedKinds, page);
        }
    }, [page, query, selectedKinds, doSearch]);

    // Close suggestions on click outside
    useEffect(() => {
        const handler = (e: MouseEvent) => {
            if (suggestionsRef.current && !suggestionsRef.current.contains(e.target as Node) &&
                inputRef.current && !inputRef.current.contains(e.target as Node)) {
                setShowSuggestions(false);
            }
        };
        document.addEventListener('mousedown', handler);
        return () => document.removeEventListener('mousedown', handler);
    }, []);

    const handleSuggestionClick = (suggestion: SearchSuggestion) => {
        setQuery(suggestion.text);
        setShowSuggestions(false);
        setActiveSuggestionIndex(-1);
    };

    const handleKeyDown = (e: React.KeyboardEvent) => {
        if (!showSuggestions || suggestions.length === 0) return;

        if (e.key === 'ArrowDown') {
            e.preventDefault();
            setActiveSuggestionIndex(prev => Math.min(prev + 1, suggestions.length - 1));
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            setActiveSuggestionIndex(prev => Math.max(prev - 1, -1));
        } else if (e.key === 'Enter' && activeSuggestionIndex >= 0) {
            e.preventDefault();
            handleSuggestionClick(suggestions[activeSuggestionIndex]);
        } else if (e.key === 'Escape') {
            setShowSuggestions(false);
        }
    };

    const toggleKind = (kind: string) => {
        setSelectedKinds(prev => {
            const next = new Set(prev);
            if (next.has(kind)) next.delete(kind);
            else next.add(kind);
            return next;
        });
        setPage(0);
    };

    const totalPages = results ? Math.ceil(results.totalResults / PAGE_SIZE) : 0;

    // Group results by file
    const groupedResults = results?.results.reduce<Record<string, typeof results.results>>((acc, r) => {
        const key = r.filePath;
        if (!acc[key]) acc[key] = [];
        acc[key].push(r);
        return acc;
    }, {}) ?? {};

    // Highlight query terms in text
    const highlightMatch = (text: string) => {
        if (!query.trim()) return text;
        const terms = query.trim().split(/\s+/).filter(t => t.length > 1);
        if (terms.length === 0) return text;
        const pattern = new RegExp(`(${terms.map(t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})`, 'gi');
        const parts = text.split(pattern);
        return parts.map((part, i) =>
            pattern.test(part)
                ? <mark key={i} className="search-highlight">{part}</mark>
                : part
        );
    };

    return (
        <div className="search-panel">
            {/* Search input with suggestions */}
            <div className="search-input-container">
                <div className="search-input-wrapper">
                    <span className="search-icon">🔍</span>
                    <input
                        ref={inputRef}
                        type="text"
                        className="search-input"
                        placeholder="Search symbols, files, imports..."
                        value={query}
                        onChange={(e) => {
                            setQuery(e.target.value);
                            setShowSuggestions(true);
                            setActiveSuggestionIndex(-1);
                        }}
                        onFocus={() => suggestions.length > 0 && setShowSuggestions(true)}
                        onKeyDown={handleKeyDown}
                    />
                    {query && (
                        <button
                            className="search-clear"
                            onClick={() => {
                                setQuery('');
                                setResults(null);
                                setSuggestions([]);
                                inputRef.current?.focus();
                            }}
                        >
                            ✕
                        </button>
                    )}
                    {loading && <span className="search-spinner" />}
                </div>

                {/* Suggestions dropdown */}
                {showSuggestions && suggestions.length > 0 && (
                    <div ref={suggestionsRef} className="suggestions-dropdown">
                        {suggestions.map((s, i) => (
                            <button
                                key={`${s.text}-${s.filePath}-${i}`}
                                className={`suggestion-item ${i === activeSuggestionIndex ? 'active' : ''}`}
                                onMouseDown={() => handleSuggestionClick(s)}
                                onMouseEnter={() => setActiveSuggestionIndex(i)}
                            >
                                <span
                                    className="suggestion-icon"
                                    style={{ color: KIND_COLORS[s.kind] || '#71717a' }}
                                >
                                    {KIND_ICONS[s.kind] || '•'}
                                </span>
                                <span className="suggestion-text">{s.text}</span>
                                <span className="suggestion-kind">{s.kind}</span>
                            </button>
                        ))}
                    </div>
                )}
            </div>

            {/* Kind filter chips */}
            {results && results.availableKinds.length > 0 && (
                <div className="kind-filters">
                    <span className="kind-filters-label">Filter:</span>
                    {results.availableKinds.map(kind => (
                        <button
                            key={kind}
                            className={`kind-chip ${selectedKinds.has(kind) ? 'active' : ''}`}
                            onClick={() => toggleKind(kind)}
                            style={selectedKinds.has(kind) ? {
                                background: KIND_COLORS[kind] || '#71717a',
                                borderColor: KIND_COLORS[kind] || '#71717a',
                            } : undefined}
                        >
                            <span className="kind-chip-icon">{KIND_ICONS[kind] || '•'}</span>
                            {kind}
                        </button>
                    ))}
                    {selectedKinds.size > 0 && (
                        <button className="kind-chip clear-chip" onClick={() => { setSelectedKinds(new Set()); setPage(0); }}>
                            Clear filters
                        </button>
                    )}
                </div>
            )}

            {/* Results summary */}
            {results && query.trim() && (
                <div className="search-summary">
                    <span className="result-count">{results.totalResults}</span> results for
                    &ldquo;<span className="result-query">{results.query}</span>&rdquo;
                    {selectedKinds.size > 0 && (
                        <span className="filter-note">
                            &nbsp;filtered to {[...selectedKinds].join(', ')}
                        </span>
                    )}
                </div>
            )}

            {/* Grouped results */}
            {results && results.results.length > 0 && (
                <div className="search-results-grouped">
                    {Object.entries(groupedResults).map(([filePath, items]) => (
                        <div key={filePath} className="file-group">
                            <div className="file-group-header">
                                <span className="file-group-icon">📄</span>
                                <span className="file-group-path">{filePath}</span>
                                <span className="file-group-count">{items.length}</span>
                            </div>
                            {items.map((r, i) => (
                                <div key={`${r.filePath}-${r.line}-${i}`} className="search-result-card-v2">
                                    <div className="result-card-header">
                                        <span
                                            className="result-kind-badge"
                                            style={{
                                                background: `${KIND_COLORS[r.kind] || '#71717a'}20`,
                                                color: KIND_COLORS[r.kind] || '#71717a',
                                                borderColor: KIND_COLORS[r.kind] || '#71717a',
                                            }}
                                        >
                                            <span className="result-kind-icon">{KIND_ICONS[r.kind] || '•'}</span>
                                            {r.kind}
                                        </span>
                                        {r.symbol && (
                                            <span className="result-symbol">{highlightMatch(r.symbol)}</span>
                                        )}
                                        {r.line > 0 && (
                                            <span className="result-line">L{r.line}</span>
                                        )}
                                        <span className="result-score">{r.score.toFixed(1)}</span>
                                    </div>
                                    {r.snippet && r.snippet !== r.symbol && (
                                        <div className="result-snippet">{highlightMatch(r.snippet)}</div>
                                    )}
                                </div>
                            ))}
                        </div>
                    ))}
                </div>
            )}

            {/* Pagination */}
            {results && totalPages > 1 && (
                <div className="search-pagination">
                    <button
                        disabled={page === 0}
                        onClick={() => setPage(p => p - 1)}
                    >
                        ← Prev
                    </button>
                    <span className="page-info">
                        Page {page + 1} of {totalPages}
                    </span>
                    <button
                        disabled={page >= totalPages - 1}
                        onClick={() => setPage(p => p + 1)}
                    >
                        Next →
                    </button>
                </div>
            )}

            {/* Empty states */}
            {results && results.results.length === 0 && query.trim() && (
                <div className="search-empty">
                    <p>No results for &ldquo;{query}&rdquo;</p>
                    {selectedKinds.size > 0 && (
                        <p className="search-empty-hint">Try clearing your kind filters.</p>
                    )}
                </div>
            )}

            {!results && !query.trim() && (
                <div className="search-welcome">
                    <div className="search-welcome-icon">🔍</div>
                    <h3>Search the repository</h3>
                    <p>Find classes, functions, files, and imports instantly.</p>
                    <div className="search-examples">
                        <span className="search-example-label">Try:</span>
                        {['Controller', 'useState', 'index.ts', 'IService'].map(ex => (
                            <button
                                key={ex}
                                className="search-example"
                                onClick={() => {
                                    setQuery(ex);
                                    inputRef.current?.focus();
                                }}
                            >
                                {ex}
                            </button>
                        ))}
                    </div>
                </div>
            )}
        </div>
    );
}

export default SearchPanel;
