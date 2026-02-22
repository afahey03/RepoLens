using Microsoft.AspNetCore.Mvc;
using RepoLens.Shared.Contracts;
using RepoLens.Shared.DTOs;
using RepoLens.Shared.Models;

namespace RepoLens.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RepositoryController : ControllerBase
{
    private readonly IRepositoryDownloader _downloader;
    private readonly IRepositoryAnalyzer _analyzer;
    private readonly ISearchEngine _searchEngine;

    // In-memory store for MVP (will be replaced with proper storage later)
    private static readonly Dictionary<string, AnalysisResult> _analysisCache = new();

    public RepositoryController(
        IRepositoryDownloader downloader,
        IRepositoryAnalyzer analyzer,
        ISearchEngine searchEngine)
    {
        _downloader = downloader;
        _analyzer = analyzer;
        _searchEngine = searchEngine;
    }

    /// <summary>
    /// POST /api/repository/analyze
    /// Triggers analysis of a public GitHub repository.
    /// </summary>
    [HttpPost("analyze")]
    public async Task<ActionResult<AnalyzeResponse>> Analyze(
        [FromBody] AnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryUrl))
        {
            return BadRequest("Repository URL is required.");
        }

        // Generate a simple repository ID
        var repoId = request.RepositoryUrl
            .TrimEnd('/')
            .Split('/')
            .TakeLast(2)
            .Aggregate((a, b) => $"{a}_{b}");

        // Download
        var repoPath = await _downloader.DownloadAsync(request.RepositoryUrl, cancellationToken);

        // Analyze
        var overview = await _analyzer.GenerateOverviewAsync(repoPath, request.RepositoryUrl, cancellationToken);
        var graph = await _analyzer.BuildDependencyGraphAsync(repoPath, cancellationToken);
        var symbols = await _analyzer.ExtractSymbolsAsync(repoPath, cancellationToken);
        var files = await _analyzer.ScanFilesAsync(repoPath, cancellationToken);

        // Build search index
        _searchEngine.BuildIndex(repoId, symbols, files);

        // Cache results
        _analysisCache[repoId] = new AnalysisResult
        {
            Overview = overview,
            Graph = graph,
            Symbols = symbols,
            Files = files
        };

        return Ok(new AnalyzeResponse
        {
            RepositoryId = repoId,
            Status = "completed"
        });
    }

    /// <summary>
    /// GET /api/repository/{id}/overview
    /// Returns the repository overview.
    /// </summary>
    [HttpGet("{id}/overview")]
    public ActionResult<RepositoryOverview> GetOverview(string id)
    {
        if (!_analysisCache.TryGetValue(id, out var result))
        {
            return NotFound("Repository not analyzed yet.");
        }

        return Ok(result.Overview);
    }

    /// <summary>
    /// GET /api/repository/{id}/architecture
    /// Returns the architecture graph.
    /// </summary>
    [HttpGet("{id}/architecture")]
    public ActionResult<ArchitectureResponse> GetArchitecture(string id)
    {
        if (!_analysisCache.TryGetValue(id, out var result))
        {
            return NotFound("Repository not analyzed yet.");
        }

        var response = new ArchitectureResponse
        {
            Nodes = result.Graph.Nodes.Select(n => new GraphNodeDto
            {
                Id = n.Id,
                Name = n.Name,
                Type = n.Type.ToString(),
                Metadata = n.Metadata
            }).ToList(),
            Edges = result.Graph.Edges.Select(e => new GraphEdgeDto
            {
                Source = e.Source,
                Target = e.Target,
                Relationship = e.Relationship.ToString()
            }).ToList()
        };

        return Ok(response);
    }

    /// <summary>
    /// GET /api/repository/{id}/search?q=query
    /// Searches the repository index.
    /// </summary>
    [HttpGet("{id}/search")]
    public ActionResult<SearchResponse> Search(string id, [FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("Query parameter 'q' is required.");
        }

        if (!_analysisCache.ContainsKey(id))
        {
            return NotFound("Repository not analyzed yet.");
        }

        var results = _searchEngine.Search(id, q);

        return Ok(new SearchResponse
        {
            Query = q,
            TotalResults = results.Count,
            Results = results
        });
    }

    /// <summary>
    /// Internal cache for analysis results during MVP.
    /// </summary>
    private class AnalysisResult
    {
        public required RepositoryOverview Overview { get; set; }
        public required DependencyGraph Graph { get; set; }
        public required List<SymbolInfo> Symbols { get; set; }
        public required List<Shared.Models.FileInfo> Files { get; set; }
    }
}
