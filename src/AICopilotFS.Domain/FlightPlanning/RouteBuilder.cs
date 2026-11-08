namespace AICopilotFS.Domain.FlightPlanning;

/// <summary>Interface for route building.</summary>
public interface IRouteBuilder
{
    /// <summary>Builds a departure to destination route.</summary>
    FlightPlan BuildRoute(string departureIcao, string destinationIcao, string departureRunway);
}
