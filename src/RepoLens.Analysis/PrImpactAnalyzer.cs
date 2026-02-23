using Microsoft.Extensions.Logging;
using RepoLens.Shared.Contracts;
using RepoLens.Shared.DTOs;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis;

/// <summary>
/// Cross-references a PR diff against a cached repository analysis to
/// determine which symbols, graph edges, and downstream files are affected.
/// </summary>
public class PrImpactAnalyzer
{
    private readonly ILogger<PrImpactAnalyzer> _logger;

    public PrImpactAnalyzer(ILogger<PrImpactAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyzes the impact of a PR's changed files against the cached analysis.
    /// </summary>
    public PrImpactResponse Analyze(
        int prNumber,
        List<PrChangedFile> changedFiles,
        CachedAnalysis cached)
    {
        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in changedFiles)
        {
            changedPaths.Add(NormalizePath(f.FilePath));
            if (f.PreviousFilePath is not null)
                changedPaths.Add(NormalizePath(f.PreviousFilePath));
        }

        _logger.LogInformation("PR #{Pr}: analyzing impact for {Count} changed paths", prNumber, changedPaths.Count);

        // ── Build file path → language lookup ──
        var fileLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fi in cached.Files)
            fileLanguages[NormalizePath(fi.RelativePath)] = fi.Language;

        // ── Changed files with symbol counts ──
        var symbolsByFile = new Dictionary<string, List<SymbolInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in cached.Symbols)
        {
            var normPath = NormalizePath(sym.FilePath);
            if (!symbolsByFile.TryGetValue(normPath, out var list))
            {
                list = [];
                symbolsByFile[normPath] = list;
            }
            list.Add(sym);
        }

        var fileImpacts = new List<PrFileImpact>();
        foreach (var f in changedFiles)
        {
            var norm = NormalizePath(f.FilePath);
            fileLanguages.TryGetValue(norm, out var lang);
            symbolsByFile.TryGetValue(norm, out var fileSyms);

            fileImpacts.Add(new PrFileImpact
            {
                FilePath = f.FilePath,
                Status = f.Status,
                Additions = f.Additions,
                Deletions = f.Deletions,
                Language = lang,
                PreviousFilePath = f.PreviousFilePath,
                SymbolCount = fileSyms?.Count ?? 0
            });
        }

        // ── Affected symbols (defined in changed files) ──
        var affectedSymbols = new List<PrSymbolImpact>();
        foreach (var path in changedPaths)
        {
            if (symbolsByFile.TryGetValue(path, out var syms))
            {
                foreach (var s in syms)
                {
                    affectedSymbols.Add(new PrSymbolImpact
                    {
                        Name = s.Name,
                        Kind = s.Kind.ToString(),
                        FilePath = s.FilePath,
                        Line = s.Line,
                        ParentSymbol = s.ParentSymbol
                    });
                }
            }
        }

        // ── Build node IDs from changed files (for graph edge matching) ──
        // Changed file paths are graph node IDs for File nodes.
        // Also collect class/interface node IDs defined in changed files.
        var changedNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in changedPaths)
            changedNodeIds.Add(path);

        foreach (var node in cached.Graph.Nodes)
        {
            if (node.FilePath is not null && changedPaths.Contains(NormalizePath(node.FilePath)))
                changedNodeIds.Add(node.Id);
        }

        // ── Affected edges (source or target is a changed node) ──
        var affectedEdges = new List<PrEdgeImpact>();
        var downstreamPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in cached.Graph.Edges)
        {
            var sourceMatch = changedNodeIds.Contains(edge.Source);
            var targetMatch = changedNodeIds.Contains(edge.Target);

            if (sourceMatch || targetMatch)
            {
                affectedEdges.Add(new PrEdgeImpact
                {
                    Source = edge.Source,
                    Target = edge.Target,
                    Relationship = edge.Relationship.ToString(),
                    ImpactSide = sourceMatch ? "source" : "target"
                });

                // If something imports a changed file → it's a downstream dependency
                if (targetMatch && edge.Relationship == EdgeRelationship.Imports)
                {
                    // The source file depends on the changed target
                    var sourcePath = edge.Source;
                    if (!changedPaths.Contains(sourcePath))
                        downstreamPaths.Add(sourcePath);
                }
            }
        }

        // ── Languages touched ──
        var languages = new HashSet<string>();
        foreach (var fi in fileImpacts)
        {
            if (fi.Language is not null)
                languages.Add(fi.Language);
        }

        var response = new PrImpactResponse
        {
            PrNumber = prNumber,
            TotalFilesChanged = changedFiles.Count,
            TotalAdditions = changedFiles.Sum(f => f.Additions),
            TotalDeletions = changedFiles.Sum(f => f.Deletions),
            ChangedFiles = fileImpacts,
            AffectedSymbols = affectedSymbols,
            AffectedEdges = affectedEdges,
            DownstreamFiles = downstreamPaths.Order().ToList(),
            LanguagesTouched = languages.Order().ToList()
        };

        _logger.LogInformation(
            "PR #{Pr}: {Files} files, {Syms} affected symbols, {Edges} affected edges, {Down} downstream files",
            prNumber, response.TotalFilesChanged, affectedSymbols.Count,
            affectedEdges.Count, downstreamPaths.Count);

        return response;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');
}
