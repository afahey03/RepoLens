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
}
