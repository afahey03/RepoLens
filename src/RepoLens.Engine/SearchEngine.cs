using RepoLens.Shared.Contracts;
using RepoLens.Shared.Models;
using FileInfo = RepoLens.Shared.Models.FileInfo;

namespace RepoLens.Engine;

/// <summary>
/// In-memory search engine with BM25-like scoring.
/// Indexes symbols, filenames, and file content.
/// </summary>
public class SearchEngine : ISearchEngine
{
    private readonly Dictionary<string, SearchIndex> _indices = new();

    public void BuildIndex(string repositoryId, List<SymbolInfo> symbols, List<FileInfo> files)
    {
        var index = new SearchIndex();

        // Index symbols
        foreach (var symbol in symbols)
        {
            index.AddDocument(new IndexDocument
            {
                FilePath = symbol.FilePath,
                Symbol = symbol.Name,
                Content = symbol.Name,
                Line = symbol.Line,
                Kind = symbol.Kind.ToString()
            });
        }

        // Index filenames
        foreach (var file in files)
        {
            index.AddDocument(new IndexDocument
            {
                FilePath = file.RelativePath,
                Symbol = Path.GetFileNameWithoutExtension(file.RelativePath),
                Content = file.RelativePath,
                Line = 0,
                Kind = "File"
            });
        }

        _indices[repositoryId] = index;
    }

    public List<SearchResult> Search(string repositoryId, string query, int maxResults = 20)
    {
        if (!_indices.TryGetValue(repositoryId, out var index))
        {
            return [];
        }

        return index.Search(query, maxResults);
    }

    /// <summary>
    /// Searches with optional kind filtering and pagination.
    /// </summary>
    public List<SearchResult> Search(string repositoryId, string query, string[]? kinds, int skip, int take)
    {
        if (!_indices.TryGetValue(repositoryId, out var index))
        {
            return [];
        }

        // Get a larger result set to allow filtering + pagination
        var all = index.Search(query, 500);

        if (kinds != null && kinds.Length > 0)
        {
            var kindSet = new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
            all = all.Where(r => kindSet.Contains(r.Kind)).ToList();
        }

        return all.Skip(skip).Take(take).ToList();
    }

    /// <summary>
    /// Returns the total matching count (after kind filtering) for pagination metadata.
    /// </summary>
    public int SearchCount(string repositoryId, string query, string[]? kinds)
    {
        if (!_indices.TryGetValue(repositoryId, out var index))
        {
            return 0;
        }

        var all = index.Search(query, 500);

        if (kinds != null && kinds.Length > 0)
        {
            var kindSet = new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
            all = all.Where(r => kindSet.Contains(r.Kind)).ToList();
        }

        return all.Count;
    }

    /// <summary>
    /// Returns autocomplete suggestions for a prefix.
    /// </summary>
    public List<SearchSuggestion> Suggest(string repositoryId, string prefix, int maxResults = 10)
    {
        if (!_indices.TryGetValue(repositoryId, out var index))
        {
            return [];
        }

        return index.Suggest(prefix, maxResults);
    }

    /// <summary>
    /// Returns all available symbol kinds in the index.
    /// </summary>
    public List<string> GetAvailableKinds(string repositoryId)
    {
        if (!_indices.TryGetValue(repositoryId, out var index))
        {
            return [];
        }

        return index.GetAvailableKinds();
    }
}
