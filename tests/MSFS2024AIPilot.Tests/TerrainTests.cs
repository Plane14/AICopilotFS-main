using MSFS2024AIPilot.Core.Aircraft;
using MSFS2024AIPilot.Core.Geo;
using MSFS2024AIPilot.Core.Terrain;

namespace MSFS2024AIPilot.Tests;

public class TerrainTests
{
    private static List<(double lat, double lon)> StraightRoute()
        => new() { (47.0, 8.0), (48.0, 9.0) };

    [Fact]
    public void Required_altitude_adds_1000ft_below_mountain_threshold()
    {
        // 1000 m ≈ 3281 ft — below the 4500 ft mountainous threshold.
        var req = TerrainProfile.RequiredFtForElevationMeters(1000);
        Assert.Equal(1000 * GeoMath.FeetPerMeter + 1000, req, 1);
    }

    [Fact]
    public void Required_altitude_adds_2000ft_over_mountainous_terrain()
    {
        // 2000 m ≈ 6562 ft — mountainous.
        var req = TerrainProfile.RequiredFtForElevationMeters(2000);
        Assert.Equal(2000 * GeoMath.FeetPerMeter + 2000, req, 1);
    }

    [Fact]
    public void Build_marks_mountainous_when_high_terrain_present()
    {
        var route = StraightRoute();
        var pts = TerrainProfile.SampleRoutePoints(route);
        var samples = pts.Select((p, i) => new TerrainSample(
            p.lat, p.lon, i == pts.Count / 2 ? 2500 : 100)).ToList(); // 2500 m ≈ 8200 ft
        var profile = TerrainProfile.Build(samples)!;
        Assert.True(profile.Mountainous);
        Assert.False(profile.IsFallback);
        Assert.True(profile.MaxRequiredAltitudeFt > 9000);
    }

    [Fact]
    public void RequiredAhead_returns_max_in_window()
    {
        var route = StraightRoute();
        var pts = TerrainProfile.SampleRoutePoints(route, spacingNm: 2);
        var samples = pts.Select((p, i) => new TerrainSample(
            p.lat, p.lon, i == pts.Count - 2 ? 1500 : 100)).ToList(); // spike near the end
        var profile = TerrainProfile.Build(samples)!;

        // Query from the start with a long window — the spike is ahead.
        var ahead = profile.RequiredAheadFt(route[0].lat, route[0].lon, 500);
        Assert.True(ahead > 1500 * GeoMath.FeetPerMeter);

        // Query from past the spike — window no longer covers it.
        var nearEnd = pts[^1];
        var behind = profile.RequiredAheadFt(nearEnd.lat, nearEnd.lon, 2);
        Assert.True(behind < ahead);
    }

    [Fact]
    public void Fallback_floor_is_highest_airport_plus_3000()
    {
        var route = StraightRoute();
        var profile = TerrainProfile.Fallback(500, route); // 500 m ≈ 1640 ft + 3000
        var expected = 500 * GeoMath.FeetPerMeter + 3000;
        Assert.Equal(expected, profile.MaxRequiredAltitudeFt, 1);
        Assert.True(profile.IsFallback);
        Assert.Equal(expected, profile.RequiredAheadFt(route[0].lat, route[0].lon, 500), 1);
    }

    [Fact]
    public void Racetrack_alternates_heading_each_leg()
    {
        var rt = new Racetrack();
        rt.Reset(47.0, 8.0);
        var h1 = rt.Update(47.0, 8.0, 90);
        Assert.Equal(90, h1);
        // Fly 3 NM outbound — past the 2 NM leg length → inbound.
        var (lat2, lon2) = GeoMath.DestinationNm(47.0, 8.0, 90, 3);
        var h2 = rt.Update(lat2, lon2, 90);
        Assert.Equal(270, h2);
        // Fly 3 NM further → outbound again.
        var (lat3, lon3) = GeoMath.DestinationNm(lat2, lon2, 270, 3);
        var h3 = rt.Update(lat3, lon3, 90);
        Assert.Equal(90, h3);
    }
}
