using MSFS2024AIPilot.Core;

Console.Title = "MSFS 2024 AI Pilot";

var pilot = new AIPilot();

pilot.OnStatus += s => Console.WriteLine($"[STATUS] {s}");
pilot.OnPhaseChanged += (old, newPhase) => Console.WriteLine($"[PHASE] {old} -> {newPhase}");
pilot.OnControlStateChanged += s => Console.WriteLine($"[CONTROL] {s}");
pilot.OnAtcLog += t => Console.WriteLine($"[ATC] {t}");

Console.WriteLine("MSFS 2024 AI Pilot - gate-to-gate autonomous flight");
Console.WriteLine("---------------------------------------------");
Console.WriteLine("Commands:");
Console.WriteLine("  engage     - AI takes control of the aircraft");
Console.WriteLine("  disengage  - return control to the user");
Console.WriteLine("  toggle     - switch between AI and user control");
Console.WriteLine("  plan <pln> - load a flight plan (.pln path, or none for the sim's active plan)");
Console.WriteLine("  airport [icao] - load/inspect airport ground data (runways, taxiways, gates)");
Console.WriteLine("               'airport reload <ICAO>' rebuilds the graph from your position now");
Console.WriteLine("  taxi        - preview the taxi route from your position to the runway");
Console.WriteLine("  nav [all]   - flight plan route and published approach/missed approach");
Console.WriteLine("  traffic     - show surrounding aircraft and runway occupancy");
Console.WriteLine("  atc [cmd]   - ATC bridge: status, native/beyond/auto, request <kind>, tune <mhz>,");
Console.WriteLine("               squawk <code>, menu open|close|<n>, process <text>, cpdlc");
Console.WriteLine("  checklist [run <STEP>|skip|reset|reload] - aircraft checklist status/control");
Console.WriteLine("  pushback [request|dismiss] - pushback tug: status, request one, release one");
Console.WriteLine("  status     - show current state");
Console.WriteLine("  quit       - exit");
Console.WriteLine();

await pilot.StartAsync();

var running = true;

// Ctrl+C used to terminate the process where it stood: the SimConnect handle, the HTTP
// listener and the named pipe were never closed, and the simulator was left holding a
// connection to a host that no longer existed. Handing control back first means the
// aircraft keeps the control mode it was in, and the loop below performs the shutdown.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (pilot.ControlState == PilotControlState.AiActive)
    {
        pilot.SetControlState(PilotControlState.Manual);
    }
    running = false;
};

while (running)
{
    var input = Console.ReadLine();
    // A closed or redirected stdin returns null for ever; continuing on it spun this loop
    // at 100 % CPU and never exited. It means there is nothing more to read, so exit.
    if (input is null)
    {
        Console.WriteLine("Input closed - shutting down.");
        break;
    }
    if (string.IsNullOrWhiteSpace(input)) continue;

    var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var cmd = parts[0].ToLowerInvariant();
    var arg = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "engage":
                pilot.SetControlState(PilotControlState.AiActive);
                break;
        case "disengage":
            pilot.SetControlState(PilotControlState.Manual);
            break;
        case "toggle":
            pilot.ToggleControl();
            break;
        case "plan":
            if (!string.IsNullOrEmpty(arg))
            {
                pilot.LoadFlightPlan(arg);
            }
            else
            {
                var latest = pilot.FindLatestFlightPlan();
                if (latest != null)
                    pilot.LoadFlightPlan(latest);
                else
                    Console.WriteLine("No flight plan found. Specify a .pln path.");
            }
            break;
        case "airport":
            if (!string.IsNullOrEmpty(arg))
            {
                // A forced reload rebuilds the graph against where the aircraft is standing
                // now: the frame is calibrated from position, so an airport loaded before
                // arriving has a frame that was chosen from the wrong position.
                //
                // The flag is read first, in its own statement: method arguments are
                // evaluated left to right, so passing "IsReloadCommand(ref arg)" as an
                // argument of RequestAirportData ran it *after* `arg` had already been
                // captured - the reload asked the simulator for the airport "RELOAD KJFK"
                // and printed the stripped name it had been changed to.
                var forceReload = AirportCommandArguments.IsReloadCommand(ref arg);
                pilot.RequestAirportData(arg, force: forceReload);
                Console.WriteLine($"[airport] requesting {arg}");
            }
            else
            {
                foreach (var airport in pilot.GetLoadedAirports())
                {
                    Console.WriteLine($"{airport.Icao} {airport.Name}");
                    Console.WriteLine($"  nodes: {airport.Nodes.Count}  runways: {airport.Runways.Count}  " +
                                      $"paths resolved: {airport.PathResolutionRatio:P0}  frame: {airport.Frame}");
                    var holdShorts = airport.Nodes.Values
                        .Where(n => n.Type is GroundNodeType.HoldShort or GroundNodeType.IlsHoldShort)
                        .ToList();
                    Console.WriteLine($"  hold-shorts: {holdShorts.Count}" +
                        (holdShorts.Count > 0 ? $" ({string.Join(", ", holdShorts.Select(h => h.Type.ToString()))})" : ""));
                    foreach (var gate in airport.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking).Take(5))
                    {
                        Console.WriteLine($"  gate {gate.ParkingName ?? "?"} (#{gate.ParkingNumber}) hdg {gate.HeadingDegTrue:F0}T");
                    }
                    foreach (var rw in airport.Runways)
                    {
                        Console.WriteLine($"  rwy {rw.PrimaryName}/{rw.SecondaryName} hdg {rw.HeadingDegTrue:F0}T " +
                                          $"len {Geo.MetersToFeet(rw.LengthMeters):F0}ft magvar {airport.MagneticVariationDeg:F1}");
                        var threshold = rw.ThresholdFor(rw.PrimaryName);
                        if (threshold.HasValue)
                        {
                            Console.WriteLine($"    {rw.PrimaryName} threshold {threshold.Value.lat:F5},{threshold.Value.lon:F5}");
                        }
                    }
                }
            }
            break;
        case "taxi":
            {
                var airport = pilot.GetLoadedAirports().FirstOrDefault();
                if (airport is null)
                {
                    Console.WriteLine("No airport data loaded yet. Use `airport <ICAO>` first.");
                    break;
                }
                var start = TaxiRouter.FindNearestNode(airport, pilot.State.Latitude, pilot.State.Longitude);
                var runway = pilot.State.Navigation.DepartureRunway;
                // Reachability, not proximity to the runway centre: at a parallel-strip
                // airport the nearest hold-short can belong to the other runway.
                var goal = !string.IsNullOrEmpty(runway)
                    ? TaxiRouter.FindReachableHoldShort(airport, start?.Index ?? -1, runway)
                    : null;
                if (start is null || goal is null)
                {
                    Console.WriteLine("Could not resolve start or goal node.");
                    break;
                }
                var route = TaxiRouter.FindRoute(airport, start.Index, goal.Index);
                Console.WriteLine(route.Success
                    ? $"Route {start.Index} -> {goal.Index}: {route.TotalLengthMeters:F0} m, {route.Legs.Count} legs ({route.Describe()})"
                    : $"Routing failed: {route.FailureReason}");
                var runwayInfo = airport.FindRunway(runway ?? "");
                var threshold = runwayInfo?.ThresholdFor(runway ?? "");
                if (threshold.HasValue)
                {
                    var distance = Geo.DistanceMeters(pilot.State.Latitude, pilot.State.Longitude,
                        threshold.Value.lat, threshold.Value.lon);
                    Console.WriteLine($"  hold-short node {goal.Index}; threshold {threshold.Value.lat:F5}," +
                                      $"{threshold.Value.lon:F5}, {distance:F0} m away");
                }
            }
            break;
        case "nav":
            {
                var nav = pilot.NavStatus();
                Console.WriteLine($"Nav route: {nav.RouteLegCount} legs ({nav.RouteGeometryLegs} with geometry), " +
                                  $"{nav.FirstLeg} -> {nav.LastLeg}, rebuilt {nav.RebuildCount}x");
                Console.WriteLine(nav.ArrivalApproachesLoaded
                    ? $"  navdata: {nav.ArrivalApproachCount} flyable approaches published at the arrival airport"
                    : "  navdata: arrival airport procedures not loaded yet");
                if (nav.UnresolvedFixes.Count > 0)
                    Console.WriteLine($"  unresolved fixes: {string.Join(", ", nav.UnresolvedFixes)}");
                Console.WriteLine($"  approach: {(nav.HasApproach ? nav.ApproachText : "none loaded from the navdata")}");
                Console.WriteLine($"  missed approach: {(nav.MissedApproachLegs > 0 ? nav.MissedApproachText : "none")}");
                if (nav.DirectToFix is { } fix) Console.WriteLine($"  cleared direct to: {fix}");
                if (nav.ActiveLeg.Length > 0)
                {
                    Console.WriteLine($"  following: {nav.ActiveLeg} ({nav.ActiveLegIndex}), " +
                                      $"{nav.ActiveLegDistanceMeters:N0} m to the fix, " +
                                      $"cross-track {nav.CrossTrackMeters:N0} m");
                }

                if (arg.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(nav.RouteText);
                }
            }
            break;
        case "status":
            var atcStatus = pilot.AtcStatus();
            Console.WriteLine($"Control: {pilot.ControlState}");
            Console.WriteLine($"Phase:   {pilot.CurrentPhase}");
            Console.WriteLine($"AGL:     {pilot.State.AltitudeAGL:F0} ft");
            Console.WriteLine($"IAS:     {pilot.State.IndicatedAirspeed:F0} kt");
            Console.WriteLine($"HDG:     {pilot.State.Heading:F0}");
            var navStatus = pilot.NavStatus();
            Console.WriteLine($"Nav:     {(navStatus.ActiveLeg.Length > 0 ? navStatus.ActiveLeg : navStatus.FirstLeg)}" +
                              $" ({(navStatus.HasApproach ? "published approach" : "no published approach")})");
            Console.WriteLine($"ATC:     {MSFS2024AIPilot.Core.AtcCommunicationHub.DescribeSource(atcStatus.Source)}" +
                              $" (clearances: IFR={(atcStatus.ClearedIfr ? "yes" : "no")} " +
                              $"taxi={(atcStatus.ClearedTaxi ? "yes" : "no")} " +
                              $"takeoff={(atcStatus.ClearedTakeoff ? "yes" : "no")})");
            Console.WriteLine($"Traffic: {pilot.Traffic.Describe()}" +
                              $" (runway entry {(pilot.TrafficAllowsRunwayEntry ? "safe" : "UNSAFE")})");
            var iceState = pilot.AirframeIcePresent
                ? $"{pilot.StructuralIcePercent:F0}% on airframe"
                : pilot.IcingConditionsPresent ? "icing risk" : "none";
            Console.WriteLine($"Weather: wind {pilot.WindDirectionDeg:000}/{pilot.WindSpeedKt:F0} kt " +
                              $"(crosswind {pilot.CrosswindKtOn(pilot.State.Heading):+0;-0} kt), " +
                              $"turbulence {pilot.Turbulence}, ice {iceState}, {pilot.SurfaceText} surface");
            Console.WriteLine($"Pushback: {pilot.PushbackText}" +
                              (pilot.PushbackAttached ? $", pushed {pilot.PushbackPushedMeters:F0} m" : ""));
            var limitsLine = pilot.LimitExceeded
                ? $"EXCEEDED: {pilot.ActiveLimitBreach}"
                : "within limits";
            Console.WriteLine($"Limits:  {limitsLine}");
            Console.WriteLine($"        {pilot.LimitsText}");
            break;
        case "traffic":
            {
                var traffic = pilot.Traffic;
                Console.WriteLine($"Traffic: {traffic.Describe()}");
                Console.WriteLine($"  runway entry: {(pilot.TrafficAllowsRunwayEntry ? "safe" : "UNSAFE - holding short")}");
                if (traffic.DepartureRunwayOccupant is { } occupant)
                    Console.WriteLine($"  runway occupied: {TrafficFormat.Text(occupant)}");
                if (traffic.ArrivalOnDepartureRunway is { } arrival)
                    Console.WriteLine($"  aircraft on final: {TrafficFormat.Text(arrival)}");
                foreach (var conflict in traffic.AirborneConflicts)
                    Console.WriteLine($"  airborne conflict: {TrafficFormat.Text(conflict)}");
                var nearest = traffic.Contacts.OrderBy(c => c.RangeMeters).Take(8).ToList();
                if (nearest.Count > 0)
                {
                    Console.WriteLine("  nearest:");
                    foreach (var c in nearest) Console.WriteLine($"    {TrafficFormat.Text(c)}");
                }
            }
            break;
        case "atc":
            Console.WriteLine(pilot.RunAtcCommand(arg));
            break;
        case "pushback":
            {
                // The stand-holding stage, out of band: with no argument it reports what
                // the tug is doing, `request` asks the controller for one and `dismiss`
                // stops a pushback that is under way.
                var sub = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var subCmd = sub.Length > 0 ? sub[0].ToLowerInvariant() : "";
                switch (subCmd)
                {
                    case "request":
                        Console.WriteLine(pilot.RequestPushback());
                        break;
                    case "dismiss":
                    case "stop":
                    case "release":
                        Console.WriteLine(pilot.DismissPushback());
                        break;
                    default:
                        Console.WriteLine($"Pushback: {pilot.PushbackText}");
                        Console.WriteLine($"  attached: {pilot.PushbackAttached}  " +
                                          $"available: {pilot.PushbackAvailable}  " +
                                          $"requested: {pilot.PushbackRequested}");
                        if (pilot.PushbackAttached)
                            Console.WriteLine($"  pushed {pilot.PushbackPushedMeters:F0} m" +
                                              $"{(pilot.ReleasingPushback ? " - releasing the tug" : "")}");
                        break;
                }
            }
            break;
        case "checklist":
            {
                var cs = pilot.ChecklistStatus();
                Console.WriteLine($"Checklists: {cs.Source} ({cs.Format}), enabled={cs.Enabled}, loaded={cs.Loaded}");
                if (!cs.Loaded)
                {
                    Console.WriteLine("  none loaded - waiting for the aircraft package to be identified");
                    break;
                }
                foreach (var l in cs.Lists) Console.WriteLine($"  {l}");
                Console.WriteLine($"  completed: {(cs.Completed.Count > 0 ? string.Join(", ", cs.Completed) : "none")}");
                if (cs.Item.Length > 0)
                    Console.WriteLine($"  now: {cs.Step} [{cs.Index}/{cs.Count}] {cs.Item} - {cs.Expectation} ({cs.ItemState})");

                var sub = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var subCmd = sub.Length > 0 ? sub[0].ToLowerInvariant() : "";
                var subArg = sub.Length > 1 ? sub[1] : "";
                switch (subCmd)
                {
                    case "run":
                        if (Enum.TryParse<ChecklistStep>(subArg, true, out var step))
                            pilot.RunChecklistStep(step);
                        else
                            Console.WriteLine($"Unknown step '{subArg}'. Steps: {string.Join(", ", Enum.GetNames<ChecklistStep>())}");
                        break;
                    case "skip":
                        pilot.SkipChecklistItem();
                        break;
                    case "reset":
                        pilot.ResetChecklists();
                        break;
                    case "reload":
                        pilot.ReloadChecklists();
                        break;
                }
            }
            break;
        case "quit":
        case "exit":
            running = false;
            break;
        default:
            Console.WriteLine($"Unknown command: {cmd}");
            break;
    }
}

pilot.Stop();
pilot.Dispose();
Console.WriteLine("AI Pilot shut down.");
