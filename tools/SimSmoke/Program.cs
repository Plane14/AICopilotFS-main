// Live SimConnect smoke test: connects to the running sim, prints decoded
// telemetry once per second for --seconds (default 20), then disconnects.
// Read-only — it never transmits control events, so it is safe to run while
// someone is flying.
//
// Usage: dotnet run --project tools/SimSmoke [--seconds N]

using MSFS2024AIPilot.Core.Aircraft;

var seconds = 20;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--seconds" && int.TryParse(args[i + 1], out var n)) seconds = n;

using var aircraft = new SimAircraft();
aircraft.OnLog += m => Console.WriteLine(m);
aircraft.OnError += m => Console.WriteLine($"ERROR: {m}");
aircraft.OnAircraftTitleChanged += t => Console.WriteLine($"Aircraft: {t}");

try
{
    await aircraft.ConnectAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"Connect failed: {ex}");
    return 1;
}

var deadline = DateTime.UtcNow.AddSeconds(seconds);
while (DateTime.UtcNow < deadline)
{
    var s = aircraft.State;
    Console.WriteLine(
        $"{DateTime.Now:HH:mm:ss}  {aircraft.AircraftTitle,-28} " +
        $"lat={s.Latitude,9:F5} lon={s.Longitude,10:F5} " +
        $"alt={s.Altitude,7:F0}ft agl={s.AltitudeAGL,6:F0}ft " +
        $"IAS={s.IndicatedAirspeed,6:F1}kt GS={s.GroundSpeed,6:F1}kt " +
        $"vs={s.VerticalSpeed,6:F0}fpm hdg={s.TrueHeading,5:F1} " +
        $"ground={s.OnGround} ap={s.Autopilot.MasterSwitch}");
    await Task.Delay(1000);
}

aircraft.Stop();
Console.WriteLine("Done.");
return 0;
