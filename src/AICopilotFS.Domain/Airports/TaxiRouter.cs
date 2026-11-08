using AICopilotFS.Domain.State;

using AICopilotFS.Domain.ValueObjects;

namespace AICopilotFS.Domain.Airports;

/// <summary>Interface for taxi routing over the ground graph.</summary>
public interface ITaxiRouter
{
    TaxiRoute? FindRouteToHoldingPoint(AirportData airport, GeoPosition position, string runway);
}

/// <summary>Default taxi router implementation.</summary>
public sealed class TaxiRouter : ITaxiRouter
{
    public TaxiRoute? FindRouteToHoldingPoint(AirportData airport, GeoPosition position, string runway)
    {
        return new TaxiRoute { Found = false };
    }
}
