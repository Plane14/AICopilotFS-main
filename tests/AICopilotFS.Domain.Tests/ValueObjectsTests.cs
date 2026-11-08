using AICopilotFS.Domain.ValueObjects;

namespace AICopilotFS.Domain.Tests.ValueObjects;

public class GeoPositionTests
{
    [Fact]
    public void DistanceNm_SamePoint_ReturnsZero()
    {
        var pos = new GeoPosition(45.0, -122.0);
        var dist = pos.DistanceNm(pos);
        Assert.Equal(0, dist, 3);
    }

    [Fact]
    public void DistanceNm_KnownPoints_ReturnsExpected()
    {
        // Approximate distance from Seattle to Portland (~130 NM)
        var seattle = new GeoPosition(47.6062, -122.3321);
        var portland = new GeoPosition(45.5152, -122.6784);
        var dist = seattle.DistanceNm(portland);
        Assert.InRange(dist, 120, 150);
    }

    [Fact]
    public void BearingDegreesTo_North_Returns360Or0()
    {
        var pos = new GeoPosition(45.0, -122.0);
        var north = pos.Destination(0, 10);
        var bearing = pos.BearingDegreesTo(north);
        Assert.InRange(bearing, 355, 5); // Close to north (wrapping)
    }

    [Fact]
    public void Destination_FromKnownPoint_ReturnsExpected()
    {
        var pos = new GeoPosition(0, 0);
        var dest = pos.Destination(90, 60); // 60 NM east at equator = 1 degree
        Assert.InRange(dest.LongitudeDegrees, 0.9, 1.1);
        Assert.InRange(dest.LatitudeDegrees, -0.1, 0.1);
    }

    [Fact]
    public void Parse_ValidString_ReturnsPosition()
    {
        var pos = GeoPosition.Parse("45.0,-122.0");
        Assert.Equal(45.0, pos.LatitudeDegrees);
        Assert.Equal(-122.0, pos.LongitudeDegrees);
    }

    [Fact]
    public void Parse_InvalidString_Throws()
    {
        Assert.Throws<FormatException>(() => GeoPosition.Parse("invalid"));
    }
}

public class AltitudeFeetTests
{
    [Fact]
    public void FromMeters_ConvertsCorrectly()
    {
        var alt = AltitudeFeet.FromMeters(1000);
        Assert.InRange(alt.Feet, 3280, 3281);
    }

    [Fact]
    public void IsValid_NaN_ReturnsFalse()
    {
        var alt = AltitudeFeet.Invalid;
        Assert.False(alt.IsValid);
    }
}

public class SpeedKnotsTests
{
    [Fact]
    public void IsValid_Zero_ReturnsTrue()
    {
        Assert.True(SpeedKnots.Zero.IsValid);
    }
}

public class HeadingDegreesTests
{
    [Fact]
    public void Normalized_370_Returns10()
    {
        var h = new HeadingDegrees(370);
        var n = h.Normalized();
        Assert.Equal(10, n.Degrees);
    }

    [Fact]
    public void Normalized_Negative10_Returns350()
    {
        var h = new HeadingDegrees(-10);
        var n = h.Normalized();
        Assert.Equal(350, n.Degrees);
    }
}

public class SquawkCodeTests
{
    [Fact]
    public void FromDecimal_1200_ReturnsConspicuity()
    {
        var code = SquawkCode.FromDecimal(1200);
        Assert.Equal("1200", code.Code);
    }

    [Fact]
    public void FromDecimal_7700_ReturnsConspicuity_EmergencyReserved()
    {
        var code = SquawkCode.FromDecimal(7700);
        Assert.Equal("1200", code.Code); // Emergency codes not assignable
    }

    [Fact]
    public void TryCreate_ValidOctal_ReturnsTrue()
    {
        Assert.True(SquawkCode.TryCreate("4271", out var code));
        Assert.Equal("4271", code.Code);
    }

    [Fact]
    public void TryCreate_InvalidOctal_ReturnsFalse()
    {
        Assert.False(SquawkCode.TryCreate("8000", out _));
    }

    [Fact]
    public void TryCreate_TooShort_ReturnsFalse()
    {
        Assert.False(SquawkCode.TryCreate("123", out _));
    }
}
