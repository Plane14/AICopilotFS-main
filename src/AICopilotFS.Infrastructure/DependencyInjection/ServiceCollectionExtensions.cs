using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using AICopilotFS.Application.Abstractions;
using AICopilotFS.Application.Options;

namespace AICopilotFS.Infrastructure.DependencyInjection;

/// <summary>Service collection extensions for AICopilotFS.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAICopilotFS(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options
        services.Configure<SimulatorOptions>(configuration.GetSection("Simulator"));
        services.Configure<FlightOptions>(configuration.GetSection("Flight"));
        services.Configure<TerrainOptions>(configuration.GetSection("Terrain"));
        services.Configure<ContinuousFlightOptions>(configuration.GetSection("ContinuousFlight"));
        services.Configure<RealismOptions>(configuration.GetSection("Realism"));
        services.Configure<ChecklistOptions>(configuration.GetSection("Checklists"));

        // Domain services (singletons, stateless)
        services.AddSingleton<Domain.Atc.IFlightPhaseEngine, Domain.Atc.FlightPhaseEngine>();
        services.AddSingleton<Domain.Atc.IAtcStateEngine, Domain.Atc.AtcStateEngine>();
        services.AddSingleton<Domain.State.ITaxiRouter, Domain.State.TaxiRouter>();
        services.AddSingleton<Domain.State.IRouteBuilder, Domain.FlightPlanning.RouteBuilder>();
        services.AddSingleton<Domain.State.IPerformanceCalculator, Domain.Aircraft.PerformanceCalculator>();
        services.AddSingleton<Domain.State.ITerrainAnalyzer, Domain.Terrain.TerrainAnalyzer>();

        // Application services
        services.AddScoped<IFlightOrchestrator, Application.Flight.FlightOrchestrator>();
        services.AddScoped<IContinuousFlightService, Application.Continuous.ContinuousFlightService>();
        services.AddScoped<IChecklistService, Application.Checklists.ChecklistService>();
        services.AddScoped<IAutopilotService, Application.Flight.HybridAutopilotService>();
        services.AddScoped<IAtcService, Application.Atc.AtcService>();

        // Infrastructure — simulator connector (router pattern)
        services.AddSingleton<ISimulatorConnector, SimulatorConnectorRouter>();
        services.AddSingleton<MsfsSimConnectConnector>();
        services.AddSingleton<MockSimulatorConnector>();

        // Infrastructure — ATC transport
        services.AddSingleton<IAtcTransport, Infrastructure.Atc.AtcRequestEngine>();
        services.AddSingleton<Infrastructure.Atc.AtcScreenCapture>();
        services.AddSingleton<Infrastructure.Atc.AtcMenuScreenReader>();

        // Infrastructure — providers
        services.AddSingleton<ITerrainProvider, Infrastructure.Terrain.MsfsTerrainProvider>();
        services.AddSingleton<IWeatherProvider, Infrastructure.Weather.MsfsWeatherProvider>();
        services.AddSingleton<IFlightPlanProvider, Infrastructure.FlightPlanning.SimBriefFlightPlanProvider>();
        services.AddSingleton<IFlightPlanProvider, Infrastructure.FlightPlanning.BundledFlightPlanProvider>();
        services.AddSingleton<Infrastructure.FlightPlanning.FlightPlanProviderRouter>();

        // Testing: Mock terrain provider for offline unit tests
#if DEBUG
        services.AddSingleton<ITerrainProvider, Infrastructure.Terrain.MockTerrainProvider>();
#endif

        // Persistence
        services.AddSingleton<IFlightLogger, Infrastructure.Persistence.SqliteFlightLogger>();
        services.AddSingleton<ISettingsStore, Infrastructure.Persistence.JsonSettingsStore>();

        return services;
    }
}
