using System;
using System.Collections.Generic;

namespace MSFS2024AIPilot.Core.State;

public enum WaypointType
{
    Airport,
    Ndb,
    Vor,
    Intersection,
    Runway,
    User,
    Sid,
    Star,
    Approach,
    MissedApproach,
}

public sealed class FlightPlanWaypoint
{
    public string Ident { get; set; } = "";
    public string? Region { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }       // MSL feet
    public WaypointType Type { get; set; }
    public string? Airway { get; set; }
    public double SpeedConstraint { get; set; }    // knots, 0 = none
    public double AltitudeConstraint { get; set; } // feet, 0 = none
    public bool IsFlyOver { get; set; }

    public string? Runway { get; set; }
    public bool HasPosition => !double.IsNaN(Latitude) && !double.IsNaN(Longitude);

    public override string ToString() =>
        $"{Ident} ({Latitude:F5},{Longitude:F5}) @{Altitude:F0}ft";
}
