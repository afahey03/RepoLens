using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RepoLens.Analysis;
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
    private readonly ISummaryEnricher _summaryEnricher;
    private readonly IPrDiffFetcher _prDiffFetcher;
    private readonly PrImpactAnalyzer _prImpactAnalyzer;
    private readonly ILogger<RepositoryController> _logger;

    public RepositoryController(
        IRepositoryDownloader downloader,
        IRepositoryAnalyzer analyzer,
        ISearchEngine searchEngine,
        IAnalysisCache cache,
        IAnalysisProgressTracker progress,
        ISummaryEnricher summaryEnricher,
        IPrDiffFetcher prDiffFetcher,
        PrImpactAnalyzer prImpactAnalyzer,
        ILogger<RepositoryController> logger)
    {
        _downloader = downloader;
        _analyzer = analyzer;
        _searchEngine = searchEngine;
        _cache = cache;
        _progress = progress;
        _summaryEnricher = summaryEnricher;
        _prDiffFetcher = prDiffFetcher;
        _prImpactAnalyzer = prImpactAnalyzer;
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

        // Return cached result immediately (unless force re-analyze requested)
        // If an OpenAI key is provided and the cached summary is template-only,
        // kick off background enrichment rather than returning the cached result.
        if (_cache.Has(repoId) && !request.ForceReanalyze)
        {
            var existing = _cache.Get(repoId)!;
            var needsEnrichment = !string.IsNullOrWhiteSpace(request.OpenAiApiKey)
                                  && existing.Overview.SummarySource != "ai";

            if (!needsEnrichment)
            {
                _searchEngine.BuildIndex(repoId, existing.Symbols, existing.Files);
                _logger.LogInformation("Returning cached analysis for {RepoId}", repoId);
                return Ok(new AnalyzeResponse { RepositoryId = repoId, Status = "completed" });
            }

            // Background-enrich the cached overview with LLM
            _logger.LogInformation("Cached analysis found but needs LLM enrichment for {RepoId}", repoId);
            _progress.Start(repoId);
            var enrichKey = request.OpenAiApiKey;
            _ = Task.Run(async () =>
            {
                try
                {
                    _progress.Update(repoId, AnalysisStage.Indexing, "Generating AI summary...", 80);
                    var llmSummary = await _summaryEnricher.EnrichAsync(
                        existing.Overview, null, enrichKey, CancellationToken.None);
                    if (llmSummary is not null)
                    {
                        existing.Overview.Summary = llmSummary;
                        existing.Overview.SummarySource = "ai";
                        _cache.Store(repoId, existing);
                        _logger.LogInformation("LLM summary enriched for cached {RepoId}", repoId);
                    }
                    _searchEngine.BuildIndex(repoId, existing.Symbols, existing.Files);
                    _progress.Complete(repoId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LLM enrichment failed for cached {RepoId}", repoId);
                    _searchEngine.BuildIndex(repoId, existing.Symbols, existing.Files);
                    _progress.Complete(repoId); // Still complete — just without AI summary
                }
            });
            return Ok(new AnalyzeResponse { RepositoryId = repoId, Status = "analyzing" });
        }

        // If already running, return current status
        if (_progress.IsRunning(repoId))
        {
            return Ok(new AnalyzeResponse { RepositoryId = repoId, Status = "analyzing" });
        }

        // Capture previous analysis for incremental mode
        CachedAnalysis? previousAnalysis = request.ForceReanalyze ? _cache.Get(repoId) : null;

        _logger.LogInformation("Starting {Mode} analysis for {Url} (id: {RepoId})",
            previousAnalysis is not null ? "incremental" : "full",
            request.RepositoryUrl, repoId);
        _progress.Start(repoId);

        // Fire-and-forget background analysis
        var url = request.RepositoryUrl;
        var token = request.GitHubToken;
        var openAiKey = request.OpenAiApiKey;
        var prevAnalysis = previousAnalysis;
        _ = Task.Run(async () =>
        {
            try
            {
                // Stage 1: Download (clear local cache first when re-analyzing)
                _progress.Update(repoId, AnalysisStage.Downloading, "Downloading repository...", 10);
                if (prevAnalysis is not null)
                {
                    _downloader.ClearLocalCache(url);
                }
                var repoPath = await _downloader.DownloadAsync(url, CancellationToken.None, token);
                _logger.LogInformation("Repository downloaded to {Path}", repoPath);

                // Stage 2: Scanning & Parsing (full or incremental)
                FullAnalysisResult analysis;
                if (prevAnalysis is not null)
                {
                    _progress.Update(repoId, AnalysisStage.Scanning, "Scanning for changes...", 30);
                    analysis = await _analyzer.AnalyzeIncrementalAsync(repoPath, url, prevAnalysis, CancellationToken.None);
                }
                else
                {
                    _progress.Update(repoId, AnalysisStage.Scanning, "Scanning files...", 30);
                    analysis = await _analyzer.AnalyzeFullAsync(repoPath, url, CancellationToken.None);
                }
                _logger.LogInformation("Full analysis complete: {Files} files, {Symbols} symbols, {Nodes} nodes",
                    analysis.Files.Count, analysis.Symbols.Count, analysis.Graph.Nodes.Count);

                // Stage 3: Build search index
                _progress.Update(repoId, AnalysisStage.Indexing, "Building search index...", 80);
                _searchEngine.BuildIndex(repoId, analysis.Symbols, analysis.Files);
                _logger.LogInformation("Search index built for {RepoId}", repoId);

                // Stage 3b: LLM summary enrichment (optional)
                _progress.Update(repoId, AnalysisStage.Indexing, "Generating AI summary...", 90);
                var llmSummary = await _summaryEnricher.EnrichAsync(
                    analysis.Overview, analysis.ReadmeContent, openAiKey, CancellationToken.None);
                if (llmSummary is not null)
                {
                    analysis.Overview.Summary = llmSummary;
                    analysis.Overview.SummarySource = "ai";
                    _logger.LogInformation("LLM summary applied for {RepoId}", repoId);
                }

                // Stage 4: Cache
                _cache.Store(repoId, new CachedAnalysis
                {
                    Overview = analysis.Overview,
                    Graph = analysis.Graph,
                    Symbols = analysis.Symbols,
                    Files = analysis.Files,
                    FileHashes = analysis.Files
                        .Where(f => f.ContentHash is not null)
                        .ToDictionary(f => f.RelativePath, f => f.ContentHash!)
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
    /// POST /api/repository/{id}/pr-impact
    /// Analyzes the impact of a pull request against the cached analysis.
    /// The repository must already be analyzed.
    /// </summary>
    [HttpPost("{id}/pr-impact")]
    public async Task<ActionResult<PrImpactResponse>> AnalyzePrImpact(
        string id,
        [FromBody] PrImpactRequest request)
    {
        if (request.PrNumber <= 0)
            return BadRequest("PR number must be a positive integer.");

        var cached = _cache.Get(id);
        if (cached is null)
            return NotFound("Repository not analyzed yet. Analyze the repository first.");

        // Parse owner/repo from the cached overview URL
        var repoUrl = cached.Overview.Url;
        var (owner, repo) = ParseOwnerRepo(repoUrl);
        if (owner is null || repo is null)
            return BadRequest("Could not determine owner/repo from the analyzed repository URL.");

        try
        {
            var changedFiles = await _prDiffFetcher.FetchDiffAsync(
                owner, repo, request.PrNumber, request.GitHubToken);

            var impact = _prImpactAnalyzer.Analyze(request.PrNumber, changedFiles, cached);
            return Ok(impact);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            return NotFound($"Pull request #{request.PrNumber} was not found in {owner}/{repo}. Please check the PR number and try again.");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
        {
            return StatusCode(403, $"Access denied for PR #{request.PrNumber}. If this is a private repository, provide a valid GitHub token with repo access.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch PR #{Pr} diff", request.PrNumber);
            return StatusCode(502, $"Failed to fetch PR diff from GitHub. Please try again later.");
        }
    }

    /// <summary>
    /// Extracts owner and repo from a GitHub URL.
    /// </summary>
    private static (string? Owner, string? Repo) ParseOwnerRepo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, null);
        var trimmed = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return (null, null);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2) return (null, null);
        return (segments[0], segments[1].Replace(".git", "", StringComparison.OrdinalIgnoreCase));
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
