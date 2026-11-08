using System.Collections.Generic;
using System.Linq;

using AICopilotFS.Domain.State;
using AICopilotFS.Domain.ValueObjects;

namespace AICopilotFS.Domain.Airports;

/// <summary>Aircraft traffic monitor for conflict assessment.</summary>
public sealed record TrafficAssessment
{
    public bool RunwayOccupied { get; init; }
    public bool OnFinal { get; init; }
    public bool AirborneConflict { get; init; }
    public IReadOnlyList<TrafficSnapshot> Conflicts { get; init; } = new List<TrafficSnapshot>();
}

/// <summary>Interface for traffic assessment.</summary>
public interface ITrafficAssessor
{
    TrafficAssessment Assess(IReadOnlyList<TrafficSnapshot> traffic, AirportData? airport);
}
