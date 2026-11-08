using System.Collections.Generic;

using AICopilotFS.Domain.ValueObjects;

namespace AICopilotFS.Domain.FlightPlanning;

/// <summary>A single waypoint in a flight plan.</summary>
public sealed record FlightPlanWaypoint
{
    public string Name { get; init; } = string.Empty;
    public string Icao { get; init; } = string.Empty;
    public GeoPosition Position { get; init; } = GeoPosition.Zero;
    public AltitudeFeet AltitudeFeet { get; init; } = AltitudeFeet.Invalid;
    public bool HasPosition => Position != GeoPosition.Zero;
}

/// <summary>A complete flight plan.</summary>
public sealed record FlightPlan
{
    public string DepartureIcao { get; init; } = string.Empty;
    public string DestinationIcao { get; init; } = string.Empty;
    public string DepartureRunway { get; init; } = string.Empty;
    public string DestinationRunway { get; init; } = string.Empty;
    public AltitudeFeet? CruiseAltitudeFeet { get; init; }
    public string? Route { get; init; }
    public string? StandardInstrumentDeparture { get; init; }
    public List<FlightPlanWaypoint> Waypoints { get; init; } = new();
}

/// <summary>Interface for route building.</summary>
public interface IRouteBuilder
{
    FlightPlan BuildRoute(string departureIcao, string destinationIcao, string departureRunway);
}

/// <summary>Default route builder implementation.</summary>
public sealed class RouteBuilder : IRouteBuilder
{
    public FlightPlan BuildRoute(string departureIcao, string destinationIcao, string departureRunway)
    {
        return new FlightPlan
        {
            DepartureIcao = departureIcao,
            DestinationIcao = destinationIcao,
            DepartureRunway = departureRunway,
        };
    }
}
