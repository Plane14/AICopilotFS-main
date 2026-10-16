using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Builds the payloads the navigation database request returns, in the same field order
/// the definitions in <see cref="NavDataLoader"/> declare them. The order is the contract:
/// SimConnect assembles a facility payload in the order the fields were added to the
/// definition, so a reader that reads them in any other order is reading garbage.
/// </summary>
internal static class NavPayloadBuilder
{
    public static byte[] Record(params (string name, object value)[] fields)
    {
        using var ms = new MemoryStream();

        void Int(int value) => ms.Write(BitConverter.GetBytes(value));
        void Float(float value) => ms.Write(BitConverter.GetBytes(value));
        void String(string value)
        {
            var buffer = new byte[8];
            Encoding.ASCII.GetBytes(value).CopyTo(buffer, 0);
            ms.Write(buffer);
        }

        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                case "TYPE": Int((int)value); break;
                case "SUFFIX": Int((int)value); break;
                case "RUNWAY_NUMBER": Int((int)value); break;
                case "RUNWAY_DESIGNATOR": Int((int)value); break;
                case "FAF_TYPE": Int((int)value); break;
                case "HAS_LNAV":
                case "HAS_LNAVVNAV":
                case "HAS_LP":
                case "HAS_LPV":
                case "N_TRANSITIONS":
                case "N_FINAL_APPROACH_LEGS":
                case "N_MISSED_APPROACH_LEGS":
                case "FLY_OVER":
                case "DISTANCE_MINUTE":
                case "TRUE_DEGREE":
                case "TURN_DIRECTION":
                case "APPROACH_ALT_DESC":
                case "IS_IAF":
                case "IS_IF":
                case "IS_FAF":
                case "IS_MAP":
                    Int((int)value); break;
                case "FIX_TYPE":
                case "ORIGIN_TYPE":
                case "ARC_CENTER_FIX_TYPE": Int((int)value); break;
                case "FIX_ICAO":
                case "FIX_REGION":
                case "ORIGIN_ICAO":
                case "ORIGIN_REGION":
                case "IAF_ICAO":
                case "IAF_REGION":
                case "ARC_CENTER_FIX_ICAO":
                case "ARC_CENTER_FIX_REGION":
                case "FAF_ICAO":
                case "FAF_REGION": String((string)value); break;
                case "MAGVAR":
                case "FAF_HEADING":
                case "FAF_ALTITUDE":
                case "MISSED_ALTITUDE":
                case "THETA":
                case "RHO":
                case "COURSE":
                case "ROUTE_DISTANCE":
                case "ALTITUDE1":
                case "ALTITUDE2":
                case "SPEED_LIMIT":
                case "VERTICAL_ANGLE":
                case "IAF_ALTITUDE": Float((float)value); break;
                case "FIX_LATITUDE":
                case "FIX_LONGITUDE":
                case "FIX_ALTITUDE":
                case "ORIGIN_LATITUDE":
                case "ORIGIN_LONGITUDE":
                case "ORIGIN_ALTITUDE":
                case "ARC_CENTER_FIX_LATITUDE":
                case "ARC_CENTER_FIX_LONGITUDE":
                case "ARC_CENTER_FIX_ALTITUDE": ms.Write(BitConverter.GetBytes((double)value)); break;
                case "ICAO":
                case "REGION": String((string)value); break;
                default: throw new ArgumentException($"unknown field {name}");
            }
        }

        return ms.ToArray();
    }

    public static FacilityDataRecord ApproachRecord(uint requestId, params (string, object)[] fields) =>
        new() { Type = FacilityDataType.Approach, UserRequestId = requestId, Payload = Record(fields) };

    public static FacilityDataRecord LegRecord(uint requestId, FacilityDataType type, IEnumerable<(string, object)> fields) =>
        new() { Type = type, UserRequestId = requestId, Payload = Record(fields.ToArray()) };

    public static FacilityDataRecord FixRecord(uint requestId, NavFix fix) => new()
    {
        Type = fix.Type switch
        {
            'V' => FacilityDataType.Vor,
            'N' => FacilityDataType.Ndb,
            _ => FacilityDataType.Waypoint
        },
        UserRequestId = requestId,
        Payload = fix.Type == 'W'
            ? FixRecord(fix.Icao, fix.Latitude, fix.Longitude, fix.AltitudeMeters)
            : new[]
            {
                BitConverter.GetBytes(fix.Latitude),
                BitConverter.GetBytes(fix.Longitude),
                BitConverter.GetBytes(fix.AltitudeMeters)
            }.SelectMany(b => b).ToArray()
    };

    /// <summary>
    /// A leg payload with the fields every leg carries, with sensible defaults for the
    /// geometry. A leg is a fix leg with one position, unless the caller says otherwise.
    /// </summary>
    public static List<(string, object)> Leg(
        LegPathType path = LegPathType.TrackToFix,
        string ident = "GLASG",
        double lat = 40.5,
        double lon = -73.5,
        double altitudeMeters = 450,
        int flyOver = 0,
        int distanceMinute = 0,
        int trueDegree = 0,
        int turnDirection = 0,
        double course = 250,
        double routeDistance = 5000,
        int altitudeDesc = 0,
        double altitude1 = 0,
        double altitude2 = 0,
        double speedLimit = 0,
        double verticalAngle = 0,
        double theta = 90,
        double rho = 4200,
        double arcCenterLat = 0,
        double arcCenterLon = 0,
        int isIaf = 0,
        int isIf = 0,
        int isFaf = 0,
        int isMap = 0) => new()
    {
        ("TYPE", (int)path),
        ("FIX_ICAO", ident),
        ("FIX_REGION", "K"),
        ("FIX_TYPE", (int)'W'),
        ("FIX_LATITUDE", lat),
        ("FIX_LONGITUDE", lon),
        ("FIX_ALTITUDE", altitudeMeters),
        ("FLY_OVER", flyOver),
        ("DISTANCE_MINUTE", distanceMinute),
        ("TRUE_DEGREE", trueDegree),
        ("TURN_DIRECTION", turnDirection),
        ("ORIGIN_ICAO", ""),
        ("ORIGIN_REGION", ""),
        ("ORIGIN_TYPE", (int)'W'),
        ("ORIGIN_LATITUDE", 0.0),
        ("ORIGIN_LONGITUDE", 0.0),
        ("ORIGIN_ALTITUDE", 0.0),
        ("THETA", (float)theta),
        ("RHO", (float)rho),
        ("COURSE", (float)course),
        ("ROUTE_DISTANCE", (float)routeDistance),
        ("APPROACH_ALT_DESC", altitudeDesc),
        ("ALTITUDE1", (float)altitude1),
        ("ALTITUDE2", (float)altitude2),
        ("SPEED_LIMIT", (float)speedLimit),
        ("VERTICAL_ANGLE", (float)verticalAngle),
        ("ARC_CENTER_FIX_ICAO", ""),
        ("ARC_CENTER_FIX_REGION", ""),
        ("ARC_CENTER_FIX_TYPE", (int)'W'),
        ("ARC_CENTER_FIX_LATITUDE", arcCenterLat),
        ("ARC_CENTER_FIX_LONGITUDE", arcCenterLon),
        ("ARC_CENTER_FIX_ALTITUDE", 0.0),
        ("IS_IAF", isIaf),
        ("IS_IF", isIf),
        ("IS_FAF", isFaf),
        ("IS_MAP", isMap),
    };

    public static byte[] FixRecord(string icao, double lat, double lon, double altitudeMeters)
    {
        using var ms = new MemoryStream();
        void Float64(double value) => ms.Write(BitConverter.GetBytes(value));
        void String(string value)
        {
            var buffer = new byte[8];
            Encoding.ASCII.GetBytes(value).CopyTo(buffer, 0);
            ms.Write(buffer);
        }

        Float64(lat);
        Float64(lon);
        Float64(altitudeMeters);
        String(icao);
        String("K");
        return ms.ToArray();
    }
}

public class NavDataLoaderErrorFilterTests
{
    [Fact]
    public void FacilityDefinitionErrorsAreRecognisedAsNavDataErrors()
    {
        Assert.True(NavDataLoader.IsNavDataSimConnectError(
            "SimConnect exception: NAME_UNRECOGNIZED (sendId=1, index=2) [add facility field 'TYPE' to definition 8101]"));
        Assert.True(NavDataLoader.IsNavDataSimConnectError(
            "SimConnect exception: ERROR (sendId=2, index=2) [request facility data for 'KJFK' (definition 8105, request 8201)]"));
    }

    [Fact]
    public void UnrelatedChecklistEventErrorsAreNotReportedAsNavDataErrors()
    {
        Assert.False(NavDataLoader.IsNavDataSimConnectError(
            "SimConnect exception: NAME_UNRECOGNIZED (sendId=558, index=2) [map event 'MASTER_ALTERNATOR_SET' to id 1011]"));
        Assert.False(NavDataLoader.IsNavDataSimConnectError(
            "SimConnect exception: ERROR (sendId=559, index=2) [transmit event 'MASTER_ALTERNATOR_SET' (data=1)]"));
    }
}

/// <summary>
/// The approach record layout: TYPE, SUFFIX, RUNWAY_NUMBER, RUNWAY_DESIGNATOR, FAF,
/// MISSED_ALTITUDE (in feet), the capability flags and the leg counts.
/// </summary>
public class ApproachRecordParserTests
{
    private static ApproachInfo ParseIls() => ApproachRecordParser.ParseApproach(
        NavPayloadBuilder.ApproachRecord(1,
            ("TYPE", (int)ApproachKind.Ils),
            ("SUFFIX", 0),
            ("RUNWAY_NUMBER", 4),
            ("RUNWAY_DESIGNATOR", 2),
            ("FAF_ICAO", "GAUGE"),
            ("FAF_REGION", "K6"),
            ("FAF_HEADING", 39.0f),
            ("FAF_ALTITUDE", 914.4f),            // metres
            ("FAF_TYPE", (int)'W'),
            ("MISSED_ALTITUDE", 6000.0f),       // feet
            ("HAS_LNAV", 1),
            ("HAS_LNAVVNAV", 0),
            ("HAS_LP", 0),
            ("HAS_LPV", 0),
            ("N_TRANSITIONS", 1),
            ("N_FINAL_APPROACH_LEGS", 3),
            ("N_MISSED_APPROACH_LEGS", 2)));

    [Fact]
    public void ReadsTheApproachIdentity()
    {
        var approach = ParseIls();

        Assert.Equal(ApproachKind.Ils, approach.Kind);
        // Runway 4 with designator 2 is runway 04R.
        Assert.Equal("04R", approach.Runway);
        Assert.Equal("", approach.Suffix);
    }

    [Fact]
    public void ReadsTheFinalApproachFixAndMissedAltitude()
    {
        var approach = ParseIls();

        // FAF_ALTITUDE is metres; MISSED_ALTITUDE is feet, and the pilot speaks feet.
        Assert.Equal("GAUGE", approach.FafIcao);
        Assert.Equal(3000, approach.FafAltitudeFt, 1);
        Assert.Equal(6000, approach.MissedAltitudeFt);
    }

    [Fact]
    public void ReadsTheCapabilityFlags()
    {
        var approach = ParseIls();

        Assert.True(approach.HasLnav);
        Assert.False(approach.HasLnavVnav);
        Assert.True(approach.HasLpv == false);
    }

    [Fact]
    public void AnRnavSuffixIsANameNotADesignator()
    {
        var approach = ApproachRecordParser.ParseApproach(NavPayloadBuilder.ApproachRecord(1,
            ("TYPE", (int)ApproachKind.Rnav),
            ("SUFFIX", 26),
            ("RUNWAY_NUMBER", 25),
            ("RUNWAY_DESIGNATOR", 1),
            ("FAF_ICAO", ""),
            ("FAF_REGION", ""),
            ("FAF_HEADING", 250.0f),
            ("FAF_ALTITUDE", 0f),
            ("FAF_TYPE", (int)'W'),
            ("MISSED_ALTITUDE", 4000.0f),
            ("HAS_LNAV", 1),
            ("HAS_LNAVVNAV", 1),
            ("HAS_LP", 0),
            ("HAS_LPV", 0),
            ("N_TRANSITIONS", 0),
            ("N_FINAL_APPROACH_LEGS", 2),
            ("N_MISSED_APPROACH_LEGS", 1)));

        Assert.Equal("Z", approach.Suffix);
        Assert.Equal("25L", approach.Runway);
        // RNAV-Z is what an LPV approach is spelled like on the field.
        Assert.True(approach.HasLnavVnav);
        Assert.Equal("25L|10|Z", approach.Key);
    }

    [Fact]
    public void FinalApproachFixIsTheFlaggedFix()
    {
        var approach = new ApproachInfo { FafIcao = "GAUGE" };
        approach.FinalLegs.Add(new ApproachLeg { Ident = "HOTEE", Latitude = 1, Longitude = 1, IsIf = true });
        approach.FinalLegs.Add(new ApproachLeg { Ident = "GAUGE", Latitude = 1, Longitude = 1, IsFaf = true });

        Assert.Equal("GAUGE", approach.FindFinalApproachFix()?.Ident);
    }

    [Fact]
    public void MissedApproachPointIsTheFixFlaggedAsTheMap()
    {
        var approach = new ApproachInfo();
        approach.FinalLegs.Add(new ApproachLeg { Ident = "GAUGE", Latitude = 1, Longitude = 1, IsFaf = true });
        approach.FinalLegs.Add(new ApproachLeg { Ident = "RW25", Latitude = 1, Longitude = 1, IsMap = true });

        Assert.Equal("RW25", approach.FindMissedApproachPoint()?.Ident);
    }
}

/// <summary>
/// The procedure leg layout. The leg payload is the one that has to line up: a reader
/// that is out of step with its definition produces plausible-looking garbage, which is
/// worse than a parse failure.
/// </summary>
public class ApproachLegParserTests
{
    [Fact]
    public void ReadsTheFixAndItsPosition()
    {
        var leg = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.FinalApproachLeg, NavPayloadBuilder.Leg(ident: "GLASG", lat: 40.6413, lon: -73.7781)));

        Assert.Equal("GLASG", leg.Ident);
        Assert.Equal("K", leg.Region);
        Assert.Equal('W', leg.FixType);
        Assert.Equal(40.6413, leg.Latitude);
        Assert.Equal(-73.7781, leg.Longitude);
        Assert.Equal(450, leg.AltitudeMeters);
        Assert.True(leg.HasPosition);
    }

    [Fact]
    public void ReadsThePathTerminatorAndTheTurnDirection()
    {
        var leg = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.MissedApproachLeg,
            NavPayloadBuilder.Leg(path: LegPathType.ConstantRadiusArc, turnDirection: 1, trueDegree: 1)));

        Assert.Equal(LegPathType.ConstantRadiusArc, leg.PathType);
        Assert.True(leg.IsArc);
        Assert.Equal(ProcedureTurn.Left, leg.Turn);
        Assert.True(leg.TrueDegree);
    }

    [Fact]
    public void ReadsTheAltitudeRestrictions()
    {
        // "At or above 3000 feet" - 914.4 metres - is the restriction a step-down fix carries.
        var atOrAbove = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.FinalApproachLeg,
            NavPayloadBuilder.Leg(altitudeDesc: 2, altitude1: 914.4)));

        Assert.Equal(AltitudeConstraintKind.AtOrAbove, atOrAbove.AltitudeQualifier);
        Assert.Equal(3000, atOrAbove.MinimumAltitudeFt, 1);
        Assert.Equal(0, atOrAbove.MaximumAltitudeFt);

        // "At or below 5000 feet" - 1524 metres - is what a corridor fix carries.
        var atOrBelow = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.FinalApproachLeg,
            NavPayloadBuilder.Leg(altitudeDesc: 3, altitude1: 1524.0)));

        Assert.Equal(AltitudeConstraintKind.AtOrBelow, atOrBelow.AltitudeQualifier);
        Assert.Equal(5000, atOrBelow.MaximumAltitudeFt, 1);
        Assert.Equal(0, atOrBelow.MinimumAltitudeFt);

        // "Between 3000 and 5000" is the two-value restriction.
        var between = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.FinalApproachLeg,
            NavPayloadBuilder.Leg(altitudeDesc: 4, altitude1: 914.4, altitude2: 1524.0)));

        Assert.Equal(3000, between.MinimumAltitudeFt, 1);
        Assert.Equal(5000, between.MaximumAltitudeFt, 1);
    }

    [Fact]
    public void ReadsTheGroundworkOfADmeArc()
    {
        // A DME arc is described by its centre, the bearing to it (THETA) and the distance
        // from it (RHO). Both arrive between the origin and the course, so a reader that
        // skips them is eight bytes out for the rest of the leg.
        var leg = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.FinalApproachLeg,
            NavPayloadBuilder.Leg(
                path: LegPathType.ArcToFix,
                ident: "DEXET",
                course: 118.0,
                theta: 68.0,
                rho: 4200.0,
                routeDistance: 4200.0,
                turnDirection: 1)));

        Assert.Equal(118.0, leg.CourseDeg);
        Assert.Equal(68.0, leg.ThetaDeg);
        Assert.Equal(4200.0, leg.RhoMeters);
        Assert.Equal(4200.0, leg.RouteDistance);
        Assert.True(leg.IsArc);
    }

    [Fact]
    public void TrueCoursesAreConvertedWithTheAirportVariation()
    {
        // Geo works in [-180, 180), so a true course of 086 is what a magnetic 100 is at
        // 14 degrees west.
        var magnetic = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.FinalApproachLeg,
            NavPayloadBuilder.Leg(course: 100.0, trueDegree: 0)));

        var trueDeg = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.FinalApproachLeg,
            NavPayloadBuilder.Leg(course: 100.0, trueDegree: 1)));

        Assert.Equal(86.0, magnetic.TrueCourseDeg(-14), 6);
        // A published true course is already true and is left alone.
        Assert.Equal(100.0, trueDeg.TrueCourseDeg(-14), 6);
    }

    [Fact]
    public void ReadsTheSegmentMarkers()
    {
        var leg = ApproachRecordParser.ParseLeg(NavPayloadBuilder.LegRecord(1,
            FacilityDataType.FinalApproachLeg,
            NavPayloadBuilder.Leg(ident: "GAUGE", isIaf: 1, isFaf: 1)));

        Assert.True(leg.IsIaf);
        Assert.True(leg.IsFaf);
        Assert.False(leg.IsMap);
    }
}

/// <summary>
/// Follows a route of legs from the aircraft's own position: which leg is being flown,
/// which way to steer, and when the next one starts.
/// </summary>
public class NavPathFollowerTests
{
    /// <summary>
    /// A route of three fixes along 40 degrees north, 40 km apart from west to east, so
    /// each leg is long enough to fly without sequencing mid-test. Only the last leg is
    /// a fly-over.
    /// </summary>
    private static List<NavLeg> StraightRoute()
    {
        var (lat1, lon1) = Geo.OffsetByMeters(40.0, -73.0, 0, 0);
        var (lat2, lon2) = Geo.OffsetByMeters(40.0, -73.0, 40000, 0);
        var (lat3, lon3) = Geo.OffsetByMeters(40.0, -73.0, 80000, 0);

        return new List<NavLeg>
        {
            new() { Ident = "HOTEE", Latitude = lat1, Longitude = lon1 },
            new() { Ident = "GAUGE", Latitude = lat2, Longitude = lon2 },
            new() { Ident = "RW25", Latitude = lat3, Longitude = lon3, FlyOver = true }
        };
    }

    [Fact]
    public void SteersTheCourseOfTheLegItIsOn()
    {
        var follower = new NavPathFollower(StraightRoute());
        // Between the first and second fix, on course. The leg being flown is the one
        // whose terminator is the second fix: a leg's line runs from its predecessor to
        // its terminator, so this is the HOTEE-to-GAUGE leg.
        var (lat, lon) = Geo.OffsetByMeters(40.0, -73.0, 20000, 0);

        var guidance = follower.Update(lat, lon, 200);

        Assert.Equal(1, guidance.ActiveIndex);
        Assert.Equal("GAUGE", guidance.ActiveLeg?.Ident);
        // The route runs due east, to within the fraction of a degree a great circle
        // between two points of the same latitude bows off it.
        Assert.InRange(guidance.DesiredTrackTrueDeg, 89, 91);
        Assert.InRange(guidance.CrossTrackMeters, -50, 50);
    }

    [Fact]
    public void CrossTrackErrorCommandsACorrectionBackOntoTheLine()
    {
        var follower = new NavPathFollower(StraightRoute());

        // On that leg, 3000 metres to the left of it (north of an eastbound course).
        var (lat, lon) = Geo.OffsetByMeters(40.0, -73.0, 30000, 3000);
        var guidance = follower.Update(lat, lon, 200);

        Assert.Equal(1, guidance.ActiveIndex);
        Assert.True(guidance.CrossTrackMeters < -2900, $"cross track was {guidance.CrossTrackMeters}");
        // A course of 090 with the aircraft north of it is steered south of east, which is
        // a larger bearing: 120 rather than 60.
        Assert.True(guidance.DesiredTrackTrueDeg > 90, $"track was {guidance.DesiredTrackTrueDeg}");
    }

    [Fact]
    public void CrossTrackOnTheOtherSideCorrectsTheOtherWay()
    {
        var follower = new NavPathFollower(StraightRoute());

        // South of the same eastbound leg: the correction points north of east.
        var (lat, lon) = Geo.OffsetByMeters(40.0, -73.0, 30000, -3000);
        var guidance = follower.Update(lat, lon, 200);

        Assert.Equal(1, guidance.ActiveIndex);
        Assert.True(guidance.CrossTrackMeters > 2900, $"cross track was {guidance.CrossTrackMeters}");
        Assert.True(guidance.DesiredTrackTrueDeg < 90, $"track was {guidance.DesiredTrackTrueDeg}");
    }

    [Fact]
    public void SequencesToTheNextLegWhenTheFixIsReached()
    {
        var follower = new NavPathFollower(StraightRoute());

        // 5 km short of the second fix: still the leg being flown.
        var (onLegLat, onLegLon) = Geo.OffsetByMeters(40.0, -73.0, 35000, 0);
        var onLeg = follower.Update(onLegLat, onLegLon, 200);
        Assert.Equal(1, onLeg.ActiveIndex);
        Assert.Equal("GAUGE", onLeg.ActiveLeg?.Ident);

        // 100 metres short of the fix, inside the catch radius at 200 knots: the follower
        // has moved on to the last leg.
        var (nearLat, nearLon) = Geo.OffsetByMeters(40.0, -73.0, 39900, 0);
        var near = follower.Update(nearLat, nearLon, 200);

        Assert.Equal(2, near.ActiveIndex);
        Assert.Equal("RW25", near.ActiveLeg?.Ident);
    }

    [Fact]
    public void AFixLeftBehindIsSequencedPastOnTheNextFrame()
    {
        var follower = new NavPathFollower(StraightRoute());

        // Behind the first terminator but still well short of the second: the follower
        // has to work out which leg it is on rather than fly back to the fix it passed.
        var (lat, lon) = Geo.OffsetByMeters(40.0, -73.0, 45000, 0);
        var guidance = follower.Update(lat, lon, 200);

        Assert.Equal(2, guidance.ActiveIndex);
        Assert.Equal("RW25", guidance.ActiveLeg?.Ident);
    }

    [Fact]
    public void AFlyOverFixIsNotTurnedEarly()
    {
        var route = StraightRoute();
        // The fix the leg ends at has to be overflown.
        route[1].FlyOver = true;

        var follower = new NavPathFollower(route);

        // 500 m short of the fix, inside the turn anticipation at 200 knots.
        var (lat, lon) = Geo.OffsetByMeters(40.0, -73.0, 39500, 0);
        var guidance = follower.Update(lat, lon, 200);

        Assert.Equal(1, guidance.ActiveIndex);
    }

    [Fact]
    public void AFlyByFixIsTurnedEarly()
    {
        // The same route without the fly-over: the turn to the last leg starts before the
        // fix, which is what keeps the aircraft from cutting the corner of the turn.
        var route = StraightRoute();
        route[1].FlyOver = false;
        route[2].FlyOver = false;

        var follower = new NavPathFollower(route);
        var (lat, lon) = Geo.OffsetByMeters(40.0, -73.0, 39500, 0);

        var guidance = follower.Update(lat, lon, 200);

        Assert.Equal(2, guidance.ActiveIndex);
    }

    [Fact]
    public void TheLastLegCompletesRatherThanRunningPastTheThreshold()
    {
        var follower = new NavPathFollower(StraightRoute());

        // On the threshold, and a kilometre past it: neither is a leg to fly, because the
        // end of an approach is the threshold and running past it is not a turn.
        var (lat, lon) = Geo.OffsetByMeters(40.0, -73.0, 80000, 0);
        var at = follower.Update(lat, lon, 200);
        Assert.True(at.Complete);

        var (pastLat, pastLon) = Geo.OffsetByMeters(40.0, -73.0, 81000, 0);
        var past = follower.Update(pastLat, pastLon, 200);
        Assert.True(past.Complete);
    }

    [Fact]
    public void AnEmptyPathIsImmediatelyComplete()
    {
        var follower = new NavPathFollower(Array.Empty<NavLeg>());
        var guidance = follower.Update(40.0, -73.0, 200);

        Assert.True(guidance.Complete);
        Assert.Equal(-1, guidance.ActiveIndex);
    }

    [Fact]
    public void ActivatingMidRouteStartsAtTheNearestLeg()
    {
        var follower = new NavPathFollower(StraightRoute());

        // The aircraft is halfway along the 40-80 km leg (GAUGE-to-RW25), so the legs
        // before it are behind it: selecting on "the aircraft still projects past the
        // start" alone used to land on the GAUGE leg, whose terminator at 40 km was
        // already 20 km back.
        var (lat, lon) = Geo.OffsetByMeters(40.0, -73.0, 60000, 0);
        var active = follower.ActivateAtNearestLeg(lat, lon);

        Assert.Equal(2, active);
        Assert.Equal("RW25", follower.ActiveLeg?.Ident);
    }

    [Fact]
    public void CatchRadiusScalesWithGroundSpeed()
    {
        // A fix passed at 250 knots is not the same as one passed at 60: the slow aircraft
        // needs a radius small enough not to sequence it on a leg it has not reached, and
        // the fast one needs one large enough that it does not fly past its own fix.
        var slow = NavPathFollower.CatchRadiusMeters(30.8);   // 60 kt
        var fast = NavPathFollower.CatchRadiusMeters(128);    // 250 kt

        Assert.True(fast > slow * 1.5, $"catch radii {fast} and {slow}");
        Assert.InRange(slow, 120, 200);
        Assert.InRange(fast, 250, 400);

        // A radius neither shrinks to nothing nor grows without limit: it is clamped
        // between a floor that keeps a stationary aircraft from sequencing on a fix it has
        // not reached, and a ceiling that stops a fast one from turning a kilometre early.
        Assert.Equal(120, NavPathFollower.CatchRadiusMeters(0), 6);
        Assert.Equal(3000, NavPathFollower.CatchRadiusMeters(3000), 6);
    }

    [Fact]
    public void MagneticCoursesUseTheRoutesOwnVariation()
    {
        var follower = new NavPathFollower(StraightRoute(), magneticVariationDeg: -14);

        // The first leg runs from the fix the route starts at to its terminator, so it has
        // a true course of 090.
        Assert.InRange(follower.LegCourseTrueDeg(1), 89, 91);
        // A true course of about 090 is a magnetic course of about 104 at 14 degrees west.
        Assert.InRange(follower.LegCourseMagneticDeg(1), 103, 105);

        Assert.Equal(104, NavPathFollower.TrueToMagnetic(90, -14));
        Assert.Equal(14, NavPathFollower.TrueToMagnetic(360, -14));
    }
}

/// <summary>
/// A published missed approach is a chain of legs like any other procedure, and the route
/// it produces is what a go-around flies.
/// </summary>
public class MissedApproachTests
{
    private static ApproachInfo ApproachWithMissedApproach()
    {
        var approach = new ApproachInfo
        {
            Kind = ApproachKind.Ils,
            Runway = "25L",
            MissedAltitudeFt = 6000
        };

        approach.FinalLegs.Add(new ApproachLeg
        {
            Ident = "GAUGE", Latitude = 40.6, Longitude = -73.7, IsFaf = true
        });
        approach.FinalLegs.Add(new ApproachLeg
        {
            Ident = "RW25", Latitude = 40.61, Longitude = -73.71, IsMap = true
        });

        // Climb ahead, then hold at a fix: the shape most missed approaches are published as.
        approach.MissedLegs.Add(new ApproachLeg
        {
            Ident = "DANDY", Latitude = 40.62, Longitude = -73.7, AltitudeMeters = 1828.8
        });
        approach.MissedLegs.Add(new ApproachLeg
        {
            Ident = "HOLD", Latitude = 40.65, Longitude = -73.7, AltitudeMeters = 1828.8
        });

        return approach;
    }

    [Fact]
    public void TheMissedApproachIsARouteOfItsOwn()
    {
        var approach = ApproachWithMissedApproach();

        Assert.True(approach.HasMissedApproach);
        Assert.True(approach.HasFinalApproach);
        Assert.Equal(2, approach.MissedLegs.Count);
        Assert.Equal("RW25", approach.FindMissedApproachPoint()?.Ident);
    }

    [Fact]
    public void TheMissedApproachIsFlownFromTheMissedApproachPoint()
    {
        var approach = ApproachWithMissedApproach();

        // The aircraft is at the missed approach point: the first missed leg is next. The
        // published legs are turned into route legs the same way the route builder does it.
        var legs = approach.MissedLegs.Select(l => new NavLeg
        {
            Ident = l.Ident,
            Latitude = l.Latitude,
            Longitude = l.Longitude
        }).ToList();

        var follower = new NavPathFollower(legs, -14);
        var guidance = follower.Update(40.61, -73.71, 140);

        Assert.False(guidance.Complete);
        Assert.Equal("DANDY", guidance.ActiveLeg?.Ident);
        // The first missed leg is north-east of the missed approach point, and the track is
        // what points at it.
        Assert.InRange(guidance.DesiredTrackTrueDeg, 30, 50);
    }

    [Fact]
    public void AnApproachWithNoPublishedMissedLegsHasNoneToFly()
    {
        var approach = new ApproachInfo { Kind = ApproachKind.Gps, Runway = "25L" };
        approach.FinalLegs.Add(new ApproachLeg { Ident = "RW25", Latitude = 40.6, Longitude = -73.7 });

        Assert.False(approach.HasMissedApproach);
    }

    [Fact]
    public void TheTransitionMatchedToTheArrivingFixIsTheOneFlown()
    {
        var approach = new ApproachInfo
        {
            Kind = ApproachKind.Rnav,
            Runway = "25L",
            MissedAltitudeFt = 6000
        };

        // Two ways in to the same approach, published in the order the airport lists them.
        approach.Transitions.Add(new ApproachTransition { Name = "DARTS", IafIcao = "DARTS" });
        approach.Transitions.Add(new ApproachTransition { Name = "CROUP", IafIcao = "CROUP" });

        // The route arrives from CROUP, so that is the transition that continues it; the
        // first published one would send the aircraft back the way it came.
        var chosen = approach.TransitionFor("CROUP");

        Assert.NotNull(chosen);
        Assert.Equal("CROUP", chosen!.IafIcao);
    }

    [Fact]
    public void AnApproachWithNoMatchingTransitionStillFliesTheStraightIn()
    {
        var approach = new ApproachInfo
        {
            Kind = ApproachKind.Rnav,
            Runway = "25L"
        };
        approach.Transitions.Add(new ApproachTransition { Name = "DARTS", IafIcao = "DARTS" });

        // A route arriving from a fix no transition starts at: the approach's own legs are
        // flown, which is what a "straight-in, no transition" clearance means.
        var chosen = approach.TransitionFor("ELIOT");

        Assert.NotNull(chosen);
        Assert.Equal("DARTS", chosen!.IafIcao);
    }

    [Fact]
    public void AnApproachWithNoTransitionsHasNoWayIn()
    {
        var approach = new ApproachInfo { Kind = ApproachKind.Ils, Runway = "25L" };
        approach.FinalLegs.Add(new ApproachLeg { Ident = "RW25", Latitude = 40.6, Longitude = -73.7 });

        Assert.Null(approach.TransitionFor("ELIOT"));
    }
}

/// <summary>
/// The route assembled out of a plan and the navigation database. A MSFS 2024 plan is a
/// list of names: the geometry either comes from the database or it does not come at all.
/// </summary>
public class NavPathBuilderTests
{
    private static NavigationState PlanWithNamedFixes(params string[] idents)
    {
        var nav = new NavigationState
        {
            DepartureICAO = "KSEA",
            ArrivalICAO = "KJFK",
            DepartureLatitude = 47.4502,
            DepartureLongitude = -122.3088,
            DestinationLatitude = 40.6413,
            DestinationLongitude = -73.7781
        };

        foreach (var ident in idents)
        {
            nav.Waypoints.Add(new FlightPlanWaypoint
            {
                Ident = ident,
                Type = WaypointType.Intersection,
                // A MSFS 2024 native plan carries no waypoint positions at all.
                Latitude = double.NaN,
                Longitude = double.NaN
            });
        }

        return nav;
    }

    private static readonly NavFix SeaFix = new()
    {
        Icao = "SEA",
        Latitude = 47.4502,
        Longitude = -122.3088,
        AltitudeMeters = 132
    };

    private static NavRoutePlan Build(
        NavigationState nav,
        IReadOnlyList<ApproachInfo> approaches,
        string? arrivalRunway = null) =>
        NavPathBuilder.Build(
            nav,
            ident => ident == "SEA" ? SeaFix : null,
            approaches,
            null,
            arrivalRunway,
            null,
            ident => ident != "SEA");

    [Fact]
    public void AFixThatHasNotBeenLookedUpYetIsNotReportedAsUnresolvable()
    {
        // A route is rebuilt while the lookups are still outstanding, and a name that has
        // not yet been looked up is a name still being answered for - not one the database
        // does not carry. Reporting it as unresolved during that window would tell the
        // console the route is broken when it is merely incomplete.
        var plan = NavPathBuilder.Build(
            PlanWithNamedFixes("SEA", "NOTAFIX"),
            ident => ident == "SEA" ? SeaFix : null,
            Array.Empty<ApproachInfo>(),
            null,
            null,
            null,
            ident => false);

        Assert.Empty(plan.UnresolvedFixes);
    }

    [Fact]
    public void ANamedFixTheDatabaseKnowsIsPlacedOnTheRoute()
    {
        var plan = Build(PlanWithNamedFixes("SEA"), Array.Empty<ApproachInfo>());

        Assert.Contains(plan.Legs, l => l.Ident == "SEA" && l.HasPosition);
        Assert.Empty(plan.UnresolvedFixes);
        Assert.True(plan.HasRoute);
        // The plan's first fix is the departure airport itself, so the route starts there
        // rather than being given a second leg for the same place.
        Assert.Equal("SEA", plan.Legs[0].Ident);
    }

    [Fact]
    public void ANamedFixTheDatabaseDoesNotKnowIsReportedNotImagined()
    {
        var plan = Build(PlanWithNamedFixes("NOTAFIX"), Array.Empty<ApproachInfo>());

        Assert.Contains("NOTAFIX", plan.UnresolvedFixes);
        Assert.DoesNotContain(plan.Legs, l => l.Ident == "NOTAFIX");
    }

    [Fact]
    public void ARouteWhoseFixesCannotBePlacedReportsThemAndKeepsItsAnchor()
    {
        var plan = Build(PlanWithNamedFixes("NOTAFIX"), Array.Empty<ApproachInfo>());

        // The only geometry is the departure airport: the route is not invented from a
        // fix the database does not know.
        Assert.Equal(1, plan.Legs.Count(l => l.HasPosition));
        Assert.Equal(NavLegKind.RunwayStart, plan.Legs[0].Kind);
        Assert.Contains("NOTAFIX", plan.UnresolvedFixes);
    }

    [Fact]
    public void ThePublishedApproachIsAppendedToTheRoute()
    {
        var approach = new ApproachInfo
        {
            Kind = ApproachKind.Ils,
            Runway = "25L",
            MissedAltitudeFt = 6000
        };
        approach.FinalLegs.Add(new ApproachLeg { Ident = "GAUGE", Latitude = 40.6, Longitude = -73.7 });
        approach.FinalLegs.Add(new ApproachLeg { Ident = "RW25", Latitude = 40.61, Longitude = -73.71 });

        var plan = Build(PlanWithNamedFixes("SEA"), new[] { approach }, arrivalRunway: "25L");

        Assert.Equal("25L", plan.Approach?.Runway);
        Assert.Equal(NavLegKind.Approach, plan.Legs[^1].Kind);
        Assert.True(plan.HasApproach);
    }

    [Fact]
    public void ThePublishedMissedApproachIsKeptSeparateFromTheRoute()
    {
        var approach = new ApproachInfo
        {
            Kind = ApproachKind.Ils,
            Runway = "25L",
            MissedAltitudeFt = 6000
        };
        approach.FinalLegs.Add(new ApproachLeg { Ident = "GAUGE", Latitude = 40.6, Longitude = -73.7 });
        approach.MissedLegs.Add(new ApproachLeg { Ident = "DANDY", Latitude = 40.62, Longitude = -73.7 });
        approach.MissedLegs.Add(new ApproachLeg { Ident = "HOLD", Latitude = 40.65, Longitude = -73.7 });

        var plan = Build(PlanWithNamedFixes("SEA"), new[] { approach }, arrivalRunway: "25L");

        // The go-around is not flown as part of the arrival.
        Assert.DoesNotContain(plan.Legs, l => l.Ident == "DANDY");
        Assert.True(plan.HasMissedApproach);
        Assert.Equal("DANDY", plan.MissedLegs[0].Ident);
        Assert.Equal(NavLegKind.Missed, plan.MissedLegs[0].Kind);
    }

    [Fact]
    public void TheApproachForTheAssignedRunwayIsChosen()
    {
        var wrong = new ApproachInfo { Kind = ApproachKind.Rnav, Runway = "31R" };
        wrong.FinalLegs.Add(new ApproachLeg { Ident = "OTHER", Latitude = 40.0, Longitude = -73.0 });

        var right = new ApproachInfo { Kind = ApproachKind.Ils, Runway = "25L" };
        right.FinalLegs.Add(new ApproachLeg { Ident = "RW25", Latitude = 40.6, Longitude = -73.7 });

        var chosen = NavPathBuilder.SelectApproach(new[] { wrong, right }, "25L");

        Assert.NotNull(chosen);
        Assert.Equal("25L", chosen!.Runway);
    }

    [Fact]
    public void AnIlsBeatsAnRnavToTheSameRunway()
    {
        var rnav = new ApproachInfo { Kind = ApproachKind.Rnav, Runway = "25L", HasLnav = true };
        rnav.FinalLegs.Add(new ApproachLeg { Ident = "RW25", Latitude = 40.6, Longitude = -73.7 });

        var ils = new ApproachInfo { Kind = ApproachKind.Ils, Runway = "25L" };
        ils.FinalLegs.Add(new ApproachLeg { Ident = "RW25", Latitude = 40.6, Longitude = -73.7 });

        var chosen = NavPathBuilder.SelectApproach(new[] { rnav, ils }, "25L");

        Assert.Equal(ApproachKind.Ils, chosen?.Kind);
    }

    [Fact]
    public void AnApproachWithNoFinalLegsIsNotAnApproach()
    {
        var noLegs = new ApproachInfo { Kind = ApproachKind.Ils, Runway = "25L" };

        var chosen = NavPathBuilder.SelectApproach(new[] { noLegs }, "25L");

        Assert.Null(chosen);
    }

    [Fact]
    public void ATurnIsChainedToTheLegItIsFlownFrom()
    {
        // Two named fixes the database knows, one the pilot resolves and one it does not.
        var plan = Build(PlanWithNamedFixes("SEA", "KJFK"), new[] { ApproachWithKJfkFixes() });

        var placed = plan.Legs.Where(l => l.HasPosition).ToList();
        Assert.True(placed.Count >= 2, $"legs were [{string.Join(", ", plan.Legs.Select(l => l.ToString()))}]");

        // The chain is a linked list as well as a list: the follower steers from the fix a
        // leg leaves, and finding it by index alone would break if a leg were skipped.
        for (var i = 1; i < placed.Count; i++)
        {
            Assert.NotNull(placed[i].Previous);
        }
    }

    /// <summary>A published approach whose legs carry positions, as the navdata would.</summary>
    private static ApproachInfo ApproachWithKJfkFixes()
    {
        var approach = new ApproachInfo
        {
            Kind = ApproachKind.Rnav,
            Runway = "31R",
            MissedAltitudeFt = 6000
        };
        approach.FinalLegs.Add(new ApproachLeg { Ident = "KJFK", Latitude = 40.6413, Longitude = -73.7781 });
        approach.MissedLegs.Add(new ApproachLeg { Ident = "DANDY", Latitude = 40.7, Longitude = -73.8 });
        return approach;
    }
}

/// <summary>Arc geometry: a constant radius arc is not the straight line between its ends.</summary>
public class ArcGeometryTests
{
    /// <summary>
    /// A quarter circle of 5000 m about a point, starting due east of it and ending due
    /// north of it. The short way between those two points turns to the right, which is
    /// what a right turn is at a bearing of 090 flying to 000.
    /// </summary>
    private static ApproachLeg QuarterCircle(double endLat, double endLon, ProcedureTurn turn) =>
        new()
        {
            PathType = LegPathType.ConstantRadiusArc,
            Latitude = endLat,
            Longitude = endLon,
            ArcCenterLatitude = 40.0,
            ArcCenterLongitude = -73.0,
            Turn = turn
        };

    [Fact]
    public void AnArcStaysAtItsRadiusFromTheCentre()
    {
        var start = Geo.OffsetByMeters(40.0, -73.0, 5000, 0);
        var end = Geo.OffsetByMeters(40.0, -73.0, 0, 5000);

        // The radius of an RF arc is the distance from its centre to where the arc starts,
        // which is what the geometry works out from the fix the aircraft is leaving.
        var radius = Geo.DistanceMeters(40.0, -73.0, start.lat, start.lon);

        var points = ArcGeometry.Points(
            QuarterCircle(end.lat, end.lon, ProcedureTurn.Right), start.lat, start.lon);

        Assert.True(points.Count > 5, "a quarter circle is not five points long");
        foreach (var (lat, lon) in points)
        {
            Assert.Equal(radius, Geo.DistanceMeters(40.0, -73.0, lat, lon), 1);
        }

        // The last point is the terminator, on the same circle as the rest of the arc.
        Assert.Equal(radius, Geo.DistanceMeters(40.0, -73.0, points[^1].lat, points[^1].lon), 1);
        Assert.InRange(radius, 4900, 5100);
    }

    [Fact]
    public void AnArcTurnsThePublishedWayRound()
    {
        var start = Geo.OffsetByMeters(40.0, -73.0, 5000, 0);
        var end = Geo.OffsetByMeters(40.0, -73.0, 0, 5000);

        var right = ArcGeometry.Points(
            QuarterCircle(end.lat, end.lon, ProcedureTurn.Right), start.lat, start.lon);
        var left = ArcGeometry.Points(
            QuarterCircle(end.lat, end.lon, ProcedureTurn.Left), start.lat, start.lon);

        // The short way between the east point and the north point is a right turn, and a
        // left turn is the long way round: the same two fixes, a different procedure.
        Assert.True(left.Count > right.Count * 2,
            $"left had {left.Count} points and right {right.Count}");

        // Every point of both is on the circle about the centre.
        var radius = Geo.DistanceMeters(40.0, -73.0, start.lat, start.lon);
        foreach (var (lat, lon) in left)
        {
            Assert.Equal(radius, Geo.DistanceMeters(40.0, -73.0, lat, lon), 1);
        }
    }

    [Fact]
    public void AnArcWithNoStartPointCannotBeBuilt()
    {
        var end = Geo.OffsetByMeters(40.0, -73.0, 0, 5000);

        Assert.Empty(ArcGeometry.Points(QuarterCircle(end.lat, end.lon, ProcedureTurn.Right), 0, 0));
    }

    [Fact]
    public void AnArcWithNoCentreCannotBeBuilt()
    {
        var start = Geo.OffsetByMeters(40.0, -73.0, 5000, 0);
        var end = Geo.OffsetByMeters(40.0, -73.0, 0, 5000);

        var leg = new ApproachLeg
        {
            PathType = LegPathType.ConstantRadiusArc,
            Latitude = end.lat,
            Longitude = end.lon,
            Turn = ProcedureTurn.Right
        };

        Assert.Empty(ArcGeometry.Points(leg, start.lat, start.lon));
    }

    [Fact]
    public void AnArcAndItsReverseCoverTheSameGround()
    {
        var start = Geo.OffsetByMeters(40.0, -73.0, 5000, 0);
        var end = Geo.OffsetByMeters(40.0, -73.0, 0, 5000);

        var forwards = ArcGeometry.Points(
            QuarterCircle(end.lat, end.lon, ProcedureTurn.Right), start.lat, start.lon);
        // Reversing a right turn is a left turn: the direction is a property of which way
        // round the arc is flown, not of which end it is entered from.
        var backwards = ArcGeometry.Points(
            QuarterCircle(start.lat, start.lon, ProcedureTurn.Left), end.lat, end.lon);

        Assert.Equal(forwards.Count, backwards.Count);
        Assert.True(forwards.Count > 5);
        Assert.True(backwards.Count > 5);
    }
}

/// <summary>
/// The fix records the route builder resolves by ICAO. A waypoint publishes its own
/// ident; an NDB and a VOR publish a position and no ident at all, because the ICAO being
/// asked for is the ICAO the answer is for.
/// </summary>
public class FixRecordParserTests
{
    [Fact]
    public void AWaypointCarriesItsOwnIdentAndRegion()
    {
        var fix = ApproachRecordParser.ParseWaypoint(new FacilityDataRecord
        {
            Type = FacilityDataType.Waypoint,
            Payload = NavPayloadBuilder.FixRecord("GLASG", 40.6413, -73.7781, 1500)
        });

        Assert.Equal("GLASG", fix.Icao);
        Assert.Equal('W', fix.Type);
        Assert.Equal(40.6413, fix.Latitude);
        Assert.Equal(-73.7781, fix.Longitude);
        Assert.Equal(1500, fix.AltitudeMeters);
        Assert.True(fix.HasPosition);
    }

    [Fact]
    public void AnNdbCarriesAPositionAndNoIdent()
    {
        // Declaring an ICAO field the entry point does not publish would be rejected by the
        // simulator, so the reader stops after the position.
        var fix = ApproachRecordParser.ParseNdb(new FacilityDataRecord
        {
            Type = FacilityDataType.Ndb,
            Payload = NavPayloadBuilder.FixRecord("HOMER", 47.6212, -122.3042, 0)
        });

        Assert.Equal('N', fix.Type);
        Assert.Equal(47.6212, fix.Latitude);
        Assert.Equal("", fix.Icao);
        Assert.True(fix.HasPosition);
    }

    [Fact]
    public void AVorCarriesAPositionAndNoIdent()
    {
        var fix = ApproachRecordParser.ParseVor(new FacilityDataRecord
        {
            Type = FacilityDataType.Vor,
            Payload = NavPayloadBuilder.FixRecord("SEA", 47.4355, -122.3089, 0)
        });

        Assert.Equal('V', fix.Type);
        Assert.Equal(47.4355, fix.Latitude);
        Assert.Equal("", fix.Icao);
    }

    [Fact]
    public void AFixWithoutAPositionIsNotAPosition()
    {
        var fix = ApproachRecordParser.ParseWaypoint(new FacilityDataRecord
        {
            Type = FacilityDataType.Waypoint,
            Payload = NavPayloadBuilder.FixRecord("NOWHERE", 0, 0, 0)
        });

        Assert.False(fix.HasPosition);
    }
}

/// <summary>
/// The direct-to clearance the controller issues in the air: "fly direct to GLASG" is a fix
/// name and nothing else, because the position comes from the navigation database.
/// </summary>
public class DirectToInstructionTests
{
    [Theory]
    [InlineData("FLY DIRECT TO GLASG", "GLASG")]
    [InlineData("PROCEED DIRECT DOGAL", "DOGAL")]
    [InlineData("WHEN ABLE DIRECT TO HEMNS", "HEMNS")]
    public void ReadsTheFixName(string text, string expected)
    {
        var instruction = BeyondAtcBridge.ParseAtcText(text, "KJFK");

        Assert.Equal(expected, instruction.DirectToFix);
    }

    [Theory]
    [InlineData("PROCEED TO RUNWAY 25")]
    [InlineData("TURN LEFT HEADING 270")]
    [InlineData("CLIMB AND MAINTAIN 10000")]
    public void NoFixIsNamedWhenThereIsNone(string text)
    {
        Assert.Null(BeyondAtcBridge.ParseAtcText(text, "KJFK").DirectToFix);
    }

    [Fact]
    public void ADirectToIsNotAHeading()
    {
        var instruction = BeyondAtcBridge.ParseAtcText("PROCEED DIRECT TO GLASG", "KJFK");

        // "direct" is not "heading 090": the clearance carries a fix, and the heading it
        // implies is computed from the aircraft's own position each frame.
        Assert.Null(instruction.HeadingDeg);
        Assert.Equal("GLASG", instruction.DirectToFix);
    }
}

/// <summary>
/// The structure of the facility definitions, which the simulator enforces one field
/// at a time: a definition must open with one of the four documented entry points
/// (AIRPORT, WAYPOINT, NDB, VOR), a child must be opened before its parent closes,
/// and one definition carries one entry point. Each broken rule is answered
/// field-by-field with
/// <c>DATA_ERROR ... [add facility field 'X' to definition N]</c> - and after the
/// first rejection every later field of the definition fails with it - so an
/// airport's approaches silently never arrive rather than erroring visibly.
/// </summary>
public class NavFacilityDefinitionTests
{
    private static readonly string[] EntryPoints = { "AIRPORT", "WAYPOINT", "NDB", "VOR" };

    /// <summary>
    /// Children the documentation publishes as members of the APPROACH entry point. Each
    /// must appear while the approach is still open: they are procedures that belong to
    /// an approach, not siblings of it.
    /// </summary>
    public static TheoryData<string> ApproachChildren => new()
    {
        "APPROACH_TRANSITION",
        "FINAL_APPROACH_LEG",
        "MISSED_APPROACH_LEG",
        "APPROACH_LEG"
    };

    [Fact]
    public void EveryDefinitionOpensExactlyOneEntryPointAndBalancesItsContainers()
    {
        foreach (var definition in NavDataLoader.FacilityDefinitions())
        {
            var open = new Stack<string>();
            for (var i = 0; i < definition.Fields.Count; i++)
            {
                var field = definition.Fields[i];
                if (field.StartsWith("OPEN ", StringComparison.Ordinal))
                {
                    var container = field["OPEN ".Length..];
                    if (open.Count == 0)
                    {
                        // Only the first field of a definition may be an entry point.
                        Assert.True(i == 0, $"definition {definition.DefinitionId} opens a " +
                            $"second entry point, '{container}'");
                        Assert.Contains(container, EntryPoints);
                    }
                    open.Push(container);
                }
                else if (field.StartsWith("CLOSE ", StringComparison.Ordinal))
                {
                    var container = field["CLOSE ".Length..];
                    // A close may only close the innermost container, otherwise it is a
                    // child opened after its parent was closed.
                    Assert.True(open.TryPeek(out var top) && top == container,
                        $"definition {definition.DefinitionId} closes '{container}' which is " +
                        $"not open");
                    open.Pop();
                }
                else
                {
                    // A member field sits inside a container; at the top level of a
                    // definition nothing is in scope but an entry point.
                    Assert.True(open.Count > 0,
                        $"definition {definition.DefinitionId} declares '{field}' outside any container");
                }
            }
            Assert.True(open.Count == 0, $"definition {definition.DefinitionId} leaves " +
                $"'{open.FirstOrDefault()}' open");
        }
    }

    [Theory]
    [MemberData(nameof(ApproachChildren))]
    public void ApproachChildrenAreOpenedInsideTheApproach(string child)
    {
        var found = 0;
        foreach (var definition in NavDataLoader.FacilityDefinitions())
        {
            var open = new Stack<string>();
            foreach (var field in definition.Fields)
            {
                if (field.StartsWith("OPEN ", StringComparison.Ordinal))
                {
                    if (field["OPEN ".Length..] == child)
                    {
                        Assert.Contains("APPROACH", open.Where(c => c == "APPROACH"));
                        found++;
                    }
                    open.Push(field["OPEN ".Length..]);
                }
                else if (field.StartsWith("CLOSE ", StringComparison.Ordinal))
                {
                    open.Pop();
                }
            }
        }
        Assert.True(found > 0, $"no definition opens {child}; the approaches arrive without it");
    }

    [Fact]
    public void TheFixDefinitionsCarryOneEntryPointEach()
    {
        // A fix may be a waypoint, an NDB or a VOR, and the database publishes no index
        // of which, so all three are asked - but each in its own definition, because a
        // second entry point in one definition is refused.
        var fixes = NavDataLoader.FacilityDefinitions()
            .Where(d => new[] { "WAYPOINT", "NDB", "VOR" }.Contains(
                d.Fields[0]["OPEN ".Length..]))
            .ToList();

        Assert.Equal(3, fixes.Count);
        foreach (var definition in fixes)
        {
            Assert.Single(definition.Fields.Where(f => f.StartsWith("OPEN ", StringComparison.Ordinal)));
        }
    }
}


