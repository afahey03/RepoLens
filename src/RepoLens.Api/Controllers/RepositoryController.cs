using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RepoLens.Engine;
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
    private readonly ILogger<RepositoryController> _logger;

    // In-memory store for MVP (will be replaced with proper storage later)
    private static readonly Dictionary<string, AnalysisResult> _analysisCache = new();

    public RepositoryController(
        IRepositoryDownloader downloader,
        IRepositoryAnalyzer analyzer,
        ISearchEngine searchEngine,
        ILogger<RepositoryController> logger)
    {
        _downloader = downloader;
        _analyzer = analyzer;
        _searchEngine = searchEngine;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/repository/analyze
    /// Downloads and analyzes a public GitHub repository.
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

        // Generate a deterministic repository ID from the URL
        var repoId = GenerateRepoId(request.RepositoryUrl);

        // Return cached result if already analyzed
        if (_analysisCache.ContainsKey(repoId))
        {
            _logger.LogInformation("Returning cached analysis for {RepoId}", repoId);
            return Ok(new AnalyzeResponse { RepositoryId = repoId, Status = "completed" });
        }

        _logger.LogInformation("Starting analysis for {Url} (id: {RepoId})", request.RepositoryUrl, repoId);

        try
        {
            // Step 1: Download the repository
            var repoPath = await _downloader.DownloadAsync(request.RepositoryUrl, cancellationToken);
            _logger.LogInformation("Repository downloaded to {Path}", repoPath);

            // Step 2: Run full analysis in a single pass (scans files only once)
            var analysis = await _analyzer.AnalyzeFullAsync(repoPath, request.RepositoryUrl, cancellationToken);
            _logger.LogInformation("Full analysis complete: {Files} files, {Symbols} symbols, {Nodes} nodes",
                analysis.Files.Count, analysis.Symbols.Count, analysis.Graph.Nodes.Count);

            // Step 3: Build search index
            _searchEngine.BuildIndex(repoId, analysis.Symbols, analysis.Files);
            _logger.LogInformation("Search index built for {RepoId}", repoId);

            // Cache all results
            _analysisCache[repoId] = new AnalysisResult
            {
                Overview = analysis.Overview,
                Graph = analysis.Graph,
                Symbols = analysis.Symbols,
                Files = analysis.Files
            };

            return Ok(new AnalyzeResponse { RepositoryId = repoId, Status = "completed" });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Bad request: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Analysis failed for {Url}", request.RepositoryUrl);
            return UnprocessableEntity(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error analyzing {Url}", request.RepositoryUrl);
            return StatusCode(500, "An unexpected error occurred during analysis.");
        }
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
    /// GET /api/repository/{id}/architecture/stats
    /// Returns aggregate statistics about the architecture graph.
    /// </summary>
    [HttpGet("{id}/architecture/stats")]
    public ActionResult<GraphStatsResponse> GetGraphStats(string id)
    {
        if (!_analysisCache.TryGetValue(id, out var result))
        {
            return NotFound("Repository not analyzed yet.");
        }

        var graph = result.Graph;
        var targets = new HashSet<string>(graph.Edges.Select(e => e.Target));
        var sources = new HashSet<string>(graph.Edges.Select(e => e.Source));
        var allNodeIds = new HashSet<string>(graph.Nodes.Select(n => n.Id));

        // Root nodes: have no incoming edges
        var rootNodeIds = allNodeIds.Except(targets).ToList();
        var rootNames = rootNodeIds
            .Select(id2 => graph.Nodes.FirstOrDefault(n => n.Id == id2)?.Name ?? id2)
            .Take(20)
            .ToList();

        // Leaf nodes: have no outgoing edges
        var leafNodeIds = allNodeIds.Except(sources).ToList();
        var leafNames = leafNodeIds
            .Select(id2 => graph.Nodes.FirstOrDefault(n => n.Id == id2)?.Name ?? id2)
            .Take(20)
            .ToList();

        // Max depth via BFS from roots
        var maxDepth = 0;
        var adjacency = graph.Edges
            .Where(e => e.Relationship == EdgeRelationship.Contains)
            .GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Target).ToList());

        foreach (var rootId in rootNodeIds)
        {
            var queue = new Queue<(string id, int depth)>();
            var visited = new HashSet<string>();
            queue.Enqueue((rootId, 0));
            visited.Add(rootId);

            while (queue.Count > 0)
            {
                var (curId, depth) = queue.Dequeue();
                if (depth > maxDepth) maxDepth = depth;
                if (adjacency.TryGetValue(curId, out var children))
                {
                    foreach (var child in children.Where(c => visited.Add(c)))
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
            }
        }

        return Ok(new GraphStatsResponse
        {
            TotalNodes = graph.Nodes.Count,
            TotalEdges = graph.Edges.Count,
            NodeTypeCounts = graph.Nodes
                .GroupBy(n => n.Type.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            EdgeTypeCounts = graph.Edges
                .GroupBy(e => e.Relationship.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            MaxDepth = maxDepth,
            RootNodes = rootNames,
            LeafNodes = leafNames
        });
    }

    /// <summary>
    /// GET /api/repository/{id}/search?q=query&amp;kinds=Class,Function&amp;skip=0&amp;take=20
    /// Searches the repository index with optional kind filtering and pagination.
    /// </summary>
    [HttpGet("{id}/search")]
    public ActionResult<SearchResponse> Search(
        string id,
        [FromQuery] string q,
        [FromQuery] string? kinds = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("Query parameter 'q' is required.");
        }

        if (!_analysisCache.ContainsKey(id))
        {
            return NotFound("Repository not analyzed yet.");
        }

        var kindArray = string.IsNullOrWhiteSpace(kinds)
            ? null
            : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = _searchEngine.Search(id, q, kindArray, skip, take);
        var totalResults = _searchEngine.SearchCount(id, q, kindArray);
        var availableKinds = _searchEngine.GetAvailableKinds(id);

        return Ok(new SearchResponse
        {
            Query = q,
            TotalResults = totalResults,
            Skip = skip,
            Take = take,
            AvailableKinds = availableKinds,
            Results = results
        });
    }

    /// <summary>
    /// GET /api/repository/{id}/suggest?q=prefix
    /// Returns autocomplete suggestions for the given prefix.
    /// </summary>
    [HttpGet("{id}/suggest")]
    public ActionResult<SuggestResponse> Suggest(string id, [FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        {
            return Ok(new SuggestResponse { Prefix = q ?? "", Suggestions = [] });
        }

        if (!_analysisCache.ContainsKey(id))
        {
            return NotFound("Repository not analyzed yet.");
        }

        var suggestions = ((SearchEngine)_searchEngine).Suggest(id, q, 10);

        return Ok(new SuggestResponse
        {
            Prefix = q,
            Suggestions = suggestions.Select(s => new SearchSuggestionDto
            {
                Text = s.Text,
                Kind = s.Kind,
                FilePath = s.FilePath
            }).ToList()
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

    /// <summary>
    /// Generates a deterministic ID from a GitHub URL (e.g. "owner_repo").
    /// </summary>
    private static string GenerateRepoId(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var parts = trimmed.Split('/');
        if (parts.Length >= 2)
        {
            var owner = parts[^2];
            var repo = parts[^1].Replace(".git", "", StringComparison.OrdinalIgnoreCase);
            return $"{owner}_{repo}";
        }
        return trimmed.Replace("/", "_").Replace(":", "_");
    }
}
