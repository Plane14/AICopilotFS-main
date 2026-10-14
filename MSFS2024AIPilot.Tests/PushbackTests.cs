using System;
using System.IO;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The pushback decisions, as pure functions of the state the simulator publishes about
/// the tug. Everything here would otherwise be reachable only with a running simulator
/// and an aircraft parked on a stand.
/// </summary>
public class PushbackPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    private static AircraftState OnAStand(PushbackStatus? tug = null) => new()
    {
        OnGround = true,
        GroundSpeed = 0,
        Pushback = tug ?? new PushbackStatus { Available = true },
        ATC = { OnParkingSpot = true }
    };

    private static PushbackProgress Attached(DateTime now, double lat, double lon) => new()
    {
        Requested = true,
        RequestedUtc = now.AddSeconds(-20),
        SawTug = true,
        AttachedUtc = now.AddSeconds(-5),
        AttachPoint = (lat, lon),
        LastMovedUtc = now.AddSeconds(-1)
    };

    [Fact]
    public void ATugIsOnOfferOnlyOnAParkingSpaceThatHasOne()
    {
        Assert.True(PushbackPlanner.OnOffer(OnAStand()));

        var ramp = OnAStand();
        ramp.ATC.OnParkingSpot = false;
        Assert.False(PushbackPlanner.OnOffer(ramp));

        var airborne = OnAStand();
        airborne.OnGround = false;
        Assert.False(PushbackPlanner.OnOffer(airborne));

        var noTug = OnAStand(new PushbackStatus { Available = false });
        Assert.False(PushbackPlanner.OnOffer(noTug));

        var alreadyAttached = OnAStand(new PushbackStatus { Attached = true });
        Assert.False(PushbackPlanner.OnOffer(alreadyAttached));
    }

    [Fact]
    public void NothingToWaitForAtAStandWithoutTheService()
    {
        // The ramp case: there is nothing to ask a controller for, so the aircraft must not
        // sit on the brakes waiting for a pushback that was never going to come.
        var ramp = OnAStand(new PushbackStatus { Available = false });

        Assert.Equal(PushbackDecision.GiveUp,
            PushbackPlanner.Decide(ramp, new PushbackProgress(), Now));
    }

    [Fact]
    public void AStandThatOffersATugIsWaitedOn()
    {
        Assert.Equal(PushbackDecision.Wait,
            PushbackPlanner.Decide(OnAStand(), new PushbackProgress(), Now));
    }

    [Fact]
    public void TheTugIsGivenAMomentToTakeUpTheLoad()
    {
        // Attached for one second and already clear of the stand: the tug has not had time
        // to get going, and dismissing it here leaves the aircraft short of a taxiway.
        var state = OnAStand(new PushbackStatus { Attached = true });
        state.ATC.OnParkingSpot = false;

        var progress = Attached(Now, state.Latitude, state.Longitude);
        var justHookedUp = new PushbackProgress
        {
            Requested = progress.Requested,
            RequestedUtc = progress.RequestedUtc,
            SawTug = true,
            AttachedUtc = Now.AddSeconds(-1),
            AttachPoint = progress.AttachPoint,
            LastMovedUtc = progress.LastMovedUtc
        };

        Assert.Equal(PushbackDecision.Towing,
            PushbackPlanner.Decide(state, justHookedUp, Now));
    }

    [Fact]
    public void TheTugIsDismissedOnceTheAircraftIsClearOfTheStand()
    {
        var state = OnAStand(new PushbackStatus { Attached = true });
        var progress = Attached(Now, state.Latitude, state.Longitude);

        // 30 m down the stand from where the tug hooked up, which is past the tail
        // clearance the manoeuvre exists for.
        var (lat, lon) = Geo.OffsetByMeters(progress.AttachPoint!.Value.lat,
            progress.AttachPoint.Value.lon, 30, 0);
        state.Latitude = lat;
        state.Longitude = lon;

        Assert.Equal(PushbackDecision.Dismiss,
            PushbackPlanner.Decide(state, progress, Now));
    }

    [Fact]
    public void TheTugIsDismissedWhenTheAircraftLeavesTheParkingSpace()
    {
        // Pushed only a few metres, but the stand is behind it: the manoeuvre has done what
        // it exists for.
        var state = OnAStand(new PushbackStatus { Attached = true });
        state.ATC.OnParkingSpot = false;

        Assert.Equal(PushbackDecision.Dismiss,
            PushbackPlanner.Decide(state, Attached(Now, state.Latitude, state.Longitude), Now));
    }

    [Fact]
    public void APushbackThatGetsNowhereIsStillStopped()
    {
        // Still on the stand and barely moved, but the manoeuvre has run as long as it is
        // allowed to.
        var state = OnAStand(new PushbackStatus { Attached = true });
        var progress = Attached(Now, state.Latitude, state.Longitude);
        var longRunning = new PushbackProgress
        {
            Requested = progress.Requested,
            RequestedUtc = progress.RequestedUtc,
            SawTug = true,
            AttachedUtc = Now.AddMinutes(-4),
            AttachPoint = progress.AttachPoint,
            LastMovedUtc = progress.LastMovedUtc
        };

        Assert.Equal(PushbackDecision.Dismiss,
            PushbackPlanner.Decide(state, longRunning, Now));
    }

    [Fact]
    public void ATugAttachedToAMotionlessAircraftIsWrittenOff()
    {
        // Brakes off, tug on, and nothing has moved for half a minute: this pushback is not
        // happening, and holding the aircraft on the stand for the whole duration would be
        // worse than taxiing it out under its own power.
        var state = OnAStand(new PushbackStatus { Attached = true });
        state.GroundSpeed = 0;
        var progress = Attached(Now, state.Latitude, state.Longitude);
        var stalled = new PushbackProgress
        {
            Requested = progress.Requested,
            RequestedUtc = progress.RequestedUtc,
            SawTug = true,
            AttachedUtc = progress.AttachedUtc,
            AttachPoint = progress.AttachPoint,
            LastMovedUtc = Now.AddSeconds(-45)
        };

        Assert.Equal(PushbackDecision.GiveUp,
            PushbackPlanner.Decide(state, stalled, Now));
    }

    [Fact]
    public void TheWaitForATugIsBounded()
    {
        // A controller that cannot grant a pushback must not park the aircraft at the gate
        // for ever, exactly as an unavailable IFR clearance does not.
        var state = OnAStand();
        var progress = new PushbackProgress { Requested = true, RequestedUtc = Now.AddSeconds(-60) };

        Assert.Equal(PushbackDecision.Wait,
            PushbackPlanner.Decide(state, progress, Now.AddSeconds(-14)));
        Assert.Equal(PushbackDecision.GiveUp,
            PushbackPlanner.Decide(state, progress, Now.AddSeconds(16)));
    }

    [Fact]
    public void TheWaitIsMeasuredFromTheAskNotFromArrivingOnTheStand()
    {
        // The request is what starts the clock: an aircraft that has been standing for
        // minutes without a controller being asked has not been waiting for a tug.
        var state = OnAStand();
        var progress = new PushbackProgress { Requested = true, RequestedUtc = Now.AddSeconds(-5) };

        Assert.Equal(PushbackDecision.Wait,
            PushbackPlanner.Decide(state, progress, Now));
    }

    [Fact]
    public void PushedMetersMeasuresFromWhereTheTugAttached()
    {
        var state = OnAStand(new PushbackStatus { Attached = true });
        var progress = Attached(Now, 41.4, 2.3);
        var (lat, lon) = Geo.OffsetByMeters(41.4, 2.3, 40, 0);
        state.Latitude = lat;
        state.Longitude = lon;

        Assert.Equal(40, PushbackPlanner.PushedMeters(state, progress), 0);
    }

    [Fact]
    public void ATugThatHasLetGoLeavesNothingToDismiss()
    {
        // The sim ended the pushback itself: the stand-holding stage is over, and there is
        // no tug left to fire anything at.
        var state = OnAStand();
        var progress = Attached(Now, state.Latitude, state.Longitude);
        var released = new PushbackProgress
        {
            Requested = progress.Requested,
            RequestedUtc = progress.RequestedUtc,
            SawTug = true,
            AttachedUtc = Now.AddSeconds(-30),
            AttachPoint = progress.AttachPoint,
            LastMovedUtc = progress.LastMovedUtc
        };

        Assert.Equal(PushbackDecision.Dismiss,
            PushbackPlanner.Decide(state, released, Now));
    }
}

/// <summary>
/// The pushback is the one request whose grant is not a clearance variable: the answer
/// arrives as the tug attaching to the aircraft. These tests pin how that is asked for,
/// and what the controller's own words about a pushback mean.
/// </summary>
public class PushbackRequestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pushback-hub-" + Guid.NewGuid().ToString("n"));

    public PushbackRequestTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// A hub over a parked aircraft. The source is pinned to the simulator's own ATC so the
    /// test does not depend on whether BeyondATC happens to be running on this machine, and
    /// the log directory is a temp one so no real player log is read.
    /// </summary>
    private (AtcCommunicationHub hub, AircraftController aircraft) Hub(Action<AircraftState>? configure = null)
    {
        var aircraft = new AircraftController();
        aircraft.State.OnGround = true;
        aircraft.State.GroundSpeed = 0;
        configure?.Invoke(aircraft.State);

        var hub = new AtcCommunicationHub(aircraft,
            new AtcOptions { LogDirectoryOverride = _root }, _ => { });
        hub.Start();
        hub.SetSource(AtcSource.Native);
        return (hub, aircraft);
    }

    private static void ParkedOnAStand(AircraftState state)
    {
        state.Pushback.Available = true;
        state.ATC.OnParkingSpot = true;
        state.ATC.AirportIsTowered = true;
    }

    [Fact]
    public void PushbackIsARequestTheConsoleCanName()
    {
        Assert.Equal(AtcRequestKind.Pushback, AtcRequestKinds.Parse("pushback"));
        Assert.Equal(AtcRequestKind.Pushback, AtcRequestKinds.Parse("tug"));
        Assert.Equal("pushback", AtcRequestKinds.Label(AtcRequestKind.Pushback));
    }

    [Fact]
    public void TheFlightLogicDecidesWhenATugIsAskedFor()
    {
        var (hub, aircraft) = Hub(ParkedOnAStand);
        using var _ = hub;

        // Without the flight logic saying the aircraft is prepared, the ATC bridge asks for
        // nothing: it has no way to know a gate checklist is unfinished or that the
        // departure clearance has not been dealt with yet. A taxi request may well go out -
        // that one needs nothing of the aircraft - but not a pushback.
        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());

        Assert.False(hub.PushbackRequested);
        Assert.NotEqual(AtcRequestKind.Pushback, hub.Requests.ActiveKind);
    }

    [Fact]
    public void APushbackIsAskedForOnAStandThatOffersOne()
    {
        var (hub, aircraft) = Hub(ParkedOnAStand);
        using var _ = hub;
        hub.PushbackWanted = true;

        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());

        Assert.True(hub.PushbackRequested);
        Assert.True(hub.Requests.IsBusy, "the controller has been asked and is being waited on");
        Assert.Equal(AtcRequestKind.Pushback, hub.Requests.ActiveKind);
    }

    [Fact]
    public void NothingIsAskedForAtAStandThatOffersNoTug()
    {
        var (hub, aircraft) = Hub(state =>
        {
            ParkedOnAStand(state);
            state.Pushback.Available = false;
        });
        using var _ = hub;
        hub.PushbackWanted = true;

        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());

        Assert.False(hub.PushbackRequested);
        Assert.False(hub.Requests.IsBusy);
    }

    [Fact]
    public void ThePushbackRequestIsDroppedOnceTheTugArrives()
    {
        var (hub, aircraft) = Hub(ParkedOnAStand);
        using var _ = hub;
        hub.PushbackWanted = true;

        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());
        Assert.True(hub.PushbackRequested);

        // The answer arrives as the tug attaching to the aircraft.
        aircraft.State.Pushback.Attached = true;
        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());

        Assert.False(hub.PushbackRequested, "the question has been answered");
    }

    [Fact]
    public void ThePushbackRequestIsDroppedOffTheParkingSpace()
    {
        var (hub, aircraft) = Hub(ParkedOnAStand);
        using var _ = hub;
        hub.PushbackWanted = true;

        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());
        Assert.True(hub.PushbackRequested);

        aircraft.State.ATC.OnParkingSpot = false;
        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());

        Assert.False(hub.PushbackRequested);
    }

    [Fact]
    public void TheTugAttachingIsWhatGrantsTheRequest()
    {
        // The request is confirmed by the tug rather than by any clearance variable, so a
        // pressed menu line that changes nothing the ATC state tracks still counts.
        var (hub, aircraft) = Hub(ParkedOnAStand);
        using var _ = hub;
        hub.PushbackWanted = true;

        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());
        Assert.True(hub.Requests.IsBusy);

        aircraft.State.Pushback.Attached = true;
        hub.Requests.Tick();

        Assert.Equal(AtcRequestStatus.Accepted, hub.Requests.Status);
        Assert.False(hub.Requests.IsBusy);
    }

    [Fact]
    public void PushbackApprovalIsReadFromWhatTheControllerSays()
    {
        var approved = BeyondAtcBridge.ParseAtcText("ASXGS, pushback approved", "ground");

        Assert.True(approved.PushbackApproved);
        Assert.False(approved.PushbackComplete);
    }

    [Fact]
    public void PushbackCompleteIsReadFromWhatTheControllerSays()
    {
        foreach (var text in new[]
        {
            "ASXGS, pushback complete",
            "pushback is complete",
            "pushback completed, contact ground on 121.7",
            "pushback finished"
        })
        {
            var instruction = BeyondAtcBridge.ParseAtcText(text, "ground");
            Assert.True(instruction.PushbackComplete, text);
            Assert.False(instruction.PushbackApproved, text);
        }
    }

    [Fact]
    public void ACancelledPushbackIsNotAFinishedOne()
    {
        // A withdrawal is neither of the two things the pilot acts on: no tug is coming and
        // none has to be released, and the stand-holding stage times out on its own.
        var cancelled = BeyondAtcBridge.ParseAtcText("pushback request cancelled", "ground");

        Assert.False(cancelled.PushbackApproved);
        Assert.False(cancelled.PushbackComplete);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>
/// The panel reads the pushback block off /status, so the field names and their JSON types
/// are part of the contract between the host and the in-sim HTML.
/// </summary>
public class PushbackPanelContractTests
{
    private static string StatusJson()
    {
        var pilot = new AIPilot();
        var body = PanelHttpBridge.BuildStatus(pilot);
        pilot.Dispose();
        return body;
    }

    [Fact]
    public void StatusCarriesThePushbackBlock()
    {
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());

        Assert.True(doc.RootElement.TryGetProperty("pushback", out var pushback), "panel reads pushback");

        foreach (var name in new[] { "summary", "attached", "available", "requested",
                                      "waiting", "releasing", "pushedMeters" })
        {
            Assert.True(pushback.TryGetProperty(name, out _), $"panel reads pushback.{name}");
        }

        foreach (var name in new[] { "attached", "available", "requested" })
        {
            var value = pushback.GetProperty(name);
            Assert.True(value.ValueKind == System.Text.Json.JsonValueKind.True ||
                        value.ValueKind == System.Text.Json.JsonValueKind.False,
                $"pushback.{name} must be a boolean");
        }
    }
}
