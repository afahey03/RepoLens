using Microsoft.Extensions.DependencyInjection;
using RepoLens.Shared.Contracts;

namespace RepoLens.Engine;

/// <summary>
/// Registers Engine services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEngineServices(this IServiceCollection services)
    {
        services.AddSingleton<ISearchEngine, SearchEngine>();

        return services;
    }
}
