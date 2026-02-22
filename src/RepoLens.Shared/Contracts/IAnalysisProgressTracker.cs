using RepoLens.Shared.Models;

namespace RepoLens.Shared.Contracts;

/// <summary>
/// Tracks analysis progress for repositories currently being analyzed.
/// </summary>
public interface IAnalysisProgressTracker
{
    void Start(string repoId);
    void Update(string repoId, AnalysisStage stage, string label, int percentComplete);
    void Complete(string repoId);
    void Fail(string repoId, string error);
    AnalysisProgress? Get(string repoId);
    void Remove(string repoId);
    bool IsRunning(string repoId);
}
