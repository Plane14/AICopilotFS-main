using AICopilotFS.Domain.Geo;

namespace AICopilotFS.Domain.Tests.Geo;

public class GeoMathTests
{
    [Fact]
    public void NormalizeDeg_370_Returns10()
    {
        var result = GeoMath.NormalizeDeg(370);
        Assert.Equal(10, result, 5);
    }

    [Fact]
    public void NormalizeDeg_Negative10_Returns350()
    {
        var result = GeoMath.NormalizeDeg(-10);
        Assert.Equal(350, result, 5);
    }

    [Fact]
    public void AngleDelta_SmallDifference_ReturnsSignedDiff()
    {
        var result = GeoMath.AngleDelta(100, 90);
        Assert.Equal(10, result, 5);
    }

    [Fact]
    public void AngleDelta_WrapAround_ReturnsShortestPath()
    {
        var result = GeoMath.AngleDelta(350, 10);
        Assert.Equal(20, result, 5);
    }

    [Fact]
    public void DistanceNm_SamePoint_ReturnsZero()
    {
        var result = GeoMath.DistanceNm(45.0, -122.0, 45.0, -122.0);
        Assert.Equal(0, result, 5);
    }

    [Fact]
    public void DistanceNm_KnownPoints_ReturnsExpected()
    {
        // Seattle to Portland approx 130 NM
        var dist = GeoMath.DistanceNm(47.6062, -122.3321, 45.5152, -122.6784);
        Assert.InRange(dist, 120, 150);
    }

    [Fact]
    public void BearingDeg_North_ReturnsZero()
    {
        var result = GeoMath.BearingDeg(0, 0, 1, 0);
        Assert.InRange(result, 355, 5);
    }

    [Fact]
    public void DestinationNm_EastAtEquator_ApproxOneDegreePer60Nm()
    {
        var (lat, lon) = GeoMath.DestinationNm(0, 0, 90, 60);
        Assert.InRange(lon, 0.9, 1.1);
        Assert.InRange(lat, -0.1, 0.1);
    }

    [Fact]
    public void FeetPerMeter_Constant_IsCorrect()
    {
        Assert.Equal(3.280839895, GeoMath.FeetPerMeter, 6);
    }

    [Fact]
    public void MetersPerNm_Constant_IsCorrect()
    {
        Assert.Equal(1852.0, GeoMath.MetersPerNm);
    }
}
