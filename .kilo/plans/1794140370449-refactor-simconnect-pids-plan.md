# Refactor Plan: SimConnect correctness, live issue fixes, per-aircraft PIDs

## Objective
Refactor AICopilotFS (MSFS 2024) for robust SimConnect usage, fix runtime issues observed in logs, and replace one-size-fits-all PID gains with aircraft/rotorcraft-tailored control loops. Implementation is to be performed by an implementation-capable agent; this document is the plan only.

## Research findings (SimConnect)
- Out-of-process only; SimConnect is not thread-safe. All calls must be serialized (existing `_lock`/`_nativeLock` pattern is correct; do not call from multiple threads).
- Data definitions max 1000 entries; string/struct datums use `SIMCONNECT_UNUSED` (0xFFFFFFFF) and empty units. Already followed; audit for any definition exceeding 1000 or missing `SIMCONNECT_UNUSED` for string datums.
- Facility payloads are order-sensitive and nested OPEN/CLOSE; `FacilityLoader` must keep pairing. Audit CLOSE counts vs OPEN counts.
- Events: `AXIS_ELEVATOR_SET`/`AXIS_AILERONS_SET`/`AXIS_RUDDER_SET` range -16383..+16383 normalized -1..1; neutral is 0 (code's 0..16383 with neutral 8192 is equivalent). `THROTTLE_SET` 0..16383. `AP_VS_VAR_SET_ENGLISH` uses scaled signed int cast to uint (code already does this). `HEADING_BUG_SET` etc. are value sets safe to repeat; `AP_PANEL_*_HOLD` are toggles and must be edge-triggered (code already latches).
- Native AP events are toggles; dedup is required (code latches `_lastHdg`, etc.).
- `SimConnect_CallDispatch` blocks when queue empty; pump thread must only call when `hEventHandle` signaled (code already does).
- SimVars indexed 1-based; strings pass empty units.
- `IS GEAR FLOATS` can upgrade fixed-wing to amphibian at runtime (code already observes).
- RPN L/I/O/Z vars are FLOAT64 only; units may convert.

## Observed runtime issues (from logs)
- Excessive facility taxi-path spam and potential OPEN/CLOSE imbalance during airport load.
- Repeated data-definition re-registration risk on reconnect.
- Axis deadband cache can desync on mode switches; `Neutralize()` resets cache (verify all mode transitions call it).
- Racetrack hold 10-minute cap and terrain-clear release must be verified.
- Continuous-mode destination selection must handle rotorcraft (no runway) and water runways.

## Scope / modules to touch
1. `Core/SimConnect/SimConnect.cs` — audit P/Invoke layouts, string datum units, request IDs, facility OPEN/CLOSE pairing, exception reporting.
2. `Core/Aircraft/SimAircraft.cs` — verify pump thread, data decoding, reconnect data-definition re-registration.
3. `Core/Guidance/Autopilot.cs` — refactor PID instantiation to per-aircraft-kind factory; expose gains.
4. `Core/Aircraft/Performance.cs` / `AircraftKind.cs` — expose mass/inertia/control authority hints for PID tuning.
5. `Core/Pilot/PilotEngine.cs` — ensure mode switches call `Neutralize()` and PID reset; pass aircraft kind to autopilot.
6. `Core/Environment/Envelope.cs` — guard VS limits per aircraft.
7. Tests — add PID gain selection tests and SimConnect mapping tests where pure logic.

## PID persistence
- Store user overrides in `%LocalAppData%/AICopilotFS/pid-gains.json`, keyed by aircraft title (exact sim title).
- Schema: `{ "version": 1, "overrides": { "<title>": { "kind": "Airplane|Rotorcraft|...", "pitch": {kp,ki,kd,limit}, "bank": {...}, "speed": {...}, "altitude": {...}, "maxBankDeg": number, "controlAuthority": number } } }`.
- On load, merge overrides over kind-based defaults; unknown titles get defaults by detected kind.
- Atomic write (temp + replace) to avoid corruption; handle missing/corrupt file by resetting to defaults and logging.
- Add `PidGainStore` (pure-ish, testable) and wire into `PilotEngine` after aircraft identification.
- No GUI editor in this scope; manual JSON edits allowed.

## PID tailoring approach
- Introduce `AircraftControlProfile` (mass class, rotorcraft flag, approach speed, max bank, control authority) derived from `AircraftPerformance` + `AircraftKind`.
- `HybridAutopilot` accepts a profile and selects gain sets:
  - Fixed-wing (light): kp pitch 0.025, ki 0.002, kd 0.06; bank 0.04/0.001/0.05; speed 0.012/0.005/0.02.
  - Fixed-wing (heavy/jet): lower gains, larger integral limits.
  - Rotorcraft: very low pitch/bank gains, high damping, tighter VS authority, max bank 15°, speed loop disabled in hover.
- Reset loops on profile change and phase transitions.
- Make gains configurable via performance data when available (e.g., `flight_performance.cfg`).

## Ordered implementation tasks
1. Audit `SimConnect.cs` datum units/string handling and facility OPEN/CLOSE pairing; add unit tests for pairing logic.
2. Add `AircraftControlProfile` + `PidGainStore` in `Core/Aircraft`; wire defaults by kind.
3. Refactor `HybridAutopilot` to accept profile and select gains; reset on profile change.
4. Persist overrides per title via `PidGainStore` in `%LocalAppData%/AICopilotFS/pid-gains.json`.
5. Update `PilotEngine` to detect aircraft kind, build profile, load overrides, pass to autopilot, and `Neutralize()` on mode switches.
6. Add tests for gain selection and store round-trip.
7. Build and test; manual sim validation.

## Taxi speed management (realistic)
### Research findings
- Straight clear taxiway: 20–30 kt max; turns: ≤10 kt (often 6–8 kt for sharp/contaminated); apron/ramp: walking pace 5–8 kt; runway crossing: ≤15 kt; hold-short: decelerate to stop.
- Avoid riding brakes: use thrust to maintain speed, intermittent/stab braking for deceleration; test brakes early.
- Weather/surface: reduce to ~1/3 in low visibility/contaminated; wind affects ground speed.
- Aircraft-specific: heavy jets lower speeds and need more anticipation; rotorcraft hover-taxi ~25 kt.

### Design
- Extend `AircraftControlProfile` with taxi parameters:
  `MaxStraightTaxiKt`, `MaxTurnTaxiKt`, `ApronTaxiKt`, `RunwayCrossingTaxiKt`, `HoldShortDecelDistanceM`, `TurnDetectionAngleDeg`, `TaxiSpeedWeatherFactor`.
- Add `TaxiSpeedController` (pure, testable) computing target ground speed from:
  - current leg distance, next-leg turn angle, node type (parking/apron vs taxiway), `CrossesRunway`,
  - weather/surface factors, aircraft profile.
- Add brake control to `HybridAutopilot`:
  `SetBrakes(double left, double right)` using `AXIS_LEFT_BRAKE_SET`/`AXIS_RIGHT_BRAKE_SET` (map 0..1 to -16383..+16383), `ReleaseBrakes()`.
- Replace fixed throttle fractions in `ExecuteTaxiOut`/`ExecuteTaxiIn` with controller output:
  - maintain target ground speed via throttle modulation (increase if below target, reduce if above),
  - apply differential braking in turns and stab braking for deceleration to hold-short.
- Rotorcraft hover-taxi uses target 25 kt with speed-based throttle; water ops unchanged.
- Log target speed, actual GS, brake application.

### Ordered implementation tasks (taxi)
1. Extend `AircraftControlProfile` + defaults + persistence schema (backward compatible).
2. Add `TaxiSpeedController` with unit tests for straight/turn/apron/crossing/hold-short/weather.
3. Add brake methods to `HybridAutopilot`.
4. Refactor `ExecuteTaxiOut` and `ExecuteTaxiIn` to use controller and brakes.
5. Update rotorcraft hover-taxi to speed-based throttle (optional).
6. Manual sim validation: no excessive brake riding, smooth turns, holds short.

## Validation
- `dotnet build MSFS2024AIPilot.sln`
- `dotnet test MSFS2024AIPilot.sln`
- Manual: connect to running sim, observe no axis spam in logs, verify neutral surfaces at mode switch, verify rotorcraft hover-taxi and set-down.

## Risks
- SimConnect not thread-safe; must keep locking.
- Live sim required for integration validation; unit tests cover pure logic only.
- Changing axis scaling could destabilize flight; keep deadband and neutralize.

## Open questions
- Which aircraft types to prioritize for gain calibration (suggest: C172, A320, R22, B407)?
- Should PID gains be persisted per aircraft title in user config?

## Out of scope
- GUI redesign.
- Network/remote SimConnect.
- WASM in-sim panel.
