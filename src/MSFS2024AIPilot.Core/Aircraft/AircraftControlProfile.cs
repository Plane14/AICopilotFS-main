using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSFS2024AIPilot.Core.Aircraft;

/// <summary>Gain set for a single PID loop.</summary>
public sealed class PidGains
{
    public double Kp { get; set; }
    public double Ki { get; set; }
    public double Kd { get; set; }
    public double IntegralLimit { get; set; } = 100;

    public static PidGains Default(double kp, double ki, double kd, double limit = 100) =>
        new() { Kp = kp, Ki = ki, Kd = kd, IntegralLimit = limit };
}

/// <summary>Control profile applied to the hybrid autopilot for a given aircraft.</summary>
public sealed class AircraftControlProfile
{
    public AircraftKind Kind { get; set; } = AircraftKind.Airplane;
    public double MaxBankDeg { get; set; } = 25;
    public double ControlAuthority { get; set; } = 1.0;
    public PidGains Pitch { get; set; } = PidGains.Default(0.02, 0.002, 0.05);
    public PidGains Bank { get; set; } = PidGains.Default(0.03, 0.001, 0.04);
    public PidGains Speed { get; set; } = PidGains.Default(0.01, 0.005, 0.02);
    public PidGains Altitude { get; set; } = PidGains.Default(0.005, 0.0005, 0.02);
    public double MaxStraightTaxiKt { get; set; } = 25;
    public double MaxTurnTaxiKt { get; set; } = 10;
    public double ApronTaxiKt { get; set; } = 8;
    public double RunwayCrossingTaxiKt { get; set; } = 15;
    public double HoldShortDecelDistanceM { get; set; } = 40;
    public double TurnDetectionAngleDeg { get; set; } = 25;
    public double TaxiSpeedWeatherFactor { get; set; } = 0.8;

    /// <summary>
    /// Scales a gain set by <paramref name="factor"/> — heavier/sluggish
    /// aircraft get proportionally softer loops. Integral limits scale with
    /// the square root so big aeroplanes can still wind up enough to trim.
    /// </summary>
    private static PidGains Scaled(PidGains g, double factor) => new()
    {
        Kp = g.Kp * factor,
        Ki = g.Ki * factor,
        Kd = g.Kd * factor,
        IntegralLimit = g.IntegralLimit * Math.Sqrt(Math.Max(factor, 0.05)),
    };

    public static AircraftControlProfile ForKind(AircraftKind kind, AircraftPerformance perf)
    {
        var weight = perf.MaxGrossWeight;
        var isJet = perf.EngineType.Contains("JET", StringComparison.OrdinalIgnoreCase);
        var isTurbo = perf.EngineType.Contains("TURBO", StringComparison.OrdinalIgnoreCase);
        // Unknown weight defaults to the light profile — the conservative
        // (more responsive) end is safer than over-gaining an unknown jet.
        var heavy = isJet || (isTurbo && weight > 15000) || weight > 30000;
        var medium = !heavy && weight >= 6000;

        AircraftControlProfile profile;
        if (kind == AircraftKind.Rotorcraft)
        {
            // Rotorcraft respond far more slowly than fixed-wing: low gains,
            // heavy damping, tight VS authority. Scale by gross weight so a
            // light piston heli and a heavy twin-engine don't share gains.
            var massFactor = weight > 0 ? Math.Clamp(Math.Sqrt(weight / 3000.0), 0.85, 1.5) : 1.0;
            var f = 1.0 / massFactor;
            profile = new AircraftControlProfile
            {
                Kind = kind,
                MaxBankDeg = 15,
                ControlAuthority = 0.8,
                MaxStraightTaxiKt = 20,
                MaxTurnTaxiKt = 8,
                ApronTaxiKt = 6,
                RunwayCrossingTaxiKt = 12,
                HoldShortDecelDistanceM = 35,
                TurnDetectionAngleDeg = 20,
                TaxiSpeedWeatherFactor = 0.9,
                Pitch = Scaled(PidGains.Default(0.015, 0.001, 0.04, 50), f),
                Bank = Scaled(PidGains.Default(0.02, 0.0005, 0.03, 50), f),
                Speed = Scaled(PidGains.Default(0.008, 0.003, 0.015, 50), f),
                Altitude = Scaled(PidGains.Default(0.003, 0.0003, 0.01, 50), f),
            };
            return profile;
        }

        if (heavy)
        {
            profile = new AircraftControlProfile
            {
                Kind = kind,
                MaxBankDeg = 20,
                ControlAuthority = 0.9,
                MaxStraightTaxiKt = 18,
                MaxTurnTaxiKt = 8,
                ApronTaxiKt = 6,
                RunwayCrossingTaxiKt = 12,
                HoldShortDecelDistanceM = 50,
                TurnDetectionAngleDeg = 20,
                TaxiSpeedWeatherFactor = 0.85,
                Pitch = PidGains.Default(0.012, 0.001, 0.04, 80),
                Bank = PidGains.Default(0.02, 0.0005, 0.035, 80),
                Speed = PidGains.Default(0.008, 0.004, 0.018, 80),
                Altitude = PidGains.Default(0.003, 0.0003, 0.015, 80),
            };
        }
        else if (medium)
        {
            // Turboprops and small jets: between the light single and the
            // heavies — softer pitch, moderate bank authority.
            profile = new AircraftControlProfile
            {
                Kind = kind,
                MaxBankDeg = 22,
                ControlAuthority = 0.95,
                MaxStraightTaxiKt = 22,
                MaxTurnTaxiKt = 9,
                ApronTaxiKt = 7,
                RunwayCrossingTaxiKt = 14,
                HoldShortDecelDistanceM = 45,
                TurnDetectionAngleDeg = 22,
                TaxiSpeedWeatherFactor = 0.85,
                Pitch = PidGains.Default(0.016, 0.0015, 0.045, 90),
                Bank = PidGains.Default(0.025, 0.0008, 0.04, 90),
                Speed = PidGains.Default(0.009, 0.004, 0.02, 90),
                Altitude = PidGains.Default(0.004, 0.0004, 0.018, 90),
            };
        }
        else
        {
            profile = new AircraftControlProfile
            {
                Kind = kind,
                MaxBankDeg = 25,
                ControlAuthority = 1.0,
                MaxStraightTaxiKt = 25,
                MaxTurnTaxiKt = 10,
                ApronTaxiKt = 8,
                RunwayCrossingTaxiKt = 15,
                HoldShortDecelDistanceM = 40,
                TurnDetectionAngleDeg = 25,
                TaxiSpeedWeatherFactor = 0.8,
                Pitch = PidGains.Default(0.02, 0.002, 0.05, 100),
                Bank = PidGains.Default(0.03, 0.001, 0.04, 100),
                Speed = PidGains.Default(0.01, 0.005, 0.02, 100),
                Altitude = PidGains.Default(0.005, 0.0005, 0.02, 100),
            };
        }

        // Floats add pitch inertia and drag: soften the pitch loop and the
        // bank a touch so floatplanes don't porpoise on rotation.
        if (kind is AircraftKind.Seaplane or AircraftKind.Amphibian)
        {
            profile.Kind = kind;
            profile.Pitch = Scaled(profile.Pitch, 0.85);
            profile.Altitude = Scaled(profile.Altitude, 0.85);
            profile.ControlAuthority *= 0.9;
        }
        return profile;
    }
}

/// <summary>Persists per-aircraft PID overrides in user config.</summary>
public sealed class PidGainStore
{
    public int Version { get; set; } = 1;
    public Dictionary<string, AircraftControlProfile> Overrides { get; set; } =
        new(StringComparer.Ordinal);

    public static string DefaultPath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "AICopilotFS", "pid-gains.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static PidGainStore Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new PidGainStore();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new PidGainStore();
            return JsonSerializer.Deserialize<PidGainStore>(json, JsonOpts) ?? new PidGainStore();
        }
        catch
        {
            return new PidGainStore();
        }
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch { /* best effort */ }
    }

    public AircraftControlProfile Resolve(string title, AircraftKind kind, AircraftPerformance perf)
    {
        if (!string.IsNullOrWhiteSpace(title) && Overrides.TryGetValue(title, out var existing))
            return Clone(existing);
        return AircraftControlProfile.ForKind(kind, perf);
    }

    public void Seed(string title, AircraftControlProfile profile)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (Overrides.ContainsKey(title)) return;
        Overrides[title] = Clone(profile);
    }

    private static AircraftControlProfile Clone(AircraftControlProfile p) => new()
    {
        Kind = p.Kind,
        MaxBankDeg = p.MaxBankDeg,
        ControlAuthority = p.ControlAuthority,
        MaxStraightTaxiKt = p.MaxStraightTaxiKt,
        MaxTurnTaxiKt = p.MaxTurnTaxiKt,
        ApronTaxiKt = p.ApronTaxiKt,
        RunwayCrossingTaxiKt = p.RunwayCrossingTaxiKt,
        HoldShortDecelDistanceM = p.HoldShortDecelDistanceM,
        TurnDetectionAngleDeg = p.TurnDetectionAngleDeg,
        TaxiSpeedWeatherFactor = p.TaxiSpeedWeatherFactor,
        Pitch = new PidGains { Kp = p.Pitch.Kp, Ki = p.Pitch.Ki, Kd = p.Pitch.Kd, IntegralLimit = p.Pitch.IntegralLimit },
        Bank = new PidGains { Kp = p.Bank.Kp, Ki = p.Bank.Ki, Kd = p.Bank.Kd, IntegralLimit = p.Bank.IntegralLimit },
        Speed = new PidGains { Kp = p.Speed.Kp, Ki = p.Speed.Ki, Kd = p.Speed.Kd, IntegralLimit = p.Speed.IntegralLimit },
        Altitude = new PidGains { Kp = p.Altitude.Kp, Ki = p.Altitude.Ki, Kd = p.Altitude.Kd, IntegralLimit = p.Altitude.IntegralLimit },
    };
}
