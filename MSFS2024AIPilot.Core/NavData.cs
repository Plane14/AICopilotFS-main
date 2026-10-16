using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>Which way a procedure turns at a fix (leg TURN_DIRECTION).</summary>
public enum ProcedureTurn
{
    None = 0,
    Left = 1,
    Right = 2,
    Either = 3
}

/// <summary>
/// What a leg's altitude restriction means, from the ARINC 424 altitude description
/// (leg APPROACH_ALT_DESC).
/// </summary>
public enum AltitudeConstraintKind
{
    NotUsed = 0,
    At = 1,
    AtOrAbove = 2,
    AtOrBelow = 3,
    Between = 4
}

/// <summary>The approach type published on the APPROACH record (and the .pln spelling).</summary>
public enum ApproachKind
{
    Undefined = 0,
    Gps = 1,
    Vor = 2,
    Ndb = 3,
    Ils = 4,
    Localizer = 5,
    Sdf = 6,
    Lda = 7,
    VorDme = 8,
    NdbDme = 9,
    Rnav = 10,
    LocalizerBackCourse = 11
}

/// <summary>
/// The ARINC 424 path terminator of a procedure leg. It says what the leg is: a course
/// to a fix, a heading to an altitude, a constant radius arc, a course reversal. Two of
/// them (the arcs and the course reversals) cannot be flown as a straight line, which is
/// why the terminator is carried rather than thrown away.
/// </summary>
public enum LegPathType
{
    Unknown = 0,
    ArcToFix = 1,
    CourseToAltitude = 2,
    CourseToDme = 3,
    CourseToFix = 4,
    CourseToIntercept = 5,
    CourseToRadial = 6,
    DirectToFix = 7,
    FixToAltitude = 8,
    TrackFromFix = 9,
    TrackFromFixToDme = 10,
    TrackFromFixToManual = 11,
    HoldToAltitude = 12,
    HoldToFix = 13,
    HoldToManual = 14,
    InitialFix = 15,
    ProcedureTurn = 16,
    ConstantRadiusArc = 17,
    TrackToFix = 18,
    HeadingToAltitude = 19,
    HeadingToDme = 20,
    HeadingToIntercept = 21,
    HeadingToManual = 22,
    HeadingToRadial = 23
}

/// <summary>
/// One published procedure leg, as SimConnect delivers it on the APPROACH_LEG,
/// FINAL_APPROACH_LEG and MISSED_APPROACH_LEG records.
/// </summary>
public sealed class ApproachLeg
{
    public string Ident { get; set; } = "";
    public string Region { get; set; } = "";
    /// <summary>Fix type as published: 'A' airport, 'V' VOR, 'N' NDB, 'W' waypoint.</summary>
    public char FixType { get; set; } = 'W';
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeMeters { get; set; }

    public LegPathType PathType { get; set; } = LegPathType.Unknown;
    public bool FlyOver { get; set; }
    public bool TrueDegree { get; set; }
    public ProcedureTurn Turn { get; set; } = ProcedureTurn.None;

    /// <summary>Published course, magnetic unless <see cref="TrueDegree"/> is set.</summary>
    public double CourseDeg { get; set; }
    /// <summary>Magnetic bearing from the reference navaid to the fix, in degrees.</summary>
    public double ThetaDeg { get; set; }
    /// <summary>Distance from the reference navaid to the fix, in metres.</summary>
    public double RhoMeters { get; set; }
    /// <summary>Published leg length, in metres or minutes (see <see cref="DistanceIsMinutes"/>).</summary>
    public double RouteDistance { get; set; }
    public bool DistanceIsMinutes { get; set; }
    public double VerticalAngleDeg { get; set; }
    public double SpeedLimitKt { get; set; }

    public AltitudeConstraintKind AltitudeQualifier { get; set; } = AltitudeConstraintKind.NotUsed;
    /// <summary>First altitude restriction of the leg, in metres MSL.</summary>
    public double Altitude1Meters { get; set; }
    /// <summary>Second altitude restriction, only meaningful when the qualifier is <see cref="AltitudeConstraintKind.Between"/>.</summary>
    public double Altitude2Meters { get; set; }

    /// <summary>Fix the leg is defined from: the reference navaid of a DME arc or a course reversal.</summary>
    public string OriginIcao { get; set; } = "";
    public string OriginRegion { get; set; } = "";
    public char OriginType { get; set; } = 'W';
    public double OriginLatitude { get; set; }
    public double OriginLongitude { get; set; }
    public double OriginAltitudeMeters { get; set; }


    /// <summary>Centre of a constant radius arc (RF legs), when the sim publishes one.</summary>
    public string ArcCenterIcao { get; set; } = "";
    public string ArcCenterRegion { get; set; } = "";
    public char ArcCenterType { get; set; } = 'W';
    public double ArcCenterLatitude { get; set; }
    public double ArcCenterLongitude { get; set; }
    public double ArcCenterAltitudeMeters { get; set; }

    public bool IsIaf { get; set; }
    public bool IsIf { get; set; }
    public bool IsFaf { get; set; }
    public bool IsMap { get; set; }

    public bool HasPosition => !double.IsNaN(Latitude) && !double.IsNaN(Longitude) && (Latitude != 0 || Longitude != 0);

    /// <summary>
    /// True when the leg flies an arc rather than a straight line: a constant radius arc,
    /// or an arc terminated by a fix off the reference navaid.
    /// </summary>
    public bool IsArc => PathType is LegPathType.ConstantRadiusArc or LegPathType.ArcToFix;

    /// <summary>Minimum altitude the leg may be flown at, in feet, or 0 when unrestricted.</summary>
    /// <remarks>
    /// NaN is not a number and fails every comparison, so a "candidate &lt; this" test
    /// elsewhere would read it as a real floor of 0; a NaN constraint therefore resolves to
    /// 0, which every consumer already treats as "unrestricted".
    /// </remarks>
    public double MinimumAltitudeFt
    {
        get
        {
            if (!double.IsFinite(Altitude1Meters)) return 0;
            return AltitudeQualifier switch
            {
                AltitudeConstraintKind.At or AltitudeConstraintKind.AtOrAbove => Geo.MetersToFeet(Altitude1Meters),
                AltitudeConstraintKind.Between => double.IsFinite(Altitude2Meters)
                    ? Geo.MetersToFeet(Math.Min(Altitude1Meters, Altitude2Meters))
                    : 0,
                _ => 0
            };
        }
    }

    /// <summary>Maximum altitude the leg may be flown at, in feet, or 0 when unrestricted.</summary>
    public double MaximumAltitudeFt
    {
        get
        {
            if (!double.IsFinite(Altitude1Meters)) return 0;
            return AltitudeQualifier switch
            {
                AltitudeConstraintKind.At or AltitudeConstraintKind.AtOrBelow => Geo.MetersToFeet(Altitude1Meters),
                AltitudeConstraintKind.Between => double.IsFinite(Altitude2Meters)
                    ? Geo.MetersToFeet(Math.Max(Altitude1Meters, Altitude2Meters))
                    : 0,
                _ => 0
            };
        }
    }

    /// <summary>
    /// True course of the leg. The published course is magnetic unless the record says
    /// otherwise, so the airport's variation is applied only where it is needed.
    /// </summary>
    public double TrueCourseDeg(double magneticVariationDeg) =>
        TrueDegree ? CourseDeg : Geo.NormalizeDegrees(CourseDeg + magneticVariationDeg);

    public override string ToString() =>
        $"{PathType} {Ident} {(HasPosition ? $"{Latitude:F5},{Longitude:F5}" : "(no fix position)")}";
}

/// <summary>A named way in to an approach: the IAF, its altitude and the legs that follow.</summary>
public sealed class ApproachTransition
    {
        public ApproachKind Type { get; set; } = ApproachKind.Undefined;
        public string IafIcao { get; set; } = "";
        public string IafRegion { get; set; } = "";
        public char IafType { get; set; } = 'W';
        public double IafAltitudeFt { get; set; }
        public string DmeArcIcao { get; set; } = "";
        public string DmeArcRegion { get; set; } = "";
        public char DmeArcType { get; set; } = 'W';
        public int DmeArcRadial { get; set; }
        public double DmeArcDistanceMeters { get; set; }
        public string Name { get; set; } = "";
        public int LegCount { get; set; }
        public List<ApproachLeg> Legs { get; } = new();
    }

/// <summary>
/// One published approach to a runway: its identification, its final approach legs and
/// its published missed approach. The missed approach is a list of legs like any other
/// procedure, which is what a go-around flies instead of the runway heading.
/// </summary>
public sealed class ApproachInfo
{
    public ApproachKind Kind { get; set; } = ApproachKind.Undefined;
    /// <summary>Multiple-approach suffix, e.g. the "Z" of an RNAV-Z.</summary>
    public string Suffix { get; set; } = "";
    /// <summary>Runway the approach serves, normalised to the "25L" form.</summary>
    public string Runway { get; set; } = "";

    public string FafIcao { get; set; } = "";
    public string FafRegion { get; set; } = "";
    public double FafHeadingDeg { get; set; }
    public double FafAltitudeFt { get; set; }
    public char FafType { get; set; } = 'W';

    /// <summary>Altitude of the first missed-approach leg, in feet.</summary>
    public double MissedAltitudeFt { get; set; }

    public bool HasLnav { get; set; }
    public bool HasLnavVnav { get; set; }
    public bool HasLp { get; set; }
    public bool HasLpv { get; set; }

    public List<ApproachLeg> FinalLegs { get; } = new();
    public List<ApproachLeg> MissedLegs { get; } = new();
    public List<ApproachTransition> Transitions { get; } = new();

    /// <summary>
    /// The identity an approach is merged and looked up by: the runway, the type and the
    /// suffix, because that is the only triple that distinguishes two approaches to the
    /// same strip (RNAV-Y and RNAV-Z).
    /// </summary>
    public string Key => $"{Runway}|{(int)Kind}|{Suffix}";

    public bool HasMissedApproach => MissedLegs.Any(l => l.HasPosition);

    public bool HasFinalApproach => FinalLegs.Any(l => l.HasPosition);

    /// <summary>
    /// The final approach fix, either the one the approach record names or the last fix
    /// flagged as such on the final legs.
    /// </summary>
    public ApproachLeg? FindFinalApproachFix()
    {
        var flagged = FinalLegs.FirstOrDefault(l => l.IsFaf);
        if (flagged != null) return flagged;

        if (!string.IsNullOrEmpty(FafIcao))
        {
            var named = FinalLegs.FirstOrDefault(l =>
                string.Equals(l.Ident, FafIcao, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
        }

        return FinalLegs.FirstOrDefault();
    }

    /// <summary>The missed approach point, which is where a missed approach begins.</summary>
    public ApproachLeg? FindMissedApproachPoint() => FinalLegs.FirstOrDefault(l => l.IsMap);

    /// <summary>
    /// Picks the way in to the approach: the transition whose initial fix is the fix the
    /// aircraft is arriving from, which is what a flight management system does, and
    /// otherwise the first transition published.
    /// </summary>
    public ApproachTransition? TransitionFor(string? fromFix)
    {
        if (Transitions.Count == 0) return null;

        if (!string.IsNullOrEmpty(fromFix))
        {
            var match = Transitions.FirstOrDefault(t =>
                string.Equals(t.IafIcao, fromFix, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return Transitions[0];
    }

    public string Describe() =>
        $"{Kind}{SuffixedRunway()}{(HasLnavVnav ? " LNAV/VNAV" : HasLnav ? " LNAV" : "")}" +
        $"{(HasLpv ? " LPV" : "")} final {FinalLegs.Count} legs, missed {MissedLegs.Count} legs" +
        (MissedAltitudeFt > 0 ? $", missed alt {MissedAltitudeFt:F0} ft" : "");

    private string SuffixedRunway() =>
        string.IsNullOrEmpty(Runway) ? "" : $" RWY {Runway}{(string.IsNullOrEmpty(Suffix) ? "" : $" {Suffix}")}";
}

/// <summary>A navaid or fix resolved from the navigation database, with its position.</summary>
public sealed class NavFix
{
    public string Icao { get; set; } = "";
    public string Region { get; set; } = "";
    public char Type { get; set; } = 'W';
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeMeters { get; set; }

    /// <summary>
    /// True when the controller sent this aircraft to this fix, rather than it being a leg
    /// of the route. It is what lets the pilot tell a clearance that has been withdrawn from
    /// one that is still standing: the sim's own ATC clears the SimVar when the aircraft has
    /// been handed on, and the direct-to stands with it.
    /// </summary>
    public bool WasSentByAtc { get; set; }

    public bool HasPosition => !double.IsNaN(Latitude) && !double.IsNaN(Longitude) && (Latitude != 0 || Longitude != 0);

    public override string ToString() => $"{Icao} ({Type}) {Latitude:F5},{Longitude:F5}";
}

/// <summary>
/// What the navigation database and the route hold right now, for the console and the
/// panel.
/// </summary>
public sealed class NavStatusSnapshot
{
    public int RouteLegCount { get; set; }
    public int RouteGeometryLegs { get; set; }
    public string FirstLeg { get; set; } = "";
    public string LastLeg { get; set; } = "";
    public List<string> UnresolvedFixes { get; set; } = new();
    public bool HasApproach { get; set; }
    public string? ApproachText { get; set; }
    /// <summary>The approach type as a name, for a short panel line.</summary>
    public string ApproachType { get; set; } = "";
    /// <summary>The runway the published approach serves, in the "25L" form.</summary>
    public string ApproachRunway { get; set; } = "";
    public int MissedApproachLegs { get; set; }
    public string? MissedApproachText { get; set; }
    /// <summary>
    /// True when the simulator has answered for the arrival airport's approaches. Without it
    /// the route's approach is "none" because nothing has loaded, which is a different
    /// statement from an airport that publishes no approaches at all.
    /// </summary>
    public bool ArrivalApproachesLoaded { get; set; }
    /// <summary>How many flyable approaches the navigation database publishes at the arrival airport.</summary>
    public int ArrivalApproachCount { get; set; }
    public int ActiveLegIndex { get; set; }
    public string ActiveLeg { get; set; } = "";
    public double ActiveLegDistanceMeters { get; set; }
    public double CrossTrackMeters { get; set; }
    public string? DirectToFix { get; set; }
    public int RebuildCount { get; set; }
    public string RouteText { get; set; } = "";
}

/// <summary>
/// Reads one facility payload sequentially. Fields arrive in exactly the order they were
/// added to the definition, so a single sequential reader is sufficient for every record
/// type; the sizes are the ones the SimConnect facility documentation publishes for each
/// field name (STRING8, FLOAT32, FLOAT64, INT32).
/// </summary>
public static class ApproachRecordParser
{
    public static ApproachInfo ParseApproach(FacilityDataRecord rec)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        var approach = new ApproachInfo
        {
            Kind = (ApproachKind)r.ReadInt32(),
            Suffix = DesignatorLetter(r.ReadInt32()),
            Runway = RunwayName(r.ReadInt32(), r.ReadInt32()),
            FafIcao = r.ReadString(8),
            FafRegion = r.ReadString(8),
            FafHeadingDeg = r.ReadFloat32(),
            FafAltitudeFt = Geo.MetersToFeet(r.ReadFloat32()),
            FafType = (char)r.ReadInt32(),
            MissedAltitudeFt = r.ReadFloat32(),
            HasLnav = r.ReadInt32() != 0,
            HasLnavVnav = r.ReadInt32() != 0,
            HasLp = r.ReadInt32() != 0,
            HasLpv = r.ReadInt32() != 0,
        };

        return approach;
    }

    public static ApproachLeg ParseLeg(FacilityDataRecord rec)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        var leg = new ApproachLeg
        {
            PathType = (LegPathType)r.ReadInt32(),
            Ident = r.ReadString(8),
            Region = r.ReadString(8),
            FixType = (char)r.ReadInt32(),
            Latitude = r.ReadFloat64(),
            Longitude = r.ReadFloat64(),
            AltitudeMeters = r.ReadFloat64(),
            FlyOver = r.ReadInt32() != 0,
            DistanceIsMinutes = r.ReadInt32() != 0,
            TrueDegree = r.ReadInt32() != 0,
            Turn = (ProcedureTurn)r.ReadInt32(),
            OriginIcao = r.ReadString(8),
            OriginRegion = r.ReadString(8),
            OriginType = (char)r.ReadInt32(),
            OriginLatitude = r.ReadFloat64(),
            OriginLongitude = r.ReadFloat64(),
            OriginAltitudeMeters = r.ReadFloat64(),
            // THETA and RHO come before COURSE in the published order: THETA is the bearing
            // from the reference navaid and RHO the distance to it, which is what an arc leg
            // is described by.
            ThetaDeg = r.ReadFloat32(),
            RhoMeters = r.ReadFloat32(),
            CourseDeg = r.ReadFloat32(),
            RouteDistance = r.ReadFloat32(),
            AltitudeQualifier = (AltitudeConstraintKind)r.ReadInt32(),
            Altitude1Meters = r.ReadFloat32(),
            Altitude2Meters = r.ReadFloat32(),
            SpeedLimitKt = r.ReadFloat32(),
            VerticalAngleDeg = r.ReadFloat32(),
            ArcCenterIcao = r.ReadString(8),
            ArcCenterRegion = r.ReadString(8),
            ArcCenterType = (char)r.ReadInt32(),
            ArcCenterLatitude = r.ReadFloat64(),
            ArcCenterLongitude = r.ReadFloat64(),
            ArcCenterAltitudeMeters = r.ReadFloat64(),
            IsIaf = r.ReadInt32() != 0,
            IsIf = r.ReadInt32() != 0,
            IsFaf = r.ReadInt32() != 0,
            IsMap = r.ReadInt32() != 0,
        };

        return leg;
    }

    public static ApproachTransition ParseTransition(FacilityDataRecord rec)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        // The APPROACH_TRANSITION payload order is exactly the one the MSFS 2024 docs
        // publish: TYPE, IAF_ICAO, IAF_REGION, IAF_TYPE, IAF_ALTITUDE, DME_ARC_ICAO,
        // DME_ARC_REGION, DME_ARC_TYPE, DME_ARC_RADIAL, DME_ARC_DISTANCE, NAME,
        // N_APPROACH_LEGS. Getting the order wrong is silent garbage rather than an error,
        // which is why the parser is pinned by tests that build the payload field by field.
        var transition = new ApproachTransition
        {
            Type = (ApproachKind)r.ReadInt32(),
            IafIcao = r.ReadString(8),
            IafRegion = r.ReadString(8),
            IafType = (char)r.ReadInt32(),
            // IAF_ALTITUDE is published in feet, unlike FAF_ALTITUDE and the leg
            // ALTITUDE1/ALTITUDE2 constraints, which are metres. Converting it as if it
            // were metres inflated the transition's initial altitude by 3.28.
            IafAltitudeFt = r.ReadFloat32(),
            DmeArcIcao = r.ReadString(8),
            DmeArcRegion = r.ReadString(8),
            DmeArcType = (char)r.ReadInt32(),
            DmeArcRadial = r.ReadInt32(),
            DmeArcDistanceMeters = r.ReadFloat32(),
            Name = r.ReadString(8),
            LegCount = r.ReadInt32(),
        };

        return transition;
    }

    /// <summary>Reads a waypoint record from the waypoint facility entry point.</summary>
    public static NavFix ParseWaypoint(FacilityDataRecord rec)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        return new NavFix
        {
            Type = 'W',
            Latitude = r.ReadFloat64(),
            Longitude = r.ReadFloat64(),
            AltitudeMeters = r.ReadFloat64(),
            Icao = r.ReadString(8),
            Region = r.ReadString(8),
        };
    }

    /// <summary>Reads an NDB record from the NDB facility entry point, which carries no ident.</summary>
    public static NavFix ParseNdb(FacilityDataRecord rec)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        return new NavFix
        {
            Type = 'N',
            Latitude = r.ReadFloat64(),
            Longitude = r.ReadFloat64(),
            AltitudeMeters = r.ReadFloat64()
        };
    }

    /// <summary>Reads a VOR record from the VOR facility entry point, which carries no ident.</summary>
    public static NavFix ParseVor(FacilityDataRecord rec)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        return new NavFix
        {
            Type = 'V',
            Latitude = r.ReadFloat64(),
            Longitude = r.ReadFloat64(),
            AltitudeMeters = r.ReadFloat64()
        };
    }

    /// <summary>
    /// Builds a runway name from the published number and designator. The facility API
    /// numbers runways 1-36 and names the designator 1 = left, 2 = right, which is the
    /// inverse of the spelling the rest of the pilot uses ("25L").
    /// </summary>
    public static string RunwayName(int number, int designator)
    {
        if (number is <= 0 or > 36) return "";

        var suffix = designator switch
        {
            1 => "L",
            2 => "R",
            3 => "C",
            4 => "W",
            5 => "A",
            6 => "B",
            _ => ""
        };

        return $"{number:00}{suffix}";
    }

    private static string DesignatorLetter(int multipleIndicator) =>
        multipleIndicator switch
        {
            > 0 and <= 26 => ((char)('A' + multipleIndicator - 1)).ToString(),
            _ => ""
        };
}
