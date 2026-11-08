# AICopilotFS — MSFS 2024 AI Pilot

Fully automated gate-to-gate AI pilot for Microsoft Flight Simulator 2024, with an
optional **continuous flights** mode (depart a suitable airport, land at a random
suitable airport in range, service the aircraft, repeat until stopped). Desktop
WPF GUI, out-of-process, native SimConnect only — no in-sim panel, no BeyondATC.

## Architecture

```
MSFS2024AIPilot.sln
├── src/MSFS2024AIPilot.Core/   Core library (SimConnect, state, phases, planning)
├── src/MSFS2024AIPilot.Gui/    WPF desktop application (AICopilotFS.exe)
└── tests/MSFS2024AIPilot.Tests/  xUnit tests for the pure-logic modules
```

### Why an out-of-process app

Per the MSFS 2024 SimConnect SDK: out-of-process applications are more stable —
if they crash they typically will not crash the simulator — and are easier to
debug. The GUI is a normal WPF app referencing the Core library.

## Key components

| Component | File | Purpose |
|---|---|---|
| SimConnect client | `Core/SimConnect/SimConnect.cs` | Native P/Invoke wrapper over `SimConnect.dll`: data definitions, events, SimObject/string/facility data, airport lists, flight-plan load |
| Aircraft link | `Core/Aircraft/SimAircraft.cs` | Owns the connection; registers one data definition per block (POSITION, ATTITUDE, SPEEDS, ENGINES, ENGINE_LIMITS, FUEL, AUTOPILOT, ENVIRONMENT, NAVIGATION, NAVRADIO, SYSTEMS, APMODES, COM, ATC, WEATHER, PUSHBACK, ATC text, aircraft title) and decodes them into `AircraftState`. Runs a dedicated pump thread that blocks on the SimConnect message event (`hEventHandle` passed to `SimConnect_Open`) and drains `SimConnect_CallDispatch` — `CallDispatch` blocks when the sim has no messages, so it is only ever called while the event is signaled |
| MSFS paths | `Core/Paths/SimPaths.cs` | `UserCfg.opt` parsing, Store package roots, community/official package folders, VFS projection |
| Aircraft package | `Core/Aircraft/AircraftPackageLocator.cs` | Locates the loaded aircraft's package directory from its title |
| Performance | `Core/Aircraft/Performance.cs` | `AircraftPerformance` model, `PerformanceCalculator` (n-D table interpolation, V-speeds), `AircraftConfigParser` for `aircraft.cfg` / `flight_model.cfg` / `engines.cfg` / `flight_performance.cfg` (incl. `::`-separated multidimensional tables) |
| Airport data | `Core/Airports/AirportData.cs` | Runways, ground-graph nodes/edges, parking, bias frames |
| Facilities API | `Core/Airports/FacilityLoader.cs` | `SimConnect_AddToFacilityDefinition` / `SimConnect_RequestFacilityData`; assembles `AirportData` including runways, taxi points, taxi paths, parking |
| Airport list | `SimConnect.cs` + `ContinuousFlightPlanner` | `SimConnect_RequestFacilitiesList` for random destination selection |
| Taxi routing | `Core/Airports/TaxiRouter.cs` | A* over the ground graph with runway/weight/width/hold-short constraints |
| Geo math | `Core/Geo/GeoMath.cs` | Great-circle, bearings, cross-track, segment projection |
| Weather | `Core/Environment/Weather.cs` | Wind components/correction, turbulence classification + `TurbulenceTracker`, icing, surface factors |
| Envelope | `Core/Environment/Envelope.cs` | Speed/load-factor/VS/engine limits, `EnvelopeGuard` |
| Autopilot | `Core/Guidance/Autopilot.cs` | `HybridAutopilot` (native AP events + PID direct surface control) + `PidLoop` |
| Nav following | `Core/Guidance/NavFollower.cs` | Waypoint sequencing, cross-track, wind-corrected heading |
| Pushback | `Core/Guidance/PushbackPlanner.cs` | `PushbackDecision` over `PushbackProgress` (ask/wait/tow/dismiss/give-up) |
| Back-taxi | `Core/Guidance/BacktrackPlanner.cs` | Geometric runway-remaining vs required-roll decision |
| Flight plan | `Core/State/FlightPlan.cs`, `Planning/PlnFile.cs` | `FlightPlanWaypoint` model; AceXML `.pln` read/write |
| Route builder | `Planning/RouteBuilder.cs` | Departure → enroute → final-approach-fix → threshold route generation |
| Continuous planner | `Planning/ContinuousFlightPlanner.cs` | Random suitable destination inside aircraft range via the facility list; kind-aware (rotorcraft need no runway, floatplanes need water runways) |
| Aircraft class | `Aircraft/AircraftKind.cs` | `AircraftKind` (Airplane/Rotorcraft/Seaplane/Amphibian) detection from package cfgs/title/performance + runtime `IS GEAR FLOATS` upgrade |
| Terrain | `Terrain/TerrainElevations.cs`, `Terrain/TerrainRoute.cs` | Open-Meteo elevation API + disk cache; route terrain profile (1000 ft margin, 2000 ft mountainous, airport+3000 ft fallback), in-flight look-ahead, racetrack helper |
| Native ATC | `Core/Atc/NativeAtc.cs` | `AtcRequestEngine` (menu probing + SimVar grant detection), `AtcRadio` (COM tuning, squawk, handoffs) |
| ATC screen capture | `Core/Atc/AtcScreenCapture.cs`, `Core/Atc/AtcMenuScreenReader.cs` | GDI capture of the simulator window + template-matched menu-line recognition; recognised lines are tried before learned/sequential probing |
| Traffic monitor | `Core/Traffic/TrafficMonitor.cs` | `SimConnect_RequestDataOnSimObjectType` aircraft feed, stale-contact expiry, runway occupancy / on-final / airborne-conflict assessment; gates hold-short entry and triggers go-around |
| Checklists | `Core/Checklists/` | MSFS 2024 `SimBase.Document Type="CheckpointLibrary"/"Checklist"` XML parser (steps/pages/blocks/checkpoints, `IncludeCheckpointLibrary`/`IncludeChecklist`, `.loc` localization), legacy text format, condition/expression engine, copilot `Action` execution via `ChecklistSimHost`, text→event/SimVar resolver, procedural `GenericChecklists` fallback |
| Orchestrator | `Core/Pilot/PilotEngine.cs` | Connection loop, phase state machine, phase executors, turnaround/continuous logic, operational logging |
| Flight logging | `PilotEngine.Log` | GUI log + daily file `%LocalAppData%/AICopilotFS/pilot-yyyyMMdd.log` (rotated at ~60k lines); diagnostics ~5 s, waypoint captures, phase/traffic/terrain events — recurring telemetry is rate-limited |
| GUI | `src/MSFS2024AIPilot.Gui` | Connect, mode select (Off / Single / Continuous), service time, engage/pause/disengage, telemetry, checklist, log |

## Control strategy

Hybrid, matching the previous design:

- **Ground & precision phases** (taxi, takeoff roll, final approach, rollout): direct
  surface control via PID loops on `AXIS_ELEVATOR_SET`, `AXIS_AILERONS_SET`,
  `AXIS_RUDDER_SET`, `THROTTLE_SET` (13-bit centred at ~8192). Axis events are
  deadbanded (~0.4% full-scale) so steady flight does not spam SimConnect; the
  cache is re-synced by `Neutralize()`/mode switches so suppressed commands can
  never leave stale surface state.
- **Airborne enroute** (climb, cruise, descent): MSFS native autopilot
  (`AP_PANEL_HEADING_HOLD`, `AP_ALT_VAR_SET_ENGLISH`, `AP_SPD_VAR_SET`,
  `AP_NAV1_HOLD`, `AP_APR_HOLD`, `AUTO_THROTTLE_ARM`). Native-mode commands are
  deduplicated — they are toggle events and must not be re-sent every frame.

## Flight phases

`Unknown → Preflight → Startup → TaxiOut (pushback → taxi route → hold-short →
line-up) → Takeoff → InitialClimb → Climb → Cruise → Descent → Approach →
FinalApproach → (GoAround) → Rollout → TaxiIn → Shutdown`, then — in continuous
mode — a service turnaround and the next leg.

### Terrain avoidance

After each route is built, `AnalyzeTerrainAsync` samples terrain elevation
(Open-Meteo API, disk-cached in `%LocalAppData%/AICopilotFS/terrain-cache.json`)
along the route and stores a `TerrainProfile`. Required altitude = terrain +
1000 ft, or +2000 ft over terrain above ~4500 ft MSL. The cruise altitude and
enroute waypoint altitudes are raised to the profile floor; in flight,
`ApplyTerrainClearance` raises the target when terrain within the next
~10–15 NM demands more. When the climb can't reach terrain ahead (valley
departure) or terrain blocks the descent near a mountain airport, a 2 NM-leg
racetrack holds the aircraft high (10-minute cap) only while the path is
blocked — it releases to nav guidance as soon as the look-ahead window is
terrain-clear. Offline fallback: highest
airport elevation + 3000 ft everywhere — logged, never silent.

### Rotorcraft

No runway/pushback phases: hover-taxi at ~30 ft AGL / ~25 kt toward a
departure point ~1 NM out along the route bearing (or lift off in place
when already clear), then climb to 500 ft AGL transit for <15 NM legs
(else 1000 ft) → low enroute (~90 kt max, 25 kt when terrain demands a
climb) → ~300 ft approach at ~30 kt → vertical set-down at the airport
reference → Rollout → turnaround/Shutdown. Routes are
airport-reference to airport-reference (`RouteBuilder.BuildRotorcraftRoute`).

### Seaplanes / amphibians

Water runways are identified by the official facility `SURFACE` enum
(2, 26–31) or the `WATER` designator. Floatplanes only receive water runways;
amphibians prefer them but may use land runways. Water departures skip
pushback/taxi (no ground graph) and line up on the water; amphibians command
gear up for water ops and down for land ops (`ArrivalNeedsGear`). Gear is
never extended on a water arrival.

## Continuous flights

Enable "Continuous" in the GUI (or `PilotMode.Continuous`). After each landing
the engine parks, waits the configured service time, asks
`ContinuousFlightPlanner` for a random airport inside the aircraft's estimated
range with a runway long enough, builds a `RouteBuilder` route, writes a `.pln`
and loads it via `SimConnect_FlightPlanLoad`, then repeats until disengaged.

## MSFS 2024 notes

- No breaking SimConnect change vs MSFS 2020; the bundled `SimConnect SDK`
  header in this repo defines the wire layouts used by `SimConnect.cs`.
- Data definitions append with `SIMCONNECT_UNUSED` (0xFFFFFFFF); string datums
  pass an empty units name; SimVars are 1-based indexed.
- Facility payloads are order-sensitive and nest (`OPEN AIRPORT` → child records
  → matching close); `FacilityLoader` decodes them into `AirportData`.
- ATC clearances are read from `ATC …` SimVars; requests go through
  `ATC_MENU_OPEN` + `ATC_MENU_0..9` probing with per-circumstance memory. The
  open menu is also captured off the screen (`AtcScreenCapture` +
  `AtcMenuScreenReader`) so recognised request lines are pressed first; the
  tenth displayed line maps to `ATC_MENU_0`.
- `SimConnect_FlightPlanLoad` loads generated `.pln` files for the sim's GPS.

## Build & test

```
dotnet build MSFS2024AIPilot.sln
dotnet test  MSFS2024AIPilot.sln
```

Requires .NET 8 SDK. `SimConnect.dll` is copied from `SimConnect SDK/lib` (or
the installed MSFS 2024 package) to the output directory.
