using Microsoft.Extensions.DependencyInjection;
using RepoLens.Analysis.Parsers;
using RepoLens.Shared.Contracts;

namespace RepoLens.Analysis;

/// <summary>
/// Registers Analysis services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisServices(this IServiceCollection services)
    {
        services.AddHttpClient<IRepositoryDownloader, GitHubRepositoryDownloader>();
        services.AddSingleton<ILanguageParser, RoslynCSharpParser>();
        services.AddSingleton<ILanguageParser, JavaScriptTypeScriptParser>();
        services.AddSingleton<ILanguageParser, PythonParser>();
        services.AddSingleton<ILanguageParser, JavaParser>();
        services.AddSingleton<ILanguageParser, GoParser>();
        services.AddSingleton<IRepositoryAnalyzer, RepositoryAnalyzer>();
        services.AddHttpClient<ISummaryEnricher, OpenAiSummaryEnricher>();

        return services;
    }
}
