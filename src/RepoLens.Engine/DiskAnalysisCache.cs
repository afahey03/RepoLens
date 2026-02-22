using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Contracts;

namespace RepoLens.Engine;

/// <summary>
/// Stores analysis results in memory with JSON disk persistence.
/// Data survives application restarts — cached results are loaded on first access.
/// Cache directory: %LOCALAPPDATA%/RepoLens/cache/
/// </summary>
public class DiskAnalysisCache : IAnalysisCache
{
    private readonly ILogger<DiskAnalysisCache> _logger;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, CachedAnalysis> _memory = new();
    private bool _loadedFromDisk;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public DiskAnalysisCache(ILogger<DiskAnalysisCache> logger)
    {
        _logger = logger;

        // Allow override via environment variable (used in Docker)
        var envDir = Environment.GetEnvironmentVariable("REPOLENS_CACHE_DIR");
        _cacheDir = !string.IsNullOrWhiteSpace(envDir)
            ? envDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RepoLens", "cache");

        Directory.CreateDirectory(_cacheDir);
    }

    public bool Has(string repositoryId)
    {
        EnsureLoaded();
        return _memory.ContainsKey(repositoryId);
    }

    public void Store(string repositoryId, CachedAnalysis analysis)
    {
        EnsureLoaded();
        _memory[repositoryId] = analysis;

        // Persist to disk asynchronously (fire-and-forget, errors logged)
        _ = Task.Run(() => WriteToDisk(repositoryId, analysis));
    }

    public CachedAnalysis? Get(string repositoryId)
    {
        EnsureLoaded();
        return _memory.TryGetValue(repositoryId, out var cached) ? cached : null;
    }

    public void Remove(string repositoryId)
    {
        _memory.TryRemove(repositoryId, out _);
        var path = GetFilePath(repositoryId);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cache file {Path}", path);
        }
    }

    public IReadOnlyList<string> GetCachedIds()
    {
        EnsureLoaded();
        return _memory.Keys.ToList();
    }

    // ─── Disk I/O ──────────────────────────────────────────────────────

    private void EnsureLoaded()
    {
        if (_loadedFromDisk) return;
        lock (this)
        {
            if (_loadedFromDisk) return;
            LoadFromDisk();
            _loadedFromDisk = true;
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            var files = Directory.GetFiles(_cacheDir, "*.json");
            _logger.LogInformation("Loading {Count} cached analyses from disk...", files.Length);

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var cached = JsonSerializer.Deserialize<CachedAnalysis>(json, JsonOptions);
                    if (cached is not null)
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        _memory[id] = cached;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load cache file {File}, skipping", file);
                }
            }

            _logger.LogInformation("Loaded {Count} cached analyses", _memory.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan cache directory");
        }
    }

    private void WriteToDisk(string repositoryId, CachedAnalysis analysis)
    {
        var path = GetFilePath(repositoryId);
        try
        {
            var json = JsonSerializer.Serialize(analysis, JsonOptions);
            File.WriteAllText(path, json);
            _logger.LogDebug("Persisted analysis cache for {RepoId} ({Size:N0} bytes)",
                repositoryId, new System.IO.FileInfo(path).Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cache for {RepoId}", repositoryId);
        }
    }

    private string GetFilePath(string repositoryId) =>
        Path.Combine(_cacheDir, $"{SanitizeFileName(repositoryId)}.json");

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
