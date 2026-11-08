using AICopilotFS.Domain.State;

namespace AICopilotFS.Domain.Aircraft;

/// <summary>Interface for performance calculations.</summary>
public interface IPerformanceCalculator
{
    double ClimbRateFpm { get; }
    double DescentRateFpm { get; }
    double CruiseSpeed { get; }
}

/// <summary>Default performance calculator implementation.</summary>
public sealed class PerformanceCalculator : IPerformanceCalculator
{
    public double ClimbRateFpm { get; } = 1200;
    public double DescentRateFpm { get; } = 1500;
    public double CruiseSpeed { get; } = 140;
}
