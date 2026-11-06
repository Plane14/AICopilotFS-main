using MSFS2024AIPilot.Core.Airports;
using MSFS2024AIPilot.Core.State;
using MSFS2024AIPilot.Core.Traffic;

namespace MSFS2024AIPilot.Tests;

public class TrafficTests
{
    // A runway along latitude 47.0, centred on longitude 8.0, heading 090 (east).
    private static RunwayInfo RunwayEw() => new()
    {
        Latitude = 47.0,
        Longitude = 8.0,
        HeadingDegTrue = 90,
        LengthMeters = 2000,
        AltitudeMeters = 100,
    };

    private static AircraftState Own() => new()
    {
        Latitude = 47.0,
        Longitude = 8.01,
        Altitude = 500,
        OnGround = true,
    };

    private static TrafficContact Contact(uint id, double lat, double lon, double altFt,
        bool onGround = true, double heading = 90, double gs = 0) => new()
    {
        ObjectId = id,
        Latitude = lat,
        Longitude = lon,
        AltitudeMslFt = altFt,
        HeadingDegTrue = heading,
        GroundSpeedKt = gs,
        OnGround = onGround,
        IsUserSim = false,
        LastSeenUtc = DateTime.UtcNow,
    };

    [Fact]
    public void DistanceToSegment_finds_off_centreline_distance()
    {
        // Point ~111 m north of the segment midpoint.
        var d = TrafficGeometry.DistanceToSegmentMeters(47.001, 8.0, 47.0, 7.99, 47.0, 8.01);
        Assert.InRange(d, 100, 125);
    }

    [Fact]
    public void DistanceToSegment_point_on_segment_is_zero()
    {
        var d = TrafficGeometry.DistanceToSegmentMeters(47.0, 8.005, 47.0, 7.99, 47.0, 8.01);
        Assert.True(d < 1.0, $"expected ~0, got {d}");
    }

    [Fact]
    public void DistanceToSegment_clamps_to_segment_ends()
    {
        // Far beyond the east end: distance measured to the end, not the infinite line.
        // 0.04 deg lon at 47N is ~3.0 km.
        var d = TrafficGeometry.DistanceToSegmentMeters(47.0, 8.05, 47.0, 7.99, 47.0, 8.01);
        Assert.InRange(d, 2800, 3300);
    }

    [Fact]
    public void Ground_aircraft_on_runway_marks_entry_unsafe()
    {
        var own = Own();
        var contact = Contact(7, 47.0, 8.0, 600); // on the centreline
        var a = TrafficEvaluator.Evaluate(new List<TrafficContact> { contact }, own, RunwayEw(), new TrafficOptions());
        Assert.NotNull(a.DepartureRunwayOccupant);
        Assert.True(a.RunwayEntryUnsafe);
    }

    [Fact]
    public void Ground_aircraft_far_off_runway_is_not_an_occupant()
    {
        var own = Own();
        var contact = Contact(7, 47.01, 8.0, 600); // ~1.1 km north of centreline
        var a = TrafficEvaluator.Evaluate(new List<TrafficContact> { contact }, own, RunwayEw(), new TrafficOptions());
        Assert.Null(a.DepartureRunwayOccupant);
    }

    [Fact]
    public void Airborne_aircraft_on_final_is_detected()
    {
        var own = Own();
        // 2 NM west of the west threshold (8.0 - ~0.029 deg lon at 47N), 600 ft above
        // the 100 m (~328 ft) field, heading east toward the runway.
        var contact = Contact(9, 47.0, 7.971, 1000, onGround: false, heading: 90, gs: 120);
        var a = TrafficEvaluator.Evaluate(new List<TrafficContact> { contact }, own, RunwayEw(), new TrafficOptions());
        Assert.NotNull(a.ArrivalOnDepartureRunway);
        Assert.True(a.RunwayEntryUnsafe);
    }

    [Fact]
    public void User_sim_record_is_excluded()
    {
        var own = Own();
        var contact = new TrafficContact
        {
            ObjectId = 1,
            Latitude = own.Latitude,
            Longitude = own.Longitude,
            AltitudeMslFt = own.Altitude,
            IsUserSim = true,
            LastSeenUtc = DateTime.UtcNow,
        };
        var live = new List<TrafficContact>();
        var tracker = new ContactTracker();
        tracker.Add(contact);
        var removed = tracker.RemoveStale(TimeSpan.FromSeconds(30));
        var a = TrafficEvaluator.Evaluate(removed, own, RunwayEw(), new TrafficOptions());
        Assert.Empty(a.Contacts);
        Assert.False(a.RunwayEntryUnsafe);
    }

    [Fact]
    public void Stale_contacts_age_out()
    {
        var tracker = new ContactTracker();
        var old = new TrafficContact
        {
            ObjectId = 5,
            Latitude = 47.0,
            Longitude = 8.0,
            LastSeenUtc = DateTime.UtcNow.AddSeconds(-30),
        };
        tracker.Add(old);
        Assert.Empty(tracker.RemoveStale(TimeSpan.FromSeconds(4)));
    }
}
