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
        services.AddSingleton<ILanguageParser, CParser>();
        services.AddSingleton<ILanguageParser, CppParser>();
        services.AddSingleton<ILanguageParser, SwiftParser>();
        services.AddSingleton<ILanguageParser, RustParser>();
        services.AddSingleton<ILanguageParser, SqlParser>();
        services.AddSingleton<ILanguageParser, ScalaParser>();
        services.AddSingleton<ILanguageParser, KotlinParser>();
        services.AddSingleton<ILanguageParser, PhpParser>();
        services.AddSingleton<ILanguageParser, RubyParser>();
        services.AddSingleton<ILanguageParser, DartParser>();
        services.AddSingleton<ILanguageParser, LuaParser>();
        services.AddSingleton<ILanguageParser, PerlParser>();
        services.AddSingleton<ILanguageParser, RParser>();
        services.AddSingleton<ILanguageParser, HaskellParser>();
        services.AddSingleton<ILanguageParser, ElixirParser>();
        services.AddSingleton<IRepositoryAnalyzer, RepositoryAnalyzer>();
        services.AddHttpClient<ISummaryEnricher, OpenAiSummaryEnricher>();
        services.AddHttpClient<IPrDiffFetcher, GitHubPrDiffFetcher>();
        services.AddSingleton<PrImpactAnalyzer>();

        return services;
    }
}
