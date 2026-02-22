using RepoLens.Shared.Models;

namespace RepoLens.Shared.Contracts;

/// <summary>
/// Builds and queries a searchable code index.
/// </summary>
public interface ISearchEngine
{
    /// <summary>
    /// Builds a search index from the given symbols and files.
    /// </summary>
    void BuildIndex(string repositoryId, List<SymbolInfo> symbols, List<Models.FileInfo> files);

    /// <summary>
    /// Searches the index for the given query.
    /// </summary>
    List<SearchResult> Search(string repositoryId, string query, int maxResults = 20);

    /// <summary>
    /// Searches with optional kind filtering and pagination.
    /// </summary>
    List<SearchResult> Search(string repositoryId, string query, string[]? kinds, int skip, int take);

    /// <summary>
    /// Returns total match count for pagination.
    /// </summary>
    int SearchCount(string repositoryId, string query, string[]? kinds);

    /// <summary>
    /// Returns available symbol kinds in the index.
    /// </summary>
    List<string> GetAvailableKinds(string repositoryId);
}
