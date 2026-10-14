using Microsoft.Extensions.DependencyInjection;
using WhyLine.Agents.Abstractions;

namespace WhyLine.Agents.Foundry;

public static class FoundryServiceCollectionExtensions
{
    /// <summary>Replaces the template agents with Microsoft Foundry agents (Agent Framework).</summary>
    public static IServiceCollection AddFoundryNewsroomAgents(this IServiceCollection services, FoundryOptions options)
    {
        services.AddSingleton(new FoundryAgentFactory(options));
        services.AddSingleton<IAnalystAgent, FoundryAnalystAgent>();
        services.AddSingleton<INarratorAgent, FoundryNarratorAgent>();
        services.AddSingleton<ILocalizationAgent, FoundryLocalizationAgent>();
        services.AddSingleton<IRecapAgent, FoundryRecapAgent>();
        return services;
    }
}
