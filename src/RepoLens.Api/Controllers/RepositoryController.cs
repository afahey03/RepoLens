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
    private readonly IAnalysisCache _cache;
    private readonly IAnalysisProgressTracker _progress;
    private readonly ILogger<RepositoryController> _logger;

    public RepositoryController(
        IRepositoryDownloader downloader,
        IRepositoryAnalyzer analyzer,
        ISearchEngine searchEngine,
        IAnalysisCache cache,
        IAnalysisProgressTracker progress,
        ILogger<RepositoryController> logger)
    {
        _downloader = downloader;
        _analyzer = analyzer;
        _searchEngine = searchEngine;
        _cache = cache;
        _progress = progress;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/repository/analyze
    /// Kicks off analysis of a public GitHub repository.
    /// Returns immediately — poll GET /progress for status.
    /// </summary>
    [HttpPost("analyze")]
    public ActionResult<AnalyzeResponse> Analyze(
        [FromBody] AnalyzeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryUrl))
        {
            return BadRequest("Repository URL is required.");
        }

        var repoId = GenerateRepoId(request.RepositoryUrl);

        // Return cached result immediately
        if (_cache.Has(repoId))
        {
            var existing = _cache.Get(repoId)!;
            _searchEngine.BuildIndex(repoId, existing.Symbols, existing.Files);
            _logger.LogInformation("Returning cached analysis for {RepoId}", repoId);
            return Ok(new AnalyzeResponse { RepositoryId = repoId, Status = "completed" });
        }

        // If already running, return current status
        if (_progress.IsRunning(repoId))
        {
            return Ok(new AnalyzeResponse { RepositoryId = repoId, Status = "analyzing" });
        }

        _logger.LogInformation("Starting analysis for {Url} (id: {RepoId})", request.RepositoryUrl, repoId);
        _progress.Start(repoId);

        // Fire-and-forget background analysis
        var url = request.RepositoryUrl;
        var token = request.GitHubToken;
        _ = Task.Run(async () =>
        {
            try
            {
                // Stage 1: Download
                _progress.Update(repoId, AnalysisStage.Downloading, "Downloading repository...", 10);
                var repoPath = await _downloader.DownloadAsync(url, CancellationToken.None, token);
                _logger.LogInformation("Repository downloaded to {Path}", repoPath);

                // Stage 2: Scanning & Parsing
                _progress.Update(repoId, AnalysisStage.Scanning, "Scanning files...", 30);
                var analysis = await _analyzer.AnalyzeFullAsync(repoPath, url, CancellationToken.None);
                _logger.LogInformation("Full analysis complete: {Files} files, {Symbols} symbols, {Nodes} nodes",
                    analysis.Files.Count, analysis.Symbols.Count, analysis.Graph.Nodes.Count);

                // Stage 3: Build search index
                _progress.Update(repoId, AnalysisStage.Indexing, "Building search index...", 80);
                _searchEngine.BuildIndex(repoId, analysis.Symbols, analysis.Files);
                _logger.LogInformation("Search index built for {RepoId}", repoId);

                // Stage 4: Cache
                _cache.Store(repoId, new CachedAnalysis
                {
                    Overview = analysis.Overview,
                    Graph = analysis.Graph,
                    Symbols = analysis.Symbols,
                    Files = analysis.Files
                });

                _progress.Complete(repoId);
                _logger.LogInformation("Analysis complete for {RepoId}", repoId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analysis failed for {Url}", url);
                _progress.Fail(repoId, ex.Message);
            }
        });

        return Ok(new AnalyzeResponse { RepositoryId = repoId, Status = "analyzing" });
    }

    /// <summary>
    /// GET /api/repository/{id}/progress
    /// Poll this endpoint for real-time analysis progress.
    /// </summary>
    [HttpGet("{id}/progress")]
    public ActionResult<AnalysisProgress> GetProgress(string id)
    {
        // If already cached, analysis is done
        if (_cache.Has(id))
        {
            return Ok(new AnalysisProgress
            {
                RepositoryId = id,
                Stage = AnalysisStage.Completed,
                StageLabel = "Completed",
                PercentComplete = 100
            });
        }

        var progress = _progress.Get(id);
        if (progress is null)
            return NotFound("No analysis found for this repository.");

        return Ok(progress);
    }

    /// <summary>
    /// GET /api/repository/{id}/overview
    /// Returns the repository overview.
    /// </summary>
    [HttpGet("{id}/overview")]
    public ActionResult<RepositoryOverview> GetOverview(string id)
    {
        var result = _cache.Get(id);
        if (result is null)
            return NotFound("Repository not analyzed yet.");

        return Ok(result.Overview);
    }

    /// <summary>
    /// GET /api/repository/{id}/architecture
    /// Returns the architecture graph.
    /// </summary>
    [HttpGet("{id}/architecture")]
    public ActionResult<ArchitectureResponse> GetArchitecture(string id)
    {
        var result = _cache.Get(id);
        if (result is null)
            return NotFound("Repository not analyzed yet.");

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
        var result = _cache.Get(id);
        if (result is null)
            return NotFound("Repository not analyzed yet.");

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

        if (!_cache.Has(id))
            return NotFound("Repository not analyzed yet.");

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

        if (!_cache.Has(id))
            return NotFound("Repository not analyzed yet.");

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
