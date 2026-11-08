using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Core.Checklists;

public enum ChecklistPhase
{
    Preflight,
    Startup,
    BeforeTakeoff,
    AfterTakeoff,
    Cruise,
    Descent,
    Approach,
    Landing,
    Shutdown,
}

public sealed class ChecklistItem
{
    public string Text { get; set; } = "";
    public string? EventName { get; set; }
    public string? SimVar { get; set; }
    public double ExpectedValue { get; set; }
    public bool Done { get; set; }
    public string? Note { get; set; }
}

public sealed class Checklist
{
    public ChecklistPhase Phase { get; set; }
    public string Title { get; set; } = "";
    public List<ChecklistItem> Items { get; } = new();
    public int CurrentIndex { get; set; }
    public bool Complete => Items.Count > 0 && CurrentIndex >= Items.Count;
    public double Progress => Items.Count == 0 ? 0 : (double)Items.Count(i => i.Done) / Items.Count;
}

/// <summary>
/// Aircraft checklist support: parses the MSFS 2024 SimBase.Document checklist XML
/// when the aircraft ships one, falls back to a generic flow otherwise, and binds
/// items to sim events/simvars where the text resolver can match them.
/// </summary>
public sealed class ChecklistManager
{
    private readonly Dictionary<ChecklistPhase, Checklist> _checklists = new();
    private ChecklistPhase _currentPhase = ChecklistPhase.Preflight;
    private readonly string? _aircraftDir;

    public event Action<Checklist?>? OnChecklistChanged;
    public event Action<string>? OnLog;

    public ChecklistManager(string? aircraftDir = null)
    {
        _aircraftDir = aircraftDir;
        Log("ChecklistManager initialized");
        LoadFromAircraft();
        EnsureGenericFallbacks();
        OnChecklistChanged?.Invoke(Current);
    }

    private void Log(string msg) => OnLog?.Invoke($"[Checklists] {msg}");

    public ChecklistPhase CurrentPhase => _currentPhase;
    public Checklist? Current => _checklists.TryGetValue(_currentPhase, out var c) ? c : null;

    public void SetPhase(ChecklistPhase phase)
    {
        if (_currentPhase == phase) return;
        _currentPhase = phase;
        Log($"Phase changed -> {phase}");
        OnChecklistChanged?.Invoke(Current);
    }

    /// <summary>Marks the current item done and advances.</summary>
    public void Advance()
    {
        var c = Current;
        if (c is null) return;
        if (c.CurrentIndex < c.Items.Count)
            c.Items[c.CurrentIndex].Done = true;
        c.CurrentIndex++;
        Log($"Checklist '{c.Title}' advanced to item {c.CurrentIndex}/{c.Items.Count}");
        OnChecklistChanged?.Invoke(c);
    }

    public void Reset(ChecklistPhase? phase = null)
    {
        foreach (var c in _checklists.Values)
        {
            foreach (var i in c.Items) i.Done = false;
            c.CurrentIndex = 0;
        }
        if (phase is { } p) _currentPhase = p;
        Log($"Checklists reset{(phase.HasValue ? $" phase={phase}" : "")}");
        OnChecklistChanged?.Invoke(Current);
    }

    /// <summary>Reloads checklists after the aircraft changes.</summary>
    public void Reload(string? aircraftDir)
    {
        _checklists.Clear();
        if (!string.IsNullOrEmpty(aircraftDir)) LoadFromAircraft(aircraftDir);
        EnsureGenericFallbacks();
        Log($"Checklists reloaded for dir={aircraftDir}, phases={_checklists.Count}");
        OnChecklistChanged?.Invoke(Current);
    }

    private void LoadFromAircraft(string? dir = null)
    {
        dir ??= _aircraftDir;
        if (string.IsNullOrEmpty(dir)) return;
        int fileCount = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
        {
            fileCount++;
            try
            {
                var doc = XDocument.Load(file);
                var simBase = doc.Root;
                if (simBase?.Attribute("Type")?.Value != "SimBase.Document") continue;
                int checklistCount = 0;
                foreach (var desc in simBase.Descendants("Checklist"))
                {
                    ParseChecklistXml(desc);
                    checklistCount++;
                }
                if (checklistCount > 0) Log($"Loaded {checklistCount} checklists from {file}");
            }
            catch { }
        }
        Log($"Scanned {fileCount} XML files in {dir}");
    }

    private void ParseChecklistXml(XElement desc)
    {
        // MSFS checklist XML: <Checklist><Title>..</Title><ChecklistItem>..</ChecklistItem></Checklist>
        var title = desc.Element("Title")?.Value ?? "";
        var phase = InferPhase(title);
        var checklist = GetOrCreate(phase);
        checklist.Title = title;
        var itemCount = 0;
        foreach (var item in desc.Descendants("ChecklistItem"))
        {
            var text = item.Element("Text")?.Value ?? item.Element("Name")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            checklist.Items.Add(new ChecklistItem
            {
                Text = text.Trim(),
                EventName = item.Element("Event")?.Value,
                SimVar = item.Element("SimVar")?.Value,
                ExpectedValue = double.TryParse(item.Element("Value")?.Value, out var v) ? v : 0,
            });
            itemCount++;
        }
        Log($"Checklist '{title}' ({phase}) parsed with {itemCount} items");
    }

    private static ChecklistPhase InferPhase(string title)
    {
        var t = title.ToLowerInvariant();
        if (t.Contains("preflight") || t.Contains("pre-flight")) return ChecklistPhase.Preflight;
        if (t.Contains("start") || t.Contains("engine")) return ChecklistPhase.Startup;
        if (t.Contains("before takeoff") || t.Contains("before take-off")) return ChecklistPhase.BeforeTakeoff;
        if (t.Contains("after takeoff") || t.Contains("after take-off")) return ChecklistPhase.AfterTakeoff;
        if (t.Contains("cruise") || t.Contains("enroute")) return ChecklistPhase.Cruise;
        if (t.Contains("descent")) return ChecklistPhase.Descent;
        if (t.Contains("approach")) return ChecklistPhase.Approach;
        if (t.Contains("landing") || t.Contains("arrival")) return ChecklistPhase.Landing;
        if (t.Contains("shutdown") || t.Contains("shutdown") || t.Contains("postflight")) return ChecklistPhase.Shutdown;
        return ChecklistPhase.Preflight;
    }

    private Checklist GetOrCreate(ChecklistPhase phase)
    {
        if (!_checklists.TryGetValue(phase, out var c))
        {
            c = new Checklist { Phase = phase };
            _checklists[phase] = c;
        }
        return c;
    }

    /// <summary>Generic checklists used when the aircraft ships none.</summary>
    private void EnsureGenericFallbacks()
    {
        AddGeneric(ChecklistPhase.Preflight, "Preflight", new[]
        {
            "Parking brake set", "Beacon lights on", "Navigation lights on",
            "Transponder set", "Altimeter set", "Fuel quantity checked",
            "Flight controls checked", "Avionics master on", "GPS flight plan loaded",
        });
        AddGeneric(ChecklistPhase.Startup, "Engine Start", new[]
        {
            "Master switch on", "Avionics on", "Fuel pumps on",
            "Mixture rich", "Throttle idle", "Starter engage", "Oil pressure check",
        });
        AddGeneric(ChecklistPhase.BeforeTakeoff, "Before Takeoff", new[]
        {
            "Flaps set", "Trim set", "Doors closed", "Seatbelts on",
            "Takeoff briefing complete", "ATC clearance obtained", "Runway verified",
        });
        AddGeneric(ChecklistPhase.AfterTakeoff, "After Takeoff", new[]
        {
            "Gear up", "Flaps up", "Climb power set", "Navigation lights as required",
        });
        AddGeneric(ChecklistPhase.Cruise, "Cruise", new[]
        {
            "Autopilot engaged", "Altitude verified", "Fuel balance checked",
        });
        AddGeneric(ChecklistPhase.Descent, "Descent", new[]
        {
            "ATIS obtained", "Altimeter set", "Approach briefing complete",
        });
        AddGeneric(ChecklistPhase.Approach, "Approach", new[]
        {
            "Landing gear down", "Flaps approach", "Landing lights on",
            "Cabin secure", "Final checklist complete",
        });
        AddGeneric(ChecklistPhase.Landing, "Landing", new[]
        {
            "Touchdown", "Reverse/thrust reversers", "Spoilers up",
        });
        AddGeneric(ChecklistPhase.Shutdown, "Shutdown", new[]
        {
            "Parking brake set", "Avionics off", "Master switch off",
        });
    }

    private void AddGeneric(ChecklistPhase phase, string title, string[] items)
    {
        var c = GetOrCreate(phase);
        if (c.Items.Count == 0)
        {
            c.Title = title;
            foreach (var text in items)
                c.Items.Add(new ChecklistItem { Text = text });
        }
    }
}

/// <summary>Maps checklist text onto sim events/simvars where possible.</summary>
public static class ChecklistBindings
{
    /// <summary>Returns the sim event that accomplishes the checklist item, if known.</summary>
    public static string? EventFor(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("parking brake")) return "PARKING_BRAKE_SET";
        if (t.Contains("beacon")) return "BEACON_LIGHTS_SET";
        if (t.Contains("navigation light") || t.Contains("nav light")) return "NAV_LIGHTS_SET";
        if (t.Contains("strobe")) return "STROBES_SET";
        if (t.Contains("landing light")) return "LANDING_LIGHTS_SET";
        if (t.Contains("taxi light")) return "TAXI_LIGHTS_SET";
        if (t.Contains("gear up")) return "GEAR_UP";
        if (t.Contains("gear down") || t.Contains("landing gear")) return "GEAR_DOWN";
        if (t.Contains("flap")) return null; // handled by flap index logic
        if (t.Contains("master switch")) return "AVIONICS_MASTER_SET";
        if (t.Contains("starter") || t.Contains("engine start")) return "TOGGLE_STARTER1";
        if (t.Contains("pitot heat")) return "PITOT_HEAT_TOGGLE";
        return null;
    }

    /// <summary>SimVar that reports whether the item is satisfied, if known.</summary>
    public static string? SimVarFor(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("parking brake")) return "BRAKE PARKING POSITION";
        if (t.Contains("gear")) return "GEAR TOTAL PCT EXTENDED";
        if (t.Contains("on ground")) return "SIM ON GROUND";
        return null;
    }
}
