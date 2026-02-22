using System.Collections.Concurrent;
using RepoLens.Shared.Contracts;
using RepoLens.Shared.Models;

namespace RepoLens.Engine;

/// <summary>
/// In-memory progress tracker for active repository analyses.
/// </summary>
public class AnalysisProgressTracker : IAnalysisProgressTracker
{
    private readonly ConcurrentDictionary<string, AnalysisProgress> _progress = new();

    public void Start(string repoId)
    {
        _progress[repoId] = new AnalysisProgress
        {
            RepositoryId = repoId,
            Stage = AnalysisStage.Queued,
            StageLabel = "Queued",
            PercentComplete = 0
        };
    }

    public void Update(string repoId, AnalysisStage stage, string label, int percentComplete)
    {
        _progress[repoId] = new AnalysisProgress
        {
            RepositoryId = repoId,
            Stage = stage,
            StageLabel = label,
            PercentComplete = Math.Clamp(percentComplete, 0, 100)
        };
    }

    public void Complete(string repoId)
    {
        _progress[repoId] = new AnalysisProgress
        {
            RepositoryId = repoId,
            Stage = AnalysisStage.Completed,
            StageLabel = "Completed",
            PercentComplete = 100
        };
    }

    public void Fail(string repoId, string error)
    {
        _progress[repoId] = new AnalysisProgress
        {
            RepositoryId = repoId,
            Stage = AnalysisStage.Failed,
            StageLabel = "Failed",
            PercentComplete = 0,
            Error = error
        };
    }

    public AnalysisProgress? Get(string repoId)
    {
        return _progress.TryGetValue(repoId, out var p) ? p : null;
    }

    public void Remove(string repoId)
    {
        _progress.TryRemove(repoId, out _);
    }

    public bool IsRunning(string repoId)
    {
        return _progress.TryGetValue(repoId, out var p)
            && p.Stage != AnalysisStage.Completed
            && p.Stage != AnalysisStage.Failed;
    }
}
