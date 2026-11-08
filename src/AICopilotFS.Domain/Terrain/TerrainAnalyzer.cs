using AICopilotFS.Domain.State;
using AICopilotFS.Domain.ValueObjects;

namespace AICopilotFS.Domain.Terrain;

/// <summary>Interface for terrain analysis.</summary>
public interface ITerrainAnalyzer
{
    double RequiredAltitudeAheadFt(GeoPosition position, double aheadNm);
    bool IsPathBlocked(GeoPosition position, double currentAltitudeFt, double lookAheadNm);
}

/// <summary>Default terrain analyzer implementation.</summary>
public sealed class TerrainAnalyzer : ITerrainAnalyzer
{
    public double RequiredAltitudeAheadFt(GeoPosition position, double aheadNm) => 0;

    public bool IsPathBlocked(GeoPosition position, double currentAltitudeFt, double lookAheadNm) => false;
}

/// <summary>Terrain analysis segment.</summary>
public sealed record TerrainAnalysisSegment
{
    public double DistanceNm { get; init; }
    public double TerrainElevationFt { get; init; }
    public double RequiredAltitudeFt { get; init; }
}
