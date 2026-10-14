# MSFS 2024 AI Pilot

Fully automated gate-to-gate AI pilot for Microsoft Flight Simulator 2024, interfaced with BeyondATC and aware of the loaded user aircraft's characteristics and performance.

## Architecture

```
MSFS2024AIPilot.sln
├── MSFS2024AIPilot.Core/     Core library (SimConnect, performance, flight phases, ATC bridge)
├── MSFS2024AIPilot.Host/     Out-of-process EXE host (recommended by MSFS SDK for stability)
└── MSFS2024AIPilot.Panel/    In-sim toolbar panel (HTML/JS + IPC client)
```

### Why an out-of-process EXE

Per the MSFS 2024 SimConnect SDK: *"The recommended method of writing an add-on is to build it out-of-process as an application (an `.exe` file) rather than in-process (a WASM module). This is because out-of-process applications provide more stability, if they crash they typically will not crash Microsoft Flight Simulator... and they are easier to build, test and debug."*

## Key components

| Component | File | Purpose |
|---|---|---|
| SimConnect client | `Core/SimConnectManager.cs` | Native P/Invoke wrapper over `SimConnect.dll` (no managed SDK dependency) |
| Facilities API | `Core/AirportFacilitiesLoader.cs` | `SimConnect_AddToFacilityDefinition` / `SimConnect_RequestFacilityData`, assembles the airport ground graph |
| Airport data | `Core/AirportData.cs` | Runways, taxi points, taxi paths, parking spots, ground graph |
| Taxi routing | `Core/TaxiRouter.cs` | A* shortest path over the taxiway graph with runway/weight/width constraints |
| Pushback decisions | `Core/PushbackPlanner.cs` | `PushbackDecision` over `PushbackProgress`/`PushbackTuning`: ask, wait, tow, dismiss, give up |
| Weather model | `Core/WeatherModel.cs` | Pure wind/turbulence/ice/surface arithmetic + `TurbulenceTracker` (derives turbulence from the load factor) |
| Aircraft envelope | `Core/FlightEnvelope.cs` | Pure speed/load-factor/vertical-rate/engine-limit arithmetic + `EnvelopeGuard`, applied to every command the pilot makes |
| Traffic monitor | `Core/TrafficMonitor.cs` | Other aircraft via `SimConnect_RequestDataOnSimObjectType`, runway occupancy and conflict assessment |
| Aircraft state | `Core/AircraftState.cs` | Strongly-typed live aircraft/nav/ATC state |
| Aircraft performance | `Core/AircraftPerformance.cs` | V-speeds, climb/cruise/descent profiles, takeoff/landing distances |
| Config parser | `Core/ConfigParser.cs` | Reads `aircraft.cfg`, `flight_model.cfg`, `engines.cfg`, `flight_performance.cfg` |
| Flight plan parser | `Core/ConfigParser.cs` | Reads MSFS `.pln` AceXML waypoints, SID/STAR/approach |
| Navdata loader | `Core/NavDataLoader.cs` | Approaches, transitions, final and published missed approach legs, fix positions |
| Navdata records | `Core/NavData.cs` | The facility payload layouts and what each field means |
| Lateral navigation | `Core/NavPathFollower.cs` | Leg sequencing, cross-track correction, fly-by turn anticipation |
| Route builder | `Core/NavPathBuilder.cs` | The plan's fixes placed from navdata, ending in the published approach |
| Flight phases | `Core/FlightPhaseManager.cs` | Dynamic goal-based phase state machine |
| Hybrid control | `Core/HybridAutopilot.cs` | Native autopilot + PID direct surface control |
| ATC bridge | `Core/AtcCommunicationHub.cs` | One hub over both controllers: source detection, auto-requests, radio tuning |
| Sim ATC read/write | `Core/NativeAtc.cs` | `ATC …` SimVars in, `ATC_MENU_*` / `XPNDR_SET` / COM tuning out |
| ATC menu probing | `Core/NativeAtc.cs` | `AtcRequestEngine`: grace period, display-order probe, wrong-entry memory |
| ATC window capture | `Core/AtcScreenCapture.cs` | GDI capture of the desktop or the simulator window |
| ATC menu reading | `Core/AtcMenuScreenReader.cs` | Line segmentation and phrase matching off the screen |
| ATC text parsing | `Core/BeyondAtcBridge.cs` | `AtcInstruction` model and the phrase parser both controllers feed |
| BeyondATC read | `Core/BeyondAtcLink.cs` | Tails `Player.log` for runway allocations, hotkeys and the CPDLC endpoint |
| BeyondATC write | `Core/BeyondAtcCommand.cs` | Posts the "Fire Action 1" key to the BeyondATC window; probes CPDLC read-only |
| Orchestrator | `Core/AIPilot.cs` | Main control loop and phase executors |
| Panel bridge | `Core/PanelHttpBridge.cs` | Localhost HTTP bridge so the HTML panel can send commands |
| Panel IPC client | `Panel/PilotIpcClient.cs` | Named-pipe IPC for the in-sim panel |
| Checklist engine | `Core/ChecklistManager.cs`, `Core/ChecklistExecutor.cs` | Runs the aircraft's own checklists step by step |
| Checklist reader | `Core/ChecklistParser.cs` | Reads MSFS 2024 `SimBase.Document` checklist XML and the legacy text format |
| Checklist bindings | `Core/ChecklistTextResolver.cs` | Maps free-text items onto Asobo events/simvars |
| Checklist fallback | `Core/GenericChecklists.cs` | Built-in checklist flow when the aircraft ships none |
| Checklist sim bridge | `Core/ChecklistSimHost.cs` | `IChecklistHost` over `SimConnectManager`: events out, simvars in |

## Control strategy

Hybrid, as requested:

- **Ground & precision phases** (taxi, takeoff roll, final approach, landing, go-around): direct surface control via PID loops on `AXIS_ELEVATOR_SET`, `AXIS_AILERONS_SET`, `AXIS_RUDDER_SET`, `THROTTLE_SET`.
- **Airborne enroute** (climb, cruise, descent): MSFS native autopilot (`AP_PANEL_HEADING_HOLD`, `AP_ALT_VAR_SET_ENGLISH`, `AP_SPD_VAR_SET`, `AP_NAV1_HOLD`, `AP_APR_HOLD`, `AUTO_THROTTLE_ARM`).

The PID gains in `HybridAutopilot` are per-phase `PidLoop` instances (pitch, bank, speed, altitude)
seeded conservatively; expect per-aircraft tuning. The model is meant to be tuned using the
`ControlTargets` struct per phase, not globally.

## Taxi-out and the hold-short

The hold-short is a real behaviour, not the end of a route. `FlightPhase.TaxiOut` runs three
stages, `TaxiOutStage.Route` → `HoldShort` → `LineUp` - with a fourth, `Pushback`, in front of
them at a stand that offers a pushback (see below):

- **Route.** `PlanTaxiToRunway` builds a route from the aircraft's nearest node to the *reachable*
  hold-short for the assigned runway, chosen by shortest route rather than by proximity to the
  runway centre. A taxi point payload carries no runway of its own, so the runway a hold-short
  guards is only known from the runway-type taxiway paths attached to it (`TaxiRouter.HoldShortsFor`).
  At a parallel-strip airport the nearest hold-short to the centre can belong to the other runway.
  The route also carries `HoldShortBefore` set to the assigned runway, so it cannot cross it.
- **HoldShort.** Reached from position, not from an ATC phrase: the stop point is the hold-short
  node pulled back along the arriving leg by `TaxiHoldShortStandoffMeters`, or - with no ground
  graph - the simulator's own `ATC RUNWAY DISTANCE` while the aircraft is pointing down the strip.
  The pilot stops, requests takeoff clearance, and waits. `_atRunwayHoldShort` is what unlocks the
  takeoff request in `AtcCommunicationHub.UpdatePhaseRequests`; when it was only set by a parsed
  "line up and wait" - a phrase the sim's own ATC never publishes as a clearance var - the request
  was never made.
- **LineUp.** Entered on a takeoff clearance with a clear runway. Steers at the threshold of the
  assigned runway end (`RunwayInfo.ThresholdFor`, derived from the runway centre) and hands over to
  `FlightPhase.Takeoff` once aligned. A line-up that will not complete returns to the hold-short
  after a retry delay.

Traffic gates the hold-short: a clearance is not permission to enter an occupied runway, so
`ExecuteHoldShort` keeps holding while `TrafficAllowsRunwayEntry` is false and reports why.

## Runway back-taxi

A runway is usable from where the taxiway joins it. The length ahead of that joining point is what
the takeoff roll has to fit in, so a strip joined halfway along does not give an aircraft its
published run however long the runway is - and a controller deals with that by saying "enter
runway 08 and backtrack, line up and wait".

Neither the simulator's own ATC nor BeyondATC publishes a back-taxi flag, so the decision is
geometric and belongs to `Core/BacktrackPlanner.cs`:

1. The joining point is the hold-short node the taxi route ends at, or the aircraft's own position
   once it is on the runway.
2. `Geo.ProjectOntoSegment` projects it onto the runway axis, giving how far along the strip it is
   and how far off the centreline it is. A point more than half the width plus a margin off the
   centreline is a taxiway junction beside the runway rather than a joining point, and says nothing
   about how much runway is left.
3. The length ahead of that point is compared with the runway the roll needs
   (`AIPilot.TakeoffRunMeters`, which is `PerformanceCalculator.CalculateRequiredRunwayLength`
   including its 15% margin, or a v²/2a estimate from the rotation speed when the aircraft files no
   performance tables) times `BacktrackRunwayMargin`.

A back-taxi shorter than `BacktrackWorthwhileMeters` is not worth the turn, and a controller's
explicit "backtrack" instruction (`AtcInstruction.Backtrack`) is authority enough on its own.

The manoeuvre adds a fourth stage, `HoldShort` → `Backtrack` → `LineUp`: the aircraft taxis up the
centreline to a turn point `BacktrackTurnStandoffMeters` short of the threshold - short of, so the
turn happens on the strip rather than off the end of it - and then `ExecuteLineUp` swings the nose
round and brings it back onto the axis. The stage moves more slowly than a taxi because the aircraft
is rolling opposite to the takeoff direction, and `BacktrackTimeout` returns it to the hold-short
rather than taxiing off the threshold.

## Pushback

At a stand that offers one, a pushback is what actually moves the aircraft off the stand: taxing
out under the aircraft's own power is what a pilot does at a stand without the service, and doing
it at a gate is how a nosewheel ends up turned past its stops. So `FlightPhase.TaxiOut` runs a
fourth stage in front of the others, `TaxiOutStage.Pushback` → `Route` → `HoldShort` → `Backtrack`
→ `LineUp`, and the brake is released when the pushback is over rather than when the taxi-out
starts.

A pushback is the one clearance with no SimVar of its own. What the simulator publishes is the
*tug*: the airport-services block `Core/AircraftController.cs` requests as `PUSHBACK` carries
`PUSHBACK ATTACHED`, `PUSHBACK AVAILABLE`, `PUSHBACK WAIT`, `PUSHBACK STATE:index` (the documented
enum, straight/left/right) and `PUSHBACK ANGLE` (the tug's heading, in radians). So "approved" is
observed as the tug attaching and "complete" as it letting go, and the request is confirmed by the
same signal - `AtcRequestEngine.Accepted` for `AtcRequestKind.Pushback` reads `PUSHBACK ATTACHED`,
so a pressed menu line that changes nothing the ATC state tracks still counts as a grant.

The request is made through the hub exactly like the others, which keeps the sim ATC menu probe,
the BeyondATC action key and the cooldown in one place. When to ask is not the ATC bridge's
business: the flight logic owns the same flag the hold-short uses (`AtcCommunicationHub.PushbackWanted`,
set from `AIPilot.UpdatePhaseContext` when the aircraft is standing on a stand that offers a tug,
the gate checklist is done and the departure clearance has been dealt with), because the bridge has
no way to know an unfinished checklist. With auto-requests off, `pushback request` asks out of band.

The decisions - ask, wait, hold, dismiss, write it off - are pure functions of that state plus
where the aircraft stood when the tug hooked up, in `Core/PushbackPlanner.cs`, so they are exercised
without a simulator. `PushbackDecision` is one of `Wait`, `Towing`, `Dismiss` or `GiveUp`, over a
`PushbackProgress` record of what the stage has observed:

| Situation | Decision |
|---|---|
| Stand offers a tug, none attached, none asked for | `Wait` (the ramp and no-service cases are `GiveUp` immediately) |
| Asked, and the tug has not arrived inside `AttachTimeout` (75 s) | `GiveUp` - a controller that cannot grant a pushback must not park the aircraft at the gate for ever |
| Tug attached for less than `Settle` (3 s) | `Towing` - it has not had time to take up the load |
| Pushed `ClearMeters` (25 m), or off the parking space, or `MaxDuration` (180 s) | `Dismiss` |
| Attached, brakes off, still not moving after `TugStallTimeout` (30 s) | `GiveUp` - a tug that tows nothing is a pushback that is not happening |

Dismissing is done by state rather than by toggle, for the same reason the parking brake is: the
brake axis holds the aircraft where the tug left it, the parking brake is *set*
(`PARKING_BRAKE_SET`, not the `PARKING_BRAKES` toggle), and the tug is told to let go with
`TUG_DISABLE` - the event the SDK publishes for exactly that. `PUSHBACK ATTACHED` clearing is the
confirmation; a tug that has not let go inside `PushbackDetachTimeout` is released with
`TOGGLE_PUSHBACK`, which is only safe because the state says a tug is still attached, and if even
that fails the aircraft taxes out anyway rather than standing on the stand for ever. The taxi route
is then re-planned from where the aircraft now stands, because the one drawn on arrival started at
the parking space.

A controller that says so in words is acted on the same way: `AtcInstruction.PushbackApproved`
("pushback approved", "cleared to push back") is what the stand-holding stage is waiting for, and
`PushbackComplete` ("pushback complete", "pushback finished") releases the tug there and then,
ahead of the geometry. A *cancelled* pushback is deliberately neither.

## Weather, turbulence and icing

Wind was read into `AircraftState` and then used by nothing, turbulence was not read at all,
and icing was a single `OAT`-derived flag. The pilot now reacts to all three, and the
arithmetic lives in `Core/WeatherModel.cs` as pure functions (plus a small stateful
`TurbulenceTracker`) so the reactions are exercised without a simulator.

A new `WEATHER` SimConnect block (`Core/AircraftController.cs`, decoded by index and pinned
by `AircraftDataLayoutTests.WeatherBlockOrderMatchesTheDecoderIndices`) feeds:

| Datum | Field | Use |
|---|---|---|
| `ACCELERATION BODY Y` | `GLoad` (g) | turbulence estimate, via the load factor |
| `AMBIENT IN CLOUD` | `AmbientInCloud` | icing risk + overcast hint |
| `AMBIENT PRECIP STATE` | `PrecipitationState` | freezing-rain/snow icing risk |
| `STRUCTURAL ICE PCT` | `StructuralIcePercent` | airframe ice + approach-speed bump |
| `PITOT ICE PCT` | `PitotIcePercent` | reported for diagnostics |
| `STRUCTURAL DEICE SWITCH` | `StructuralDeiceOn` | reported for diagnostics |
| `SURFACE CONDITION` | `SurfaceCondition` | wet/icy/snow runway roll & braking |
| `SURFACE INFO VALID` | `SurfaceInfoValid` | whether the surface enum is meaningful |

**Wind.** `AMBIENT WIND DIRECTION`/`VELOCITY` are resolved into headwind and crosswind along a
compass course. On final (`ExecuteFinalApproach`) and on the enroute lateral path
(`SetSteeringHeading`) the heading command carries a wind-correction angle so the *ground
track* follows the course — previously a crosswind left a persistent drift, because a heading
was flown for a track. `AIPilot.TakeoffRunMeters` scales the runway the roll needs by a
head/tailwind factor and a surface-containment factor, so a headwind shortens the back-taxi
decision and a tailwind or an icy strip lengthens it. Winds and courses are always combined in
the same (compass) frame.

**Turbulence.** MSFS publishes no turbulence variable, so `TurbulenceTracker` separates the
steady load (`1 g` level, `1/cos(bank)` turning) from the high-frequency deviation with a slow
baseline and an RMS of the squared deviation, mapped to `Calm/Light/Moderate/Severe`. Reactions:
a lower bank ceiling on final and a softened bank response (`ControlTargets.MaxBankDeg` /
`ControlScale`) so gusts are not chased, and an enroute speed reduced toward a
turbulence-penetration speed (`TargetAirspeedKt`, never applied to an ATC speed assignment and
never below a floor). Ground chop is ignored while on the ground.

**Icing.** `ChecklistEnvironment.IcingRisk` is now real - moisture (in cloud or falling) in the
icing temperature band, or ice already on the structure - rather than `OAT is > -5 and < 8`, so
the aircraft's own "as required" ice-protection items (pitot heat, windshield deice) engage when
they should. Structural ice above the perception threshold raises the approach reference speed,
logs a prominent one-shot warning, and puts the weather line into a warning state. The
airframe/engine anti-ice switches are deliberately not fired from guessed event names - the
binding system in `Core/ChecklistTextResolver.cs` owns the event set.

`status` prints a `Weather:` line, and `GET /status` carries a `weather` block (wind,
crosswind, turbulence, icing, surface) the in-sim panel renders on one line and warnings in red.

## Operating envelope: overspeed, G, vertical rate and engines

`AircraftPerformance` already carried Vfe/Vno/Vne/Vle/Vlo and nothing read them, and the phase
executors commanded fixed figures: `SetVerticalSpeed(-1800)` on every descent,
`Math.Clamp(s.VerticalSpeed, -2000, 2500)` on every climb, `TargetPitch = 15` on the go-around,
`TargetAirspeed = 60` on the flare. A light single was commanded a jet's descent rate, a jet was
flared at a speed below its own stall, and neither was protected against an over-g. The limit
arithmetic now lives in `Core/FlightEnvelope.cs`, as pure functions plus one stateful
`EnvelopeGuard`, so the reactions are exercised without a simulator.

The limits come from two sources, and the simulator's own beats the config's, because it already
folds in what a config file cannot: the configuration actually selected, the altitude at which a
Mach limit converts to an indicated speed, and the barber-pole needle. `AIRSPEED BARBER POLE`,
`OVERSPEED WARNING` and `STALL WARNING` were declared in the `SYSTEMS` block and never decoded;
they are the aircraft's own answer, and a config V-speed (`max_speed` → Vne, `max_speed_cruise` →
Vno) is the fallback for an aircraft whose barber pole the simulator does not report. A limit that
is unknown is zero and never invented, so an aircraft publishing neither is simply unlimited
rather than wrongly capped.

| Datum | Field | Use |
|---|---|---|
| `ACCELERATION BODY Y` | `GLoad` | load-factor limit, via the same reading the turbulence tracker uses |
| `AIRSPEED MACH` | `Mach` | decoded; the Mach limit is reported and read for profile |
| `AIRSPEED BARBER POLE` | `BarberPoleKt` | Vmo, the primary speed limit |
| `OVERSPEED WARNING` | `OverspeedWarning` | the aircraft's own overspeed verdict, reported before any arithmetic |
| `STALL WARNING` | `StallWarning` | the aircraft's own stall verdict, for diagnostics |
| `GENERAL ENG EXHAUST GAS TEMPERATURE` | `EngineEgtC[]` | EGT redline by engine type |
| `GENERAL ENG PCT MAX RPM` | `EngineMaxRpmPct[]` | RPM redline |

The engine limits are requested in their own `ENGINE_LIMITS` block rather than appended to
`ENGINES`, because SimConnect answers an unrecognised datum name for the whole definition: a
name this build rejected would then report engine count zero on a running aircraft, and the
engine state is what every phase of flight reads. A rejected block leaves the EGT arrays empty,
the limiter degrades to full authority, and nothing else is affected - the same fail-safe shape
as the traffic monitor, which can only conclude "unsafe".

**Where the limits are applied** - once per frame, before the executors run, so every command
they issue is clamped against the same resolved envelope:

| Limit | Where |
|---|---|
| Speed | `EnvelopeGuard.CappedAirspeedKt` on every `SetAirspeed` in climb, cruise, descent and final, on the flare and on the go-around. The stall floor raises a command below it, and the configuration limit takes precedence over the floor, because exceeding Vfe damages the flaps while a low command only loses control |
| Load factor | `FlightEnvelope.LimitElevatorForLoadFactor` inside `HybridAutopilot.UpdateDirectControl`, which reads the *measured* g rather than the commanded pitch - a gust or a hard flare can be near the limit while the pitch command is still modest. Full authority above half a g of headroom, scaled back below it, and an unload past the limit |
| Vertical rate | `EnvelopeGuard.CappedVerticalRateFpm`, which both clamps to the aircraft's own envelope and **ramps** the command in, because a pull-up is a load factor: `n - 1 = (V/g)·dgamma/dt`. A commanded step becomes a rate the airframe can carry, and with no headroom the current rate is held rather than commanded |
| Engines | `EnvelopeGuard.MaxThrottle` as a ceiling on the takeoff and go-around throttle commands and on the direct-control speed loop. It is a *ceiling*, not the current lever position scaled - an aircraft at 80% with full authority still has full power available |

A breach is classified as one of `OperatingLimit` and reported once on the frame it starts
(`[Limits] …`), rather than on every frame of it; a held condition is carried continuously by
`GET /status` and the `Envelope:` line in the panel. The negative-g and flap/gear limits are
tested against the configuration actually selected, so landing flaps at 130 kt is a `FlapSpeed`
breach while the same speed clean is not.

Two things are deliberately *not* done. The 250 kt limit below 10,000 ft is computed
(`FlightEnvelope.RegulatorySpeedKt`) and applied only to the aircraft's own perf-derived target,
never to a controller's speed assignment - the same convention the turbulence speed reduction
follows. And there is no "maximum pitch attitude" function, because a steady 15° climb at any
bank a pilot would hold is at or below the limit; overstress comes from the *rate* of rotation,
which is what the elevator limiter and the vertical-rate ramp bound.

`status` prints a `Limits:` line with the current verdict and the resolved envelope,
`GET /status` carries a `limits` block (breach, load factor, Vmo, Vfe, Vle, stall floor, climb
and descent rates, EGT redline) which the in-sim panel renders as `Envelope:` in red when a
limit is exceeded, and `EngineLimitFraction` reports how far into the engine limit the aircraft
is.

`FlightEnvelopeTests` pins the arithmetic: the barber pole beating a config V-speed, the
tightest of the three configuration limits, a stall floor that never contradicts Vfe, the
load-factor limiter scaling then unloading at both ends, the vertical-rate ramp holding the
current rate with no headroom, the EGT redline cutting the throttle, and a piston getting no
EGT limit while a jet does - plus `LocaleIndependenceTests`, which pins that these strings keep
the `.` separator on a machine whose culture would print `2,90`.

## Traffic awareness

There is no traffic-list simulator variable, so other aircraft are read with
`SimConnect_RequestDataOnSimObjectType`, which delivers one `SIMCONNECT_RECV_SIMOBJECT_DATA` record
per object inside a radius, each with its own object ID. `TrafficMonitor` re-issues the request on a
timer (the request persists, so a refresh replaces it), keeps contacts by object ID, and ages out
anything the simulator stops reporting.

`TrafficEvaluator` turns the contacts into a `TrafficAssessment`: airborne conflicts inside range and
a vertical gap, ground traffic on the runway centreline, and aircraft established on final for the
runway being used. `RunwayEntryUnsafe` is what the tax-out hold-short consults.

The monitor is deliberately fail-safe - it can only conclude "unsafe". A rejected request disables
traffic awareness for the session with one log line rather than guessing, and a stale contact can
only hold the aircraft, never release it.

## Aircraft performance auto-derivation

Two sources, merged at startup (hybrid, as requested):

1. **Runtime SimVars** — `TOTAL WEIGHT`, `FUEL TOTAL QUANTITY WEIGHT`, `CG PERCENT`, `MAX GROSS WEIGHT`, live N1/N2/FF/EGT, IAS, alt, winds, OAT, baro.
2. **Installed config files** — the loaded aircraft is matched by title (`AircraftLoaded` system state) inside the Community folders, then `flight_performance.cfg` (V1/Vr/V2 tables, takeoff/landing distance tables, climb/cruise/descent profiles), `flight_model.cfg` (geometry, weights, V-fe/V-ne/V-le), `engines.cfg` (max thrust) and `aircraft.cfg` (ICAO type, wake category) are parsed.

If a `flight_performance.cfg` is absent, fallback V-speeds are derived from the ICAO wake-turbulence category.

## Airport ground network (facilities API)

`RequestAirportData(icao)` pulls the airport ground graph through the facilities API:

```cpp
SimConnect_AddToFacilityDefinition(hSimConnect, DEF, "OPEN AIRPORT");
  ... ICAO, LATITUDE, LONGITUDE, ALTITUDE, MAGVAR, TRANSITION_ALTITUDE ...
  "OPEN RUNWAY" ... "CLOSE RUNWAY"
  "OPEN TAXI_POINT" ... "CLOSE TAXI_POINT"
  "OPEN TAXI_PARKING" ... "CLOSE TAXI_PARKING"
  "OPEN TAXI_PATH" ... "CLOSE TAXI_PATH"
"CLOSE AIRPORT"
SimConnect_RequestFacilityData(hSimConnect, DEF, REQUEST, ICAO);
```

Each record arrives as a `SIMCONNECT_RECV_FACILITY_DATA` message whose payload is laid out in exactly the field order declared above, followed by `SIMCONNECT_RECV_FACILITY_DATA_END`.

What it is used for:

- **Runway data** — true runway heading, length and width, plus runway-end identification (`PRIMARY_NUMBER`/`PRIMARY_DESIGNATOR`). Runway headings come back **true** and are converted to magnetic with the airport `MAGVAR`, because the autopilot works in magnetic.
- **Transition altitude** — read from the airport instead of hardcoding 18,000 ft.
- **Taxi routing** — taxi points/parkings become graph vertices and taxi paths become edges, then `TaxiRouter.FindRoute` runs A* to the departure runway hold-short or an arrival gate.
- **Constraints** — routing honours published weight ratings against the aircraft's actual weight, taxiway widths, closed paths, and confines runway edges to the assigned runway.

The loaded data is exposed on `GET /status` under `airports`, and the console has `airport <icao>` and `taxi` commands to inspect it.

### Known limitation: the airport reference frame

Taxi points and parking spots are delivered as `biasX`/`biasZ` offsets in metres from the airport reference point along the "longitudinal" and "latitudinal" axes. Microsoft documents neither the sign nor which cardinal direction those axes point, so a hard-coded mapping would silently produce a garbage graph. Instead `AirportFacilitiesLoader.CalibrateFrame` tries all eight candidate orientations and keeps the one that puts the graph closest to the aircraft's known position — the aircraft is standing on the airport, so the correct orientation puts nearby nodes within tens of metres while wrong ones land kilometres away. `AirportData.PathResolutionRatio` reports what fraction of taxiway paths resolved to known nodes, which is the observable signal if the index namespaces do not line up.

## ATC integration

Both controllers are bridged through one hub — `Core/AtcCommunicationHub.cs` — so the
flight logic never needs to know which one is talking. Everything an ATC says is
normalised into an `AtcInstruction` (see `Core/BeyondAtcBridge.cs`) and handled by
`AIPilot.HandleAtcInstruction`.

### What an instruction changes

Every field of an `AtcInstruction` the pilot can act on is acted on:

| Instruction | Effect |
|---|---|
| `Squawk` | `XPNDR_SET` with the code in BCD |
| `Runway` | overrides the plan's runway for departure *and* arrival, and re-plans the taxi route if the aircraft is still taxiing out |
| `AltitudeFt` | the level the climb and cruise hold; below cruise it is also the descent's level-off |
| `Mach` / `SpeedKnots` | the speed the climb, cruise, descent and final approach fly (Mach converts to IAS from the aircraft's own IAS/TAS ratio above FL280) |
| `HeadingDeg` | the heading the climb, cruise, descent and go-around steer, overriding the flight plan's own path until it is released |
| `DirectToFix` | a direct-to ("fly direct to GLASG"): the fix is resolved from the navigation database and flown as a one-leg path until the plan takes over again |
| `ClearedForTakeoff` | `_takeoffCleared`, which is what unlocks the hold-short |
| `ClearedToLand` | the landing clearance. Without one, `ExecuteFinalApproach` goes around below 500 ft AGL at a towered arrival; after two unanswered go-arounds the aircraft lands rather than loop forever |
| `ClearedForApproach` | clears the approach and cancels again on a go-around |
| `HoldShort` + runway | `TaxiConstraints.HoldShortBefore` - the route may not cross that strip |
| `ClearedToCross` + runway | adds the strip to `TaxiConstraints.CrossableRunways` so the route may cross it |
| `Backtrack` | the back-taxi stage (see above) |
| `LineUpAndWait` | forces `TaxiOut`, because a cleared instruction issued while parked has to move the aircraft |
| `HoldPosition` | parking brake by state, not by toggle |
| `GoAround` | `FlightPhase.GoAround` |
| `PushbackApproved` | what the stand-holding stage is waiting for - the tug itself is the confirmation |
| `PushbackComplete` | releases the tug there and then, ahead of the geometry |
| `ResumeOwnNavigation` | releases every assignment above, including a direct-to, which is what LNAV means |
| `IsHandoff` + frequency | tunes COM1 to the station that is next in charge |

A **named taxi route** ("via ALPHA one left on BRAVO") is followed rather than replaced by the
shortest route: `TaxiRouter.ResolveTaxiwayChain` turns the names into graph nodes and
`TaxiRouter.FindRouteViaChain` joins them in order.

### Top of descent

How far there is left to fly is computed every frame from the aircraft's own position. The plan's
total length is not a live distance — it does not shrink as the aircraft moves — so it is used only
to pick a cruise altitude when the plan files none.

## Flight plan and navdata following

The air is flown on the plan's own path, built from two sources and merged at load time (`Core/NavPathBuilder.cs`):

1. **The plan's fixes.** A MSFS 2024 `.pln` is a list of *names*: the built-in planner publishes waypoint
   identifiers and no positions at all (`ConfigParser.cs` records those at `NaN`, deliberately, so a
   positionless waypoint cannot land on null island).
2. **The navigation database.** `Core/NavDataLoader.cs` requests each named fix from the facilities
   API (`OPEN WAYPOINT` / `OPEN NDB` / `OPEN VOR` by ICAO) and the approaches published at the airport.

The facilities API is the only route to navdata from an out-of-process client. The legacy
`fs9gps` GPS variables that used to expose the flight plan are deprecated in MSFS 2024, and the
planned-route API is a WASM feature; the documented SimConnect path is
`SimConnect_AddToFacilityDefinition` + `SimConnect_RequestFacilityData`. One request per airport
carries the approach headers, a second the transitions and their legs, a third the final approach
legs and a fourth the **published missed approach legs** — each with its own payload layout, so a
definition the simulator rejects for one record type cannot take the rest of the approach down
with it.

A facility definition must open with one of the four documented entry points (`AIRPORT`,
`WAYPOINT`, `NDB`, `VOR`) and carry exactly one of them, with every member nested inside a
container and a child opened before its parent closes. `APPROACH_TRANSITION`,
`FINAL_APPROACH_LEG` and `MISSED_APPROACH_LEG` are children of `APPROACH`, so they are opened
while the approach is still open; a fix is asked as three separate definitions (waypoint, NDB,
VOR) because a second entry point in one definition is refused. Breaking any of those rules is
answered field-by-field with `DATA_ERROR [add facility field 'X' to definition N]`, and after the
first rejection the whole definition is refused - so the structure is kept pure
(`NavDataLoader.FacilityDefinitions`) and pinned by `NavFacilityDefinitionTests` without a
simulator.

What the loader assembles (`Core/NavData.cs`):

| Record | What it carries |
|---|---|
| `APPROACH` | type (ILS/LOC/RNAV/LPV…), suffix (RNAV-Z), runway, FAF, missed-approach altitude, capability flags |
| `FINAL_APPROACH_LEG` | fix position, path terminator, turn direction, published course, altitude restriction (at / at-or-above / at-or-below / between), vertical angle, speed limit, IAF/IF/FAF/MAP flags |
| `MISSED_APPROACH_LEG` | the same, starting at the missed approach point |
| `APPROACH_TRANSITION` | the IAF and the legs that fly up to the final approach fix |
| `WAYPOINT` / `NDB` / `VOR` | the position of one fix by ICAO |

Units are the ones the documentation publishes and nothing else: `FAF_ALTITUDE` and leg
`ALTITUDE1` are metres, `MISSED_ALTITUDE`, `IAF_ALTITUDE` and `SPEED_LIMIT` are feet/knots,
`ROUTE_DISTANCE` is metres-or-minutes by the `DISTANCE_MINUTE` flag. Course is magnetic unless
`TRUE_DEGREE` says otherwise. Getting any of that wrong is silent garbage rather than an error,
which is why the parser is pinned by tests that build the payloads field by field.

### Following it

`Core/NavPathFollower.cs` is the lateral-navigation engine, and it is a pure function of position:
a chain of legs where leg *i* runs from leg *i-1*'s fix to its own. Each frame it answers which leg
is being flown, the course to steer (leg course corrected by a cross-track error, so an aircraft
that drifted off the line is steered back onto it rather than flown straight at the fix), the
signed cross-track, and when to sequence to the next leg. Sequencing is geometric rather than
radius-only: a fix counts as reached inside a catch radius that scales with ground speed
(120–3000 m), or once the aircraft has passed its abeam point, or — for a fly-by fix — once it is
inside the turn-anticipation distance, which is what stops the corner of a turn being cut from the
outside. A leg whose published terminator is a fly-over is held until it is actually reached, and
the last leg of a path completes rather than running off its end.

`Core/ArcGeometry.cs` turns the two published arc terms (`RF`, a constant radius turn, and `AF`, a
DME arc about a reference navaid) into a chain of points, because an arc has no place in a route of
straight legs and connecting its ends cuts the corner.

`AIPilot.SteerLateral` is what actually drives it: a controller's heading is a vector and wins; a
`direct to <fix>` clearance is flown as a one-leg path until the fix is reached, at which point the
plan takes over; otherwise the route's course is steered through the heading bug (native heading
hold) and only when there is nothing to follow does the pilot fall back on the simulator's own
`AP_NAV1_HOLD` — which is the honest fallback, because the simulator's GPS is following the same
plan.

### The controller's named fix

The sim's own ATC names the fix it has sent this aircraft to on a SimVar (`ATC NEXT WAYPOINT NAME`,
plus `ATC NEXT WAYPOINT ALTITUDE` when a crossing restriction applies). Those are a name and an
altitude rather than a position, so `AIPilot.UpdateAtcWaypointDirect` feeds both through the same
machinery as a parsed direct-to: the fix is looked up in the navigation database, flown directly
to once it resolves, and the crossing altitude becomes the level the aircraft is flown at. The
SimVar is cleared when the aircraft is handed on, which is what releases the direct-to; nothing is
acted on before the fix resolves, because a waypoint the database does not carry is a name and not
a place.

### Published missed approaches

The missed approach is a route like any other procedure, so it is kept beside the arrival route and
not bolted onto it. `AIPilot.ExecuteGoAround` flies it leg by leg from the missed approach point —
a go-around is the chain of fixes the airport publishes for exactly this situation, not the runway
heading — and the level-off is the approach's published missed-approach altitude. A controller's
heading still wins, and with no published missed approach (or no navdata) the runway heading is
what is flown.

### Console

`nav [all]` prints the route (leg count, first and last fix, unresolved fixes), the published approach
and its missed approach, the leg being followed and its distance, and any direct-to in progress.
`GET /status` carries the same as a `nav` block, which the in-sim panel renders on one line.

### Console-driven instructions

`atc process <text>` feeds a phrase through the same parser on a thread that is not the control
loop, so it is queued and applied on the next frame rather than mutating state the loop is
halfway through updating.

### The simulator's own ATC (`Core/NativeAtc.cs`)

The default ATC only speaks and offers a numbered menu, but every clearance it issues is
also published as a SimVar. That is the interface used here:

- **Read.** `Core/AircraftController.cs` requests an `ATC` block (ATC CLEARED
  IFR/TAXI/TAKEOFF/LANDING, assigned altitude, designated runways/gates, future agent,
  waypoint altitudes, towered flags, taxi path distance, flight-plan deviations) plus an
  `ATCTEXT` block of string datums (`ATC DESIGNATED RUNWAY …`, `ATC FUTURE AGENT NAME`,
  `ATC ID`, `COM ACTIVE FREQ IDENT/TYPE`). `NativeAtcMonitor` turns the transitions into
  instructions on the rising edge, so a clearance that was already granted before the
  pilot started watching is never replayed as new.
- **Write.** The ATC menu is driven with the `ATC_MENU_OPEN` / `ATC_MENU_n` /
  `ATC_MENU_CLOSE` key events. Because the menu is only addressable by position,
  `AtcRequestEngine` is a state machine: open the menu, watch the ATC SimVars for a
  grace period in case the controller grants the request by itself, then work through
  the entries in display order (`ATC_MENU_1` is the first line, `ATC_MENU_0` the
  tenth) until the expected clearance appears. It never blocks the control loop.
  Three safeguards keep a positional request from doing damage: an entry that changes
  the ATC state without granting what was asked is recorded as wrong for that
  circumstance and never pressed again while it is current; a change of circumstance
  aborts the probe; and the number of entries pressed per request is capped. Entries
  that worked before are stored per circumstance and tried first. It never sends a
  request the sim has no use for.
- **Reading the menu.** `Core/AtcScreenCapture.cs` and `Core/AtcMenuScreenReader.cs`
  can read the ATC window off the screen, as a backup to probing: capture, split the
  ink profile into lines, then recognise each line by rendering the candidate phrases
  and matching the pixels. It needs no OCR package, but it does need a screen capture
  per request and a visible simulator window, so it is opt-in — enable it with
  `atc ocr on` (and set `AtcOptions.MenuCaptureRegion` when the simulator runs
  borderless or on a second monitor). When it recognises a line, that line is pressed
  first; when it recognises nothing, probing runs exactly as before.
- **Radio.** Handoffs are auto-tuned by setting the COM1 standby side in hertz
  (`COM1_STBY_RADIO_SET_HZ`) and swapping (`COM1_RADIO_SWAP`); squawks go to the
  transponder as `XPNDR_SET` with the code in binary-coded decimal.

### BeyondATC (`Core/BeyondAtcLink.cs`, `Core/BeyondAtcCommand.cs`)

BeyondATC publishes no developer API: the documented integration channel is CPDLC/ACARS
for aircraft add-ons (`support@beyondatc.net`), which requires aircraft-side support and
is not plug-and-play. What an external program *can* observe is its `Player.log`
(`AppData\LocalLow\Skirmish Mode Games, Inc\BeyondATC\`), which BeyondATC rewrites at the
start of each flight:

- the keys it imported into its preferences — `[Bindings] Hotkeys imported to
  PlayerPrefs: [0, 32, 0, 0, 0]` — which is how the "Fire Action 1" key is known,
- the local CPDLC server it starts, `[CPDLC] http endpoint listening on
  http://localhost:57698/`, and
- the aircraft identity and the runway it allocated, `[RunwayAlloc] ASXGS … result=24L`.

So: **read** means `BeyondAtcLogTail` tails the file with shared access (resetting when
the file is truncated and rewritten), and the parsed lines feed the instruction stream —
the allocated runway in particular. **Write** means the BeyondATC window is posted the
key its own options bind to "Fire Action 1", found by reading those preferences, or set
explicitly with `atc actionkey <vk>`. No focus is stolen.
`BeyondAtcCpdlcClient` discovers the CPDLC endpoint from the log and probes it read-only,
so `atc cpdlc` reports what the endpoint answers.

The trustworthy autonomous option with BeyondATC remains its own **Auto Respond**
setting, which makes its copilot read back for you; the AI pilot then only has to fly
what it is given.

### Automatic requests

`AtcCommunicationHub.UpdatePhaseRequests` asks for what a pilot asks for at each point:
taxi when ready to roll, takeoff while holding short, and landing on the approach
(skipped at uncontrolled airports, where the ATC menu carries nothing to press). The
IFR clearance is issued by `AIPilot.ExecuteStartup`, which then waits for
`ATC CLEARED IFR` before rolling out — unless the controller cannot grant it, in which
case the flow continues rather than parking the aircraft at the gate forever.

For the sim ATC each request is confirmed against the ATC SimVars; for BeyondATC the
action key is sent once and a cooldown decides when the next one may follow.

## Aircraft checklists

MSFS 2024 ships per-aircraft checklists as `SimBase.Document` XML in the aircraft
package's `/Checklist` folder: a `<Step>` per phase (`PREFLIGHT_GATE`,
`FLIGHT_TAKEOFF`, `LANDING_FINAL`, ...), pages inside each step, and
checkpoints inside each page. A checkpoint pairs a subject ("Parking brake")
with an expectation ("Set"), a `<Test>` made of `SimVar` comparisons, and the
copilot `<Action>`s that put the system in the expected state. The AI pilot
executes the aircraft's own checklist end to end:

- **Reader** (`Core/ChecklistParser.cs`) loads the aircraft's checklists on
  aircraft change, resolves `SubjectTT`/`ExpectationTT` strings through the
  package's `*.loc` files, follows `<IncludeCheckpointLibrary>` /
  `<IncludeChecklist>` references, filters by `LevelOfDetail` and evaluates
  `<Block>` conditions. The legacy `<Checklist><Group><List>` text format ships
  with many stock and Community aircraft and is mapped onto the event/simvar
  namespace by `Core/ChecklistTextResolver.cs`; items with no binding degrade to
  manual checks so an unmapped line never blocks the flow. Aircraft shipping no
  readable checklist get the built-in flow in `Core/GenericChecklists.cs`.
- **Executor** (`Core/ChecklistExecutor.cs`) runs one checkpoint at a time: it
  fires the checkpoint's events while the test is unmet, then verifies the
  simvars, honouring `Duration`, `Once`, `ForceValidate` and
  `WaitForUserToDoIt`. `Core/ChecklistSimHost.cs` is the `IChecklistHost` over
  `SimConnectManager` — events out, one shared per-simvar feed in.
- **Phase binding** (`Core/ChecklistManager.cs`) maps flight phases onto
  checklist steps. Engaging at the gate runs the whole `PREFLIGHT_GATE` list
  first; `AIPilot.ExecuteStartup` does not request clearance or start taxiing
  until that step completes. A running step always finishes, even across a phase
  change. Emergency and abnormal groups are deliberately not automated.

`GET /status` exposes the checklist block (source, format, step, item,
index/count, completed steps); the console `checklist` command prints the same
and supports `run <STEP>`, `skip`, `reset` and `reload`.

## Delegating control

In-sim toolbar panel (`Panel/PackageSources/html_ui/InGamePanels/AIPilotPanel/`) shows
phase, telemetry, and a **DELEGATE TO AI / TAKE CONTROL** button. The panel talks to
the host over a localhost HTTP bridge (`Core/PanelhttpBridge.cs`) on
`http://localhost:18540/aipilot/`.

Commands: `GET /status`, `POST /toggle`, `POST /engage`, `POST /disengage`, `POST /load-latest-plan`, `POST /atc?arg=<sub-command>`. `GET /status` also carries the live checklist block (source, step, running item, completed steps), the ATC block (which controller is active, tuned station, clearances, last instruction), the `pushback` block (tug attached, available, requested, waiting, releasing, pushed distance) and the `nav` block (route legs, leg being followed, published approach, published missed approach, unresolved fixes), all of which the panel renders.

A named-pipe server (`MSFS2024AIPilot.Delegation`) is also available for other clients: `{"cmd":"atc","arg":"request taxi"}` is the third way to reach the same ATC commands.

## Running

Console commands: `engage`, `disengage`, `toggle`, `plan <path.pln>`, `airport <icao>`, `taxi`, `nav`, `atc`, `checklist`, `pushback`, `status`, `quit`.

## Deployment and working instructions

### 1. Prerequisites

| Requirement | Notes |
|---|---|
| Microsoft Flight Simulator 2024 | Running, with a flight loaded |
| BeyondATC | Running, if you want ATC interaction |
| .NET 8 SDK | `winget install Microsoft.DotNet.SDK.8` |
| MSFS 2024 SDK | Provides `SimConnect.dll` (see *Development* below) |

`SimConnect.dll` must be next to the host executable. The build copies it automatically when the SDK is discoverable; if it is not, the host exits immediately with `SimConnect_Open failed with HRESULT 0x80004005` and that message means *SimConnect.dll was found but no simulator answered* — actually it means either MSFS is not running, or the DLL is not present at all.

### 2. Build and publish

```powershell
dotnet build -c Release
dotnet publish MSFS2024AIPilot.Host -c Release -o .\publish
```

The publish folder contains everything needed, including `SimConnect.dll`.

### 3. First run

```powershell
1. Start MSFS 2024 and load a flight (any aircraft; performance is auto-derived).
2. Start BeyondATC.
3. Run .\publish\MSFS2024AIPilot.Host.exe
```

Expected console output:

```
[STATUS] Connecting to SimConnect...
[STATUS] Ready. Press the delegate button to engage AI control.
```

If the first line is followed by `SimConnect connection failed`, MSFS was not reachable — start MSFS, restart the host.

### 4. Day-to-day use

Each flight is a three-step handoff:

1. **Plan** — load a route. The host auto-loads the sim's active flight plan at startup; to load another:

   ```
   plan C:\Users\<you>\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalState\packages\...
   ```

   With no argument, `plan` re-loads whichever plan the simulator currently has.

2. **Verify the airport data** — run `airport <ICAO>` (for example `airport KJFK`). A healthy load looks like:

   ```
   KJFK John F Kennedy Intl
     nodes: 214  runways: 4  paths resolved: 98%  frame: XEastZNorth
     hold-shorts: 8 (HoldShort, HoldShort, IlsHoldShort, ...)
     rwy 13L/31R hdg 135T len 12467ft magvar -13.0
       13L threshold 40.65123,-73.78845
   ```

   - **`paths resolved` below ~90%** means the taxi graph is incomplete; check the log for `PathResolutionRatio`, and remember the ground frame is calibrated against the aircraft, so load it **after** you are on the airport.
   - **`frame` is the orientation chosen by calibration.** If it looks wrong, park at a gate and re-run the command with `airport reload <ICAO>`; the frame is also recalibrated automatically once the aircraft is within range of the airport, so an arrival airport loaded at engine start fixes itself on touchdown.

3. **Delegate** — press the panel button, or type `engage` in the console.

The AI pilot then runs the full gate-to-gate sequence and hands control back at the destination. To retake control at any moment use `disengage`, or the panel's **TAKE CONTROL** button.

### 5. Console commands

| Command | Effect |
|---|---|
| `engage` | AI flies the aircraft from its current phase |
| `disengage` / `toggle` | Hand control back / switch modes |
| `plan [path]` | Load a flight plan; no argument re-reads the simulator's active plan |
| `airport [reload] <icao>` | Request and display runway, hold-short, taxiway and gate data. `reload` rebuilds the graph against your current position |
| `taxi` | Show the planned taxi route from your current position to the runway |
| `nav [all]` | Show the flight-plan route built from the navigation database, the published approach, its published missed approach, and the leg being followed |
| `traffic` | Show surrounding aircraft, runway occupancy and whether runway entry is safe |
| `checklist [run <STEP> \| skip \| reset \| reload]` | Aircraft checklist status; with no sub-command it lists steps, completed steps and the running checkpoint |
| `pushback [request \| dismiss]` | The pushback tug: what it is doing, ask the controller for one, release one that is on the aircraft |
| `atc [cmd]` | ATC bridge: status, `native`/`beyond`/`auto`, `request <kind>`, `tune <mhz>`, `squawk <code>`, `menu open\|close\|<n>`, `ocr show\|on\|off`, `process <text>`, `actionkey <vk>`, `autorequest on\|off`, `autotune on\|off`, `cpdlc` |
| `status` | Control state, phase, AGL, IAS, heading, VS, traffic, weather (wind/turbulence/ice/surface), pushback, active ATC source and clearances |
| `quit` | Disconnect and exit |

### 6. In-sim panel

The panel is an MSFS toolbar package laid out like the `msfs-panel-notepad` template:

```
MSFS2024AIPilot.Panel/
├── build.bat                       discover the SDK, run fspackagetool, copy to Community
├── package.xml                     project file for fspackagetool
├── manifest.json                   package manifest and game dependencies
├── layout.json                     file list of the built package (path, size, date)
├── PackageDefinitions/
│   └── msfs2024-ai-pilot.xml       which asset groups get packed into the .spb
└── PackageSources/
    ├── InGamePanels/
    │   └── msfs2024-ai-pilot.xml   registers PANEL_AI_PILOT (id, size, icon, HTML url)
    └── html_ui/
        ├── InGamePanels/AIPilotPanel/
        │   ├── AIPilotPanel.html   ingame-ui element + layout
        │   ├── AIPilotPanel.css    panel styling using the UI framework variables
        │   └── AIPilotPanel.js     custom element; polls the host bridge
        └── Textures/Menu/toolbar/
            └── ICON_TOOLBAR.svg    toolbar button icon
```

Build and install:

```powershell
cd MSFS2024AIPilot.Panel
.\build.bat
```

`build.bat` locates the SDK (`MSFS_SDK`, then `MSFS 2024 SDK` beside the repo), runs
`fspackagetool.exe package.xml`, and copies the result into the Community folder.

**`fspackagetool.exe` is not a standalone packager.** It launches the packaging pass
from inside an installed `FlightSimulator2024.exe` and waits for it, so building the
`.spb` needs MSFS 2024 installed on this machine. When the game is missing, `build.bat`
says so and exits 1 rather than hanging; the panel's HTML/JS/CSS can still be read and
reviewed in `PackageSources`. To point it at a non-Store install, write the full path to
`FlightSimulator2024.exe` into `MSFS 2024 SDK\Tools\bin\fspackagetool_overrideExePath.txt`.

Once installed, enable the panel in MSFS under **Options → General → Navigation →
Instrument Panel Layout**.

The panel requires the host to be running; it reaches it at
`http://localhost:18540/aipilot/`. If it shows `Offline`, the host is not running
or Windows Firewall is blocking loopback on that port.

Note on `new-push-button`: the in-sim framework renders the caption from its
**`title` attribute**, not from `textContent`, and the size modifiers are
`small`, `verysmall`, `big`, `Square` and `SmallSquare`.

### 7. Logs

- Console output and `aipilot.log` (next to the host EXE) record every phase
  change, taxi route, and ATC instruction.
- Facilities messages are prefixed `[Facilities]`, taxi routing `[Taxi]`, ATC
  `[ATC]`, runway data `[Approach]`.

### 8. If the AI pilot does not fly

Symptom and cause:

| Symptom | Cause |
|---|---|
| `SimConnect connection failed` | MSFS 2024 not running, or `SimConnect.dll` missing from the host folder |
| Panel shows `Offline` | Host not running, or port 18540 blocked |
| `[Traffic] traffic request was rejected…` in the log | The simulator build does not answer the object-type request, or a datum name is wrong. Traffic awareness is off for the session; everything else still flies, but nothing will hold short for traffic |
| Aircraft waits at the hold-short with no clearance | Expected. `_atRunwayHoldShort` is position derived, so the pilot stops there whether or not the controller has spoken yet. Check `status` for the takeoff clearance and for runway entry |
| `airport` prints no nodes | Wrong ICAO, or the airport is not cached until requested — use `airport <ICAO>` explicitly |
| Taxi goes direct to the runway | No ground data loaded, or `PathResolutionRatio` low |
| Takeoff rotates at the wrong speed | No `flight_performance.cfg` for that aircraft; the wake-category fallback is being used |

## Known limitations

- Traffic is observed, not predicted. The monitor sees aircraft the simulator is
  reporting around the player object; it makes no attempt at an intent model, so an
  aircraft about to land that the sim is not yet announcing is not seen.
- The airport reference frame is calibrated against the aircraft position (see above), so
  ground routing is only accurate once the aircraft is on that airport. The frame is now
  re-calibrated automatically once the aircraft is within 20 km of the airport, but the
  first taxi plan drawn up before arrival still uses the provisional frame.
- Taxi routing now follows ATC taxi route strings end-to-end: `ResolveTaxiwayChain` resolves named
  taxiways and `FindRouteViaChain` joins them in order, so "taxi to the 27 holding point via ALPHA
  one left on BRAVO" is followed rather than replaced by the shortest route.
- The final approach glideslope coupling uses `NAV GLIDE SLOPE ERROR:1`, which the data definitions read but the controller does not yet use to command pitch directly; `AP_APR_HOLD` carries the vertical path.
- SimBrief is not yet implemented; routes are taken from the active MSFS flight plan.
- BeyondATC's own ATC speech is not readable by any external program, so with BeyondATC the
  pilot follows the runways and stations that *are* observable (allocated runway from
  `Player.log`, tuned COM1 station, CPDLC endpoint) and uses its action key to request.
  Its **Auto Respond** setting is the recommended way to have readbacks made for you.
- The sim ATC menu is only addressable by position (`ATC_MENU_0…9`, where
  `ATC_MENU_1` is the first line), so a request is normally a probe over the entries
  rather than a named call; it is confirmed against the ATC SimVars, and entries
  found to do the wrong thing are remembered per circumstance rather than retried.
  Reading the menu off the screen with `atc ocr on` names the right entry but costs a
  screen capture per request and needs the simulator window to be visible.
- CPDLC is limited to endpoint discovery and read-only probing, because BeyondATC publishes
  the protocol to aircraft developers only; there is no Hoppie-network implementation.
- This is simulation-only code and must never be used for real-world aviation.

## Development

Requires the **.NET 8 SDK** (install with `winget install Microsoft.DotNet.SDK.8`), the .NET desktop workload (the projects target `net8.0-windows`), and the **MSFS 2024 SDK** on disk for `SimConnect.dll`.

`SimConnect.dll` is resolved in this order:
1. `-p:MsfsSdkDir="C:\path\to\MSFS 2024 SDK"` passed to the build
2. the `MSFS 2024 SDK` folder in this repo
3. `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\SimConnect SDK\lib\` (a Microsoft Store install)

```powershell
dotnet build
dotnet run --project MSFS2024AIPilot.Host
dotnet test
```

Console commands: `engage`, `disengage`, `toggle`, `plan <path.pln>`, `airport [reload] <icao>`, `taxi`, `traffic`, `nav [all]`, `atc`, `checklist`, `pushback`, `status`, `quit`.

### Testing without the simulator

The suite covers everything that does not need a running sim: ATC text parsing,
the SimConnect receive ABI (including the string-datum payload layout), the taxiway
graph and router, the airport reference frame calibration, the `.pln` parser, V-speed
interpolation and locale independence. The ATC suite drives `NativeAtcMonitor`
(sim-AAT SimVar transitions), `AtcRequestEngine` (menu probing against a fake menu and
clock) and `BeyondAtcLogTail`/`BeyondAtcLogParser` against real log lines captured from
a BeyondATC session. The checklist suite drives `ChecklistExecutor` and
`ChecklistManager` through a fake `IChecklistHost` (event firing, test
verification, durations, timeouts, step progression) and parses both checklist
XML formats from temp aircraft packages. `AtcMenuReaderTests` covers the image maths of
the off-screen menu reader - Otsu thresholding, line splitting, theme and scale
normalisation - with synthetic ATC menus, so it needs no display. `SimConnectReceiveAbiTests` is the one
to keep green — it asserts the native struct layouts against `SimConnect.h`, and
a regression there produces silent garbage rather than a crash.

`TrafficMonitorTests` and `HoldShortTests` cover the airspace and the ground markings:
`TrafficMonitorTests` drives the contact tracker and `TrafficEvaluator` with synthetic
positions, so airborne conflicts, runway occupancy, an aircraft on final and contact
expiry are all exercised as pure geometry. `HoldShortTests` covers the derived runway
thresholds, recovering which runway a hold-short guards from its taxiway paths, and
choosing the hold-short by shortest reachable route rather than by proximity to the
strip. `BacktrackPlannerTests` covers the back-taxi decision the same way: a synthetic
runway, joining points along and beside it, and the run the aircraft needs.
`GeoProjectionTests` pins that projection. `PidLoopTests` pins the derivative term, since
that bug survived review by looking like working code.

`MenuLineSegmentationTests` covers the band splitter with the shapes that used to break it
- a line whose ink has a gap in it - plus the numbering past the tenth band.
`AtcParserTests` pins every altitude, heading, speed and handoff spelling a controller
uses, which is what a text-parsed clearance is worth.

`PushbackTests` covers the stand-holding stage: the tug decisions (a stand that offers one is
waited on, a ramp is not; the tug is dismissed once the aircraft is clear or off the parking
space; a tug attached to a motionless aircraft is written off; the wait is measured from the
ask and is bounded), how the request is gated - nothing is asked for until the flight logic
says the aircraft is prepared, and the question is dropped once the tug arrives - and the two
phrases a controller ends a pushback with, which must not be confused with a cancellation.
`PushbackPanelContractTests` pins the `pushback` block the panel renders.

`WeatherModelTests` pins the weather arithmetic: the wind sign conventions (headwind,
crosswind-from-the-right, the crab angle that cancels the crosswind), the approach wind/ice/
turbulence speed increments and their overall cap, the icing temperature/moisture band, the
head-tailwind and contaminated-surface factors on the runway the roll needs, and
`TurbulenceTracker` across calm air, escalating gust amplitude, ignored ground chop, garbage
input and reset — all as pure functions, no simulator.

`NavDataTests` covers the navdata side: the approach and leg payloads are built field by
field exactly as the definitions declare them, so a reader that drifts out of step with its
definition reads garbage instead of matching. It covers the altitude restriction spellings,
the DME-arc groundwork (THETA/RHO, which sit between the origin and the course), the
approach-selection order (an ILS beats an RNAV to the same strip), the transition matched
to the fix the route arrives from, and the chosen approach kept separate from the route.
`NavPathFollowerTests` drives the lateral engine as pure geometry: cross-track correction
both sides of course, a catch radius that scales with ground speed, a fly-by fix turned
early and a fly-over held, and the last leg of an approach completing rather than running
past the threshold. `ArcGeometryTests` keeps an arc on its own circle and a right turn the
short way round. `PanelStatusContractTests` pins the `nav` block the panel renders and the
`weather` block it reads.

## Bugs found and fixed

Verified against `SimConnect.h` from the MSFS 2024 SDK and against the documented
`.pln` schema:

| Bug | Effect | Where |
|---|---|---|
| `SIMCONNECT_RECV` declared `{dwID, dwSize}` instead of `{dwSize, dwVersion, dwID}` | **No dispatch matched at all** — `dwID` was read from `dwSize`, and every derived struct was offset by 4 bytes | `SimConnectManager.cs` |
| `DatumType` values were wrong (`Float64=1`, `Int32=3`) | Bool SimVars were requested as `FLOAT32` (4 bytes), shifting the returned array so every later value read garbage | `SimConnectManager.cs` |
| `SIMCONNECT_RECV_ID.SYSTEM_STATE` was 11, not 15 | The `AircraftLoaded` request never fired, so no aircraft profile was ever matched | `SimConnectManager.cs` |
| `SimConnect_SetDataOnSimObject` had a spurious `uOffset` parameter | The pointer landed in `cbUnitSize` and nothing was ever written | `SimConnectManager.cs` |
| `SIMCONNECT_RECV_SYSTEM_STATE.dwFloat` was `double` and `szString` was a pointer | Aircraft title came back garbage | `SimConnectManager.cs` |
| `DatumID` passed as `0` to `SimConnect_AddToDataDefinition` | `SIMCONNECT_UNUSED` is `DWORD_MAX`, so `0` claims client datum ID 0: the first datum of every block registered and each later one raised `DUPLICATE_ID`, so **each block silently held one variable** — the sim answered with a storm of exceptions and every state field but the first read zero | `SimConnectManager.cs` |
| String datums declared with the unit `"string"` | A string variable takes no unit — `UnitsName` must be empty (or NULL) — so all nine `ATCTEXT` datums raised `NAME_UNRECOGNIZED` and the definition could not be requested at all (`UNRECOGNIZED_ID`), leaving the ATC text block blank | `SimConnectManager.cs`, `AircraftController.cs` |
| `double.Parse` without a culture | **On any locale where `.` is a group separator (es-ES, de-DE) `"22.44"` parsed as 2244 and `"34000.000"` as 34000000** — all `.pln` and config geometry was wrong | all parsing paths, now via `Core/Parsing.cs` |
| `DepartureICAO` overwritten with the airport *name* | Airport lookups by ICAO failed | `ConfigParser.cs` |
| `.pln` `DeparturePosition` never normalised (`"6"` vs `"06"`) | Runway lookups against facility data failed | `ConfigParser.cs` |
| `NormalizeRunwayName` ignored its `designator` argument | `RunwayDesignatorFP` was dropped, so `25` never became `25R` | `ConfigParser.cs` |
| `<CruisingAlt>` not read | Cruise altitude was always guessed from waypoint heights | `ConfigParser.cs` |
| `<DepartureLLA>` / `<DestinationLLA>` not read | Destination elevation and plan geometry unavailable | `ConfigParser.cs` |
| MSFS 2024 plans carry no waypoint positions | Endpoints were recorded at 0,0 and corrupted the route | `ConfigParser.cs` |
| MSFS 2024 arrival procedure is `DestinationDetails`, not only `ArrivalDetails` | STAR name was never picked up | `ConfigParser.cs` |
| Initial climb issued native autopilot commands *and* direct surface control | The two fought over pitch and bank | `AIPilot.cs` |
| Single `_runwayHeading` field for departure and arrival | Arrival data clobbered the departure heading mid-flight | `AIPilot.cs` |
| `StartAsync` could run twice (console `start` command) | Second SimConnect handle, listener and pipe all collided | `AIPilot.cs` |
| Aircraft state read from the HTTP bridge while the control loop mutated it | Readers could observe a half-updated state | `AircraftController.cs` |
| Rotation speed fell back to `IAS + 10` | A slow aircraft could be rotated below stall | `AIPilot.cs` |
| Only engine 1 was read | Twin and four-engine aircraft repeated engine 1's values on every slot | `AircraftController.cs` |
| `MaxWeightLbs` used `uint.MaxValue` as "unlimited" | An unlimited rating was still treated as a hard limit | `TaxiRouter.cs` |
| `AutopilotStatus.VNAV` read `AUTOPILOT WING LEVELER` | Misnamed state | `AircraftController.cs` |
| Panel JSON names not aligned with the JavaScript | The bridge serialised PascalCase while the panel reads camelCase (`aircraft.altitudeAGL`), so every telemetry field rendered `undefined` | `PanelHttpBridge.cs` |
| `new-push-button` label set with `textContent` | The framework renders the caption from `title`, so the delegation button never changed from its default | `AIPilotPanel.js` |
| `new-push-button class="large"` | Not a valid modifier; the accepted set is `small`, `verysmall`, `big`, `Square`, `SmallSquare` | `AIPilotPanel.html` |
| `build.bat` could not report a failing environment | `fspackagetool.exe` launches the packager from inside the installed game, so with MSFS absent the script blocked with no message instead of failing fast | `build.bat` |
| `MASTER_BATTERY_ON` and `MASTER_BATTERY_OFF` were both registered as payload events | Both fired every checkpoint cycle, so the battery was switched off the instant it was switched on: `BATTERY SWITCH ON` could never verify, the gate checklist stalled on it for its whole retry cap, and the battery was left off for everything that followed | `ChecklistTextResolver.cs` |
| Every phase executor called `Status(...)` once per frame | The 20 Hz loop printed one identical line per frame ("Running gate checklist...", "Waiting for IFR clearance..."), so the console carried no checklist progress at all; phase status is now emitted on change plus a heartbeat | `AIPilot.cs` |
| `ChecklistStatus.Index` counted headers | "i/n" in the status and panel could exceed the item count of a step that carries headers | `ChecklistManager.cs` |
| Beacon checkpoint verified `LIGHT BEAC` | Not a SimVar: the sim answered the datum with NAME_UNRECOGNIZED and the request with ERROR, so the taxi-out step spent its whole 150 s data timeout on one checkpoint and the runway never came | `ChecklistTextResolver.cs` |
| Spoiler checkpoints verified `SPOILERS HANDLE PERCENT` | Not a SimVar either (`SPOILERS HANDLE POSITION`, in percent, is the one that exists), so both landing spoiler items could never confirm | `ChecklistTextResolver.cs` |
| `FLAPS_0` fired to retract the flaps | Not a key event - the SDK publishes FLAPS_1..4 and FLAPS_UP - so the rollout did nothing and the sim logged NAME_UNRECOGNIZED on every landing | `AIPilot.cs` |
| Generic FLAPS-up item verified the handle index as an on state | `Verify(index, expectedOn)` asks ">= 0.5", but the index reads 0 when up, so the checkpoint fired FLAPS_UP for its whole retry cap (about 24 s) inside the taxi-out step, which the console never reports | `ChecklistTextResolver.cs` |
| Turbine aircraft ran the piston MIXTURE checkpoint | With no aircraft config parsed (Store/Community aircraft), `ChecklistEnvironment.Turbine` came only from the config's engine type, so a jet or turboprop got a mixture-lever checkpoint it can never satisfy - the observed "state never confirmed (retry cap)" on the log's engine 6/9; the runtime N1/N2 feed is now the fallback signal | `AIPilot.cs`, `ChecklistTextResolver.cs` |

The following were found in a later review and are now fixed:

| Bug | Effect | Where |
|---|---|---|
| The hold-short was never actually reached | `_atRunwayHoldShort` was set only by a parsed "line up and wait" - a phrase the sim's own ATC never publishes as a clearance var - so the takeoff request was never sent and, once the taxi route ran out of legs, the pilot fell into the no-target branch, steered the runway heading at throttle 0.25 and **rolled onto the runway without clearance**. The hold-short is now a position-derived stage with a stop point and its own line-up stage | `AIPilot.cs` |
| Hold-short selection used "nearest to the runway centre" | At an airport with parallel strips the nearest hold-short can belong to the *other* runway, because a taxi point payload carries no runway of its own. The runway is now recovered from the runway-type paths attached to the node, and the hold-short is chosen by shortest reachable route | `TaxiRouter.cs` |
| No traffic awareness at all | The sim publishes no traffic list and none was requested, so the pilot was blind to aircraft on the runway or on final. Traffic is now read with `SimConnect_RequestDataOnSimObjectType` and gates runway entry | `TrafficMonitor.cs`, `SimConnectManager.cs` |
| Speed PID derivative term was dead code | `speedLastError` was a *local* re-zeroed every call, so the derivative was `kd * error / dt` regardless of history: at the 20 Hz loop rate this saturated the throttle between idle and full on any error of a few knots, which is what a go-around at 180 kt and a landing at 60 kt ran into | `HybridAutopilot.cs` |
| Altitude-hold PID ran with `dt ≈ 0` | `_lastUpdate` was assigned at the top of the method and read at the bottom of the same call, so the interval was microseconds: the integral never grew and the derivative was a single clamped spike | `HybridAutopilot.cs` |
| Engagement reset was incomplete | Nine clearance and state fields (`_takeoffCleared`, `_departureClearanceReceived`, `_startupClearanceSent`, `_goAround`, `_atRunwayHoldShort`, …) survived `engage`, so re-engaging after an aircraft or airport change skipped the clearance request and rolled on its own | `AIPilot.cs` |
| `PARKING_BRAKES` fired as a "set" command | It is a *toggle* event, so a "hold position" at a gate with the brake already set released the very brake the instruction had just asked for. The brake is now set by state via `PARKING_BRAKE_SET`, with `BRAKE PARKING POSITION` as the feedback | `AIPilot.cs`, `AircraftController.cs` |
| Wingspan constraint never applied | `MinWidthMeters` defaults to 0 and `PlanTaxiToRunway` never set it, and the span itself is in feet where the widths are in metres | `AIPilot.cs`, `TaxiRouter.cs` |
| `TaxiConstraints.HoldShortBefore` / `CrossableRunways` were dead | Two members nothing read or wrote. `HoldShortBefore` now blocks crossing the runway the route holds short of, `CrossableRunwalks` is the exception, and both are exercised | `TaxiRouter.cs` |
| `TaxiRoute.Path` returned a list of null-island points | Every leg was `(0, 0)`, so any consumer got a meaningless route; nothing consumed it either | `TaxiRouter.cs` |
| Taxi-in gave up silently | The no-route branch set `_atGate` with no brake and no parking brake, so the aircraft coasted to a halt mid-taxiway with engines running and was handed to shutdown | `AIPilot.cs` |
| Airport frame calibrated exactly once, at load time | The arrival airport's frame was chosen while the aircraft was still on the departure airport, where every candidate orientation is equally wrong. The frame is now re-calibrated when the graph is close to the aircraft, and `airport reload <ICAO>` rebuilds on demand | `AIPilot.cs` |
| `ATC RUNWAY DISTANCE` decoded and never read | It is the one simulator-owned signal for "how far from the runway", and it is now the no-graph hold-short and the phase-selection distance | `AIPilot.cs` |
| `PathResolutionRatio` denominator included closed paths | Closed paths are skipped in the numerator but counted in the denominator, understating the ratio on airports that publish a lot of closed taxiway | `AirportFacilitiesLoader.cs` |
| A parking record could overwrite a hold-short node | Taxi points and parking share an index namespace, so a hold-short whose index collided with a parking spot silently lost its classification and left a hole in the graph | `AirportFacilitiesLoader.cs` |
| Configuration events re-fired every frame | `GEAR_DOWN`, `FLAPS_2`, `FLAPS_3`, `AXIS_LEFT_BRAKE_SET`, `SPOILERS_ON` and `GEAR_UP` all fired on every frame of the phase they belonged to, filling the log with identical entries. Guarded on state now | `AIPilot.cs` |
| Panel reported `connected` from the control mode | `ControlState != Idle` told the panel it was connected after the simulator had dropped, while the named-pipe path tested `IsConnected` and disagreed | `PanelHttpBridge.cs` |
| Aircraft directory fallback picked an arbitrary aircraft | `candidates[0]` from a full Community walk could be an unrelated model, and a wrong-but-nonzero performance value wins in `MergePerformance` and then every V-speed is wrong | `AIPilot.cs` |
| Dead code with misleading names | `Class1.cs` (empty), `AirportData.MetersPerDegreeLat/Lon` (superseded by `Geo`), `ControlTargets.TargetBank` (never read), and a duplicate unused `RequestDataOnSimObjectType` overload | several |
| Facility, flight-plan and system-state sends were outside `SendNamed` | Their exceptions reached the console as `NAME_UNRECOGNIZED (sendId=249, index=2)` with no indication of which call was rejected, and batched sends made the remembered packet ID lag the one the server blames; every send is now named and the report falls back to the closest recorded packet | `SimConnectManager.cs` |

The following were found in a review of how the pilot actually reacts to ATC, and are now fixed. The
pattern is worth naming: the parser read a clearance, the handler stored it, and nothing then read it
- so eight of the fields an ATC can fill in had no effect on the flight at all.

| Bug | Effect | Where |
|---|---|---|
| ATC-assigned heading and speed were write-only | `ControlTargets _targets` was assigned by `HandleAtcInstruction` and read by nothing. Every executor built a fresh `ControlTargets` literal or used the native AP with a hardcoded `SetAirspeed(280)`, so "turn left heading 270" and "reduce speed to 180" were logged and dropped | `AIPilot.cs` |
| The controller's own named fix was read and never used | `ATC NEXT WAYPOINT NAME` and `ATC NEXT WAYPOINT ALTITUDE` were decoded into `ATCState` and nothing read them, so a controller that had sent the aircraft to a fix was silently ignored. A direct-to from either the parser or the SimVar is now resolved from the navigation database and flown, and a crossing altitude becomes the level the aircraft is flown at | `AIPilot.cs` |
| The leg parser read COURSE before THETA and RHO | `THETA` and `RHO` sit between `ORIGIN_ALTITUDE` and `COURSE` in the published leg payload. Declaring them and not reading them left the parser eight bytes short for the rest of every leg, so the course, the altitude restrictions and the fix flags of every leg were read from the wrong offsets - silent garbage rather than an error | `NavData.cs` |
| The ATC's runway never reached routing | `PlanTaxiToRunway`, `ApplyRunwayHeadings` and `ResolveArrivalRunway` read the *flight plan's* runway, while `NativeAtcMonitor` was already diffing the sim ATC's designated runway into an instruction that carried the run's assigned strip. The pilot taxied to and departed from a strip the controller had not assigned. The ATC's runway now wins on the ground and in the air, and re-plans the route while the aircraft is still taxiing | `AIPilot.cs` |
| A step-down below cruise was ignored | `_targetCruiseAltitude` was the only altitude field, so "descend and maintain 12000" overwrote the cruise level and the descent then aimed at `Math.Max(10000, elevation + 3000)`. Assigned and descent altitudes are now separate, and the descent holds the clearance it was given | `AIPilot.cs` |
| Top of descent never fired on its own | `_distanceToDestination` was the plan's total length, computed once at load and never reduced as the aircraft flew, so the 3:1 rule compared a constant against a shrinking threshold: it fired on the first frame of cruise on a short flight and never on a long one. The distance is now recomputed each frame from the aircraft's own position | `AIPilot.cs` |
| `ClearedToLand` had no branch at all | The field was parsed and then never read anywhere, so the pilot flew straight through a landing clearance it had been given. Without one, the final approach now goes around below 500 ft AGL at a towered arrival, and after two unanswered go-arounds it lands rather than orbit forever | `AIPilot.cs`, `AtcCommunicationHub.cs` |
| `ClearedForApproach`, `HoldShort`, `CrossRunway`, `ResumeOwnNavigation`, `Mach`, `TaxiRoute`/`TaxiWaypoints` | All parsed, none handled. "Resume own navigation" in particular is what releases a vector, so it now clears every assignment the controller made and re-engages LNAV; `HoldShort` feeds `TaxiConstraints.HoldShortBefore`; `ClearedToCross` feeds `CrossableRunways`; a named taxi route is now followed via `TaxiRouter.FindRouteViaChain` rather than replaced by the shortest route | `AIPilot.cs`, `TaxiRouter.cs` |
| Text-parsed altitude was a chain of missed spellings | "DESCEND AND MAINTAIN FLIGHT LEVEL 100" matched nothing (the `FL` matched the `FL` of "FLIGHT", leaving "IGHT LEVEL 100"); "DESCEND TO 3000 FEET" fell through because the keyword pattern requires the digits immediately after the verb; the `N FEET` fallback was guarded on `AltitudeFt == 0`, which an `int?` is false when null, so it never ran; "MAINTAIN 250 KNOTS" was read as an altitude of 250 ft; "MAINTAIN 3,000" was read as nothing because `\d{2,5}` stops at the comma; and "CLIMB TO FL350" only came out right because a *second* regex happened to overwrite the 350 the first one returned | `BeyondAtcBridge.cs` |
| Handoff frequency was only readable from a literal `@` | `Regex.IsMatch(t, @"@\S+|[\d.]+")` matched any digit or even a sentence-ending period, so "CONTACT TOWER 118.700" reported a handoff with an empty frequency and the radio never moved. Any MHz value near a contact verb is now read, and a handoff with no frequency is not a handoff | `BeyondAtcBridge.cs` |
| "MACH 78" was read as a speed of 78 knots | The Mach pattern required a decimal point, and the speed pattern accepted the bare digits. The spoken shorthand is now read as 0.78 | `BeyondAtcBridge.cs` |
| The off-screen menu reader could never work | `GetDC0` / `ReleaseDC0` / `GetDc0` / `ReleaseDc0` are not Win32 exports - the API is `GetDC(HWND)` / `ReleaseDC(HWND, HDC)` - so every capture threw `EntryPointNotFoundException` into the "nothing recognised" fallback and `atc ocr on` was a permanent no-op. The declaration of a name that does not exist is why this survived review: it reads like a P/Invoke that works | `AtcScreenCapture.cs`, `AtcMenuScreenReader.cs` |
| Menu line segmentation closed a band on the first blank row | `if (start < 0 || ++gap > LineGap) continue;` continues in the branch that was supposed to close the band, so the band was added after one blank row and `LineGap` was unreachable. A line whose ink has a gap in it (a hyphen, an apostrophe, the crossbar of a letter) split into two and shifted the line number of every entry below it, i.e. the aircraft pressed the wrong `ATC_MENU_n`. Bands past the tenth were also numbered 0 again, pressing the tenth entry twice; they now select no entry at all | `AtcMenuScreenReader.cs` |
| BeyondATC log tail tracked a byte offset in characters | `_offset += chunk.Length` advanced by the reader's character count while `Seek` reads bytes, so any non-ASCII byte in the log desynchronised the two and the region after it was read again and emitted twice | `BeyondAtcLink.cs` |
| A runway assignment could be dropped for good | `AddAssignment` rate-limited by key against the SimVar's own timestamp, while `_previous` was updated whether or not the instruction was emitted. A suppressed assignment was therefore never re-emitted, so a runway the controller had just assigned was never reported. One change is now always emitted; the window only suppresses a repeat of a value that has already been reported | `NativeAtc.cs` |
| `HandleAtcInstruction` was reachable from non-loop threads | `atc process <text>` reaches it from the console, the HTTP bridge and the named pipe, mutating the phase manager, the clearance flags and the brakes while the 20 Hz loop is mid-update. Instructions from off the loop are now queued and applied at the top of the next frame | `AIPilot.cs` |
| A native request could hang if the source flipped | `_requests.Tick()` only ran in the `Source == Native` branch, so a request in flight when BeyondATC started running was never advanced or failed and the engine stayed busy for the session, blocking every later request. The probe now ticks whichever controller is active | `AtcCommunicationHub.cs` |
| The pilot never waited for a landing clearance | `UpdatePhaseRequests` cooldown-gated when it *sent* the request, and nothing recorded that one had been asked for at all, so there was no way to distinguish "not asked yet" from "asked and unanswered" at the decision height | `AtcCommunicationHub.cs` |
| `FlightPhaseDefinition` was registered and never read | `MinAltitude`, `MaxAltitude`, `MinAltitudeAGL`, `MaxAltitudeAGL`, `MaxSpeed` and `Description` were all dead, while `PhaseFitsState` restated the ground/airborne split in a switch that could drift from the table. The definitions are now what `PhaseFitsState` reads, the unused members are gone, and `MaxAltitudeAGL` is what releases a forced ground phase | `FlightPhaseManager.cs` |
| A fix the plan names was either null island or missing | `FlightPlanWaypoint.HasPosition` tests for `NaN`, not zero, so a waypoint with a position of `0,0` looks placed; and a plan whose fix names the database cannot resolve was rebuilt with the name dropped and no record that it had been. The route builder resolves every named fix against the navdata, reports the ones that do not resolve, and issues the lookups that fill them in as the answers arrive | `NavPathBuilder.cs` |
| `AtcRequestKind.Acknowledge` ("readback") could never work | It had no case in the engine's `Accepted()`, `IsRequestable` returned false for it, and the sim's own ATC publishes no readback event and cannot be told a pilot has acknowledged anything - so `atc request readback` opened the ATC menu and pressed a line that never matched. The kind is removed from the enum, the parser and the usage text | `NativeAtc.cs`, `AIPilot.cs` |
| `BeyondAtcBridge` was a stub | `RaiseInstructionParsed`, `RaiseRequestSent`, the CPDLC session members and `OnFrequencyChange` had no callers or subscribers, and `Tick()` detected frequency changes nobody listened to, at 2 Hz from the control loop. The class is now the shared parser plus the enable/disable switch the hub's BeyondATC side uses | `BeyondAtcBridge.cs` |
| The CPDLC "server stopped" branch was unreachable | It sat inside a test for "found an endpoint", which only ever matched the start line, so the stop was silently swallowed. The stop now clears the endpoints and is tested | `AtcCommunicationHub.cs` |
| Dead code with misleading names | `BeyondAtcBridge`'s stub events and CPDLC sessions, `SimConnectManager.SetDataOnSimObject` (unused, and its hardcoded `cbUnitSize=1, count=8` only works for a single-datum double definition), `SimConnectManager.FireEventOnce(string,string,uint)` (unused) | several |

The following were found from one MSFS 2024 session's console: a wall of
`SimConnect exception: DATA_ERROR (sendId=…, index=2) [add facility field 'X' to definition 81NN]`
at startup, and then a gate checklist stuck re-firing item 5/9 for its whole retry cap.

| Bug | Effect | Where |
|---|---|---|
| Every approach and fix facility definition was refused field by field | `SimConnect_AddToFacilityDefinition` requires the definition to open with one of the four documented entry points (`AIRPORT`, `WAYPOINT`, `NDB`, `VOR`), every member to nest inside a container, a child to open before its parent closes, and one definition to carry one entry point. The APPROACH header definition declared no entry point at all, so its very first field (`TYPE`) was rejected and every later field of the definition failed with it; the transition, final and missed leg definitions opened `OPEN APPROACH_TRANSITION` / `OPEN FINAL_APPROACH_LEG` / `OPEN MISSED_APPROACH_LEG` *after* `CLOSE APPROACH` - all three are children of `APPROACH`; and the fix definition opened `NDB` and `VOR` as siblings of a closed `WAYPOINT`. After the first rejection the whole definition is refused, so no approach, transition, leg or fix ever arrived - and the requests still "completed" with nothing, which is why the route silently held a hole instead of erroring. The definitions are now pure (`NavDataLoader.FacilityDefinitions`) and their structure is pinned by `NavFacilityDefinitionTests` without a simulator | `NavDataLoader.cs` |
| A fix could be recorded unknown while two of its three lookups were still in flight | Splitting the fix lookup into one definition per entry point means one fix is asked of three requests. The first `FACILITY_DATA_END` used to mark it unknown on its own, so a waypoint request's "not found" discarded an NDB whose answer was on its way. The failure is now recorded only when every request for that fix has ended, and the concurrency cap counts distinct fixes rather than in-flight requests | `NavDataLoader.cs` |
| The gate checklist stalled on "FUEL PUMP - ON" | MSFS 2024's fuel pump lives in the modern `[FUEL_SYSTEM]` `Pump.N` block: commanded with `FUELSYSTEM_PUMP_ON`/`OFF` (payload = pump index), verified with `FUELSYSTEM PUMP SWITCH:index`. The binding fired only `ELECT_FUEL_PUMP1_SET`, the legacy `[FUEL]` key, against a switch that never moved, so the checkpoint fired for its whole retry cap (~24 s) and the item still failed. Both homes are now commanded, and a one-shot preset fires with the argument its event wants rather than the switch state - a `FUELSYSTEM_PUMP_OFF` carrying 0 addressed pump 0, which is none of them | `ChecklistTextResolver.cs`, pinned by `ChecklistGateFlowTests` |


