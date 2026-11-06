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
| Aircraft link | `Core/Aircraft/SimAircraft.cs` | Owns the connection; registers one data definition per block (POSITION, ATTITUDE, SPEEDS, ENGINES, ENGINE_LIMITS, FUEL, AUTOPILOT, ENVIRONMENT, NAVIGATION, NAVRADIO, SYSTEMS, APMODES, COM, ATC, WEATHER, PUSHBACK, ATC text, aircraft title) and decodes them into `AircraftState` |
| MSFS paths | `Core/Paths/SimPaths.cs` | `UserCfg.opt` parsing, Store package roots, community/official package folders, VFS projection |
| Aircraft package | `Core/Aircraft/AircraftPackageLocator.cs` | Locates the loaded aircraft's package directory from its title |
| Performance | `Core/Aircraft/Performance.cs` | `AircraftPerformance` + `AircraftConfigParser` for `aircraft.cfg` / `flight_model.cfg` / `engines.cfg` / `flight_performance.cfg` |
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
| Continuous planner | `Planning/ContinuousFlightPlanner.cs` | Random suitable destination inside aircraft range via the facility list |
| Native ATC | `Core/Atc/NativeAtc.cs` | `AtcRequestEngine` (menu probing + SimVar grant detection), `AtcRadio` (COM tuning, squawk, handoffs) |
| ATC screen capture | `Core/Atc/AtcScreenCapture.cs`, `Core/Atc/AtcMenuScreenReader.cs` | GDI capture of the simulator window + template-matched menu-line recognition; recognised lines are tried before learned/sequential probing |
| Traffic monitor | `Core/Traffic/TrafficMonitor.cs` | `SimConnect_RequestDataOnSimObjectType` aircraft feed, stale-contact expiry, runway occupancy / on-final / airborne-conflict assessment; gates hold-short entry and triggers go-around |
| Checklists | `Core/Checklists/Checklists.cs` | MSFS `SimBase.Document` checklist XML + generic fallbacks + event/simvar bindings |
| Orchestrator | `Core/Pilot/PilotEngine.cs` | Connection loop, phase state machine, phase executors, turnaround/continuous logic |
| GUI | `src/MSFS2024AIPilot.Gui` | Connect, mode select (Off / Single / Continuous), service time, engage/pause/disengage, telemetry, checklist, log |

## Control strategy

Hybrid, matching the previous design:

- **Ground & precision phases** (taxi, takeoff roll, final approach, rollout): direct
  surface control via PID loops on `AXIS_ELEVATOR_SET`, `AXIS_AILERONS_SET`,
  `AXIS_RUDDER_SET`, `THROTTLE_SET` (13-bit centred at ~8192).
- **Airborne enroute** (climb, cruise, descent): MSFS native autopilot
  (`AP_PANEL_HEADING_HOLD`, `AP_ALT_VAR_SET_ENGLISH`, `AP_SPD_VAR_SET`,
  `AP_NAV1_HOLD`, `AP_APR_HOLD`, `AUTO_THROTTLE_ARM`). Native-mode commands are
  deduplicated — they are toggle events and must not be re-sent every frame.

## Flight phases

`Unknown → Preflight → Startup → TaxiOut (pushback → taxi route → hold-short →
line-up) → Takeoff → InitialClimb → Climb → Cruise → Descent → Approach →
FinalApproach → (GoAround) → Rollout → TaxiIn → Shutdown`, then — in continuous
mode — a service turnaround and the next leg.

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
