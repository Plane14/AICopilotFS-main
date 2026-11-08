using AICopilotFS.Domain.Terrain;

namespace AICopilotFS.Domain.Tests.Terrain;

public class TerrainAnalyzerTests
{
    [Fact]
    public void RequiredAltitudeAheadFt_Default_ReturnsZero()
    {
        var analyzer = new TerrainAnalyzer();
        var result = analyzer.RequiredAltitudeAheadFt(new GeoPosition(0, 0), 10);
        Assert.Equal(0, result);
    }

    [Fact]
    public void IsPathBlocked_Default_ReturnsFalse()
    {
        var analyzer = new TerrainAnalyzer();
        var result = analyzer.IsPathBlocked(new GeoPosition(0, 0), 5000, 10);
        Assert.False(result);
    }
}
