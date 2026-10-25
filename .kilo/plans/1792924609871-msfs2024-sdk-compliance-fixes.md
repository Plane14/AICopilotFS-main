# MSFS 2024 SDK Compliance Fixes Plan

Based on comprehensive research of the MSFS 2024 SimConnect SDK documentation and analysis of the codebase, this plan addresses critical bugs and gaps.

## Priority 1: Critical Bugs (Must Fix)

### 1. EGT Units Mismatch - CRITICAL
**Issue**: MSFS 2024 SDK documents `GENERAL ENG EXHAUST GAS TEMPERATURE` as returning **Rankine**, but code treats it as Celsius.
- **Files**: `AircraftController.cs:205` (comment), `FlightEnvelope.cs:121-122` (redline values), `FlightEnvelope.cs:171-177` (EgtRedlineC)
- **Fix**: 
  - Convert Rankine to Celsius: `C = (R - 491.67) * 5/9`
  - Update redline values: Jet ~950°C = 1712°R, Turboprop ~850°C = 1538°R
  - Handle piston engines (no single EGT redline)

### 2. Taxi Path Parking END Index - CRITICAL
**Issue**: For PARKING type paths (TYPE=3), END indexes the parking list, not taxi points.
- **Files**: `AirportFacilitiesLoader.cs:376-398` (`ParseTaxiPath`)
- **Fix**: Check path type, if PARKING then resolve END against parking nodes

### 3. AMBIENT PRECIP STATE Mask Handling
**Issue**: Docs say Mask: 2=None, 4=Rain, 8=Snow. Code checks `precipState > 0.5` which treats None(2) as precipitation.
- **Files**: `WeatherModel.cs:140-145` (`IcingConditions`)
- **Fix**: Check for Rain (4) or Snow (8) bits: `(precipState & 12) != 0`

## Priority 2: Significant Gaps

### 4. Runway Threshold Data (Displaced Thresholds)
**Issue**: SDK provides `PRIMARY_THRESHOLD`/`SECONDARY_THRESHOLD` as `PAVEMENT` structs with actual threshold positions. Code calculates from center ± length/2.
- **Files**: `AirportFacilitiesLoader.cs` (runway definition), `RunwayInfo.cs` (threshold calculation)
- **Fix**: Add threshold fields to runway definition, parse PAVEMENT struct

### 5. Missing SimVars for Envelope
**Issue**: Not reading `MACH MAX OPERATE` and `BARBER POLE MACH` for additional speed limits.
- **Files**: `AircraftController.cs` (SYSTEMS block)
- **Fix**: Add to SYSTEMS data definition

### 6. ATC Menu OCR P/Invoke Fix
**Issue**: `GetDC0`/`ReleaseDC0` don't exist - correct APIs are `GetDC(HWND)`/`ReleaseDC(HWND, HDC)`.
- **Files**: `AtcScreenCapture.cs`, `AtcMenuScreenReader.cs`
- **Fix**: Correct P/Invoke signatures, find simulator window handle

### 7. FLAPS_UP Event Compatibility
**Issue**: "FLAPS_UP" not a standard SimConnect event; SDK publishes FLAPS_1..4. May not work on all aircraft.
- **Files**: `AIPilot.cs:3376` (ExecuteRollout)
- **Fix**: Use FLAPS_SET with index 0 or iterate FLAPS_1..4 down

## Priority 3: Enhancements

### 8. Parking Spot Type for Routing Constraints
**Issue**: Parking TYPE enum (RAMP_GA, GATE_SMALL, etc.) not used for aircraft size constraints.
- **Files**: `AirportData.cs`, `TaxiRouter.cs`, `TaxiConstraints.cs`
- **Fix**: Map parking types to wingspan/weight constraints

### 9. Taxiway Name Resolution Improvement
**Issue**: `ResolveTaxiwayChain` could better handle taxiway name variations.
- **Files**: `TaxiRouter.cs:261-327`
- **Fix**: Improve matching for "ALPHA ONE" vs "ALPHA" patterns

### 10. SURFACE CONDITION Documentation
**Issue**: Enum values not documented in code comments.
- **Files**: `AircraftController.cs:332-333`, `WeatherModel.cs:169-192`
- **Fix**: Add enum documentation

## Implementation Order

1. **EGT Units** - Highest impact on safety
2. **Taxi Path Parking Index** - Affects routing correctness
3. **Precip State Mask** - Affects icing detection
4. **Runway Thresholds** - Improves takeoff/landing accuracy
5. **Missing SimVars** - Completes envelope protection
6. **ATC OCR Fix** - Enables menu reading fallback
7. **FLAPS_UP Event** - Improves compatibility
8. **Parking Types** - Enhances routing
9. **Taxiway Names** - Improves ATC route following
10. **Documentation** - Maintainability

## Validation

Each fix should:
- Have unit tests added/updated
- Be tested against MSFS 2024 SDK documentation
- Not break existing functionality
- Include locale-independent parsing where applicable