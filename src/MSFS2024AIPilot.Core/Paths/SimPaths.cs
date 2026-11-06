using System;
using System.Collections.Generic;
using System.IO;

namespace MSFS2024AIPilot.Core.Paths;

/// <summary>User-overridable locations of MSFS 2024 content on disk.</summary>
public sealed class SimulatorPathsConfig
{
    public string? LocalAppData { get; set; }
    public string? ApplicationData { get; set; }
    public string? StorePackageFamily { get; set; }
    public string? InstalledPackagesPath { get; set; }
    public string? CommunityFolder { get; set; }
    public string? Community2024Folder { get; set; }
    public string? VfsProjectionRoot { get; set; }
    public string? UserCfgPath { get; set; }
    public string? FlightPlanPath { get; set; }
    public string? MsfsExecutablePath { get; set; }
    public List<string>? AdditionalAircraftSearchRoots { get; set; }

    public string GetLocalAppData() => LocalAppData ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
    public string GetApplicationData() => ApplicationData ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
    public string GetStorePackageFamily() => StorePackageFamily ?? MsfsInstallLayout.StorePackageFamily;

    public static SimulatorPathsConfig LoadFromFile(string path)
    {
        var config = new SimulatorPathsConfig();
        try
        {
            if (!File.Exists(path)) return config;
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
            if (doc is null) return config;
            string? S(string k) => doc.TryGetValue(k, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null;
            config.LocalAppData = S("localAppData");
            config.ApplicationData = S("applicationData");
            config.StorePackageFamily = S("storePackageFamily");
            config.InstalledPackagesPath = S("installedPackagesPath");
            config.CommunityFolder = S("communityFolder");
            config.Community2024Folder = S("community2024Folder");
            config.VfsProjectionRoot = S("vfsProjectionRoot");
            config.UserCfgPath = S("userCfgPath");
            config.FlightPlanPath = S("flightPlanPath");
            config.MsfsExecutablePath = S("msfsExecutablePath");
        }
        catch { }
        return config;
    }

    public void SaveToFile(string path)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

/// <summary>
/// Resolves the MSFS 2024 install layout: package roots come from UserCfg.opt's
/// InstalledPackagesPath; the runtime content tree from the Store package's
/// LocalState VFS projection.
/// </summary>
public sealed class MsfsInstallLayout
{
    public const string StorePackageFamily = "Microsoft.Limitless_8wekyb3d8bbwe";

    private readonly string _localAppData;
    private readonly string _applicationData;
    private readonly string _storePackageFamily;
    private readonly SimulatorPathsConfig? _config;

    public string StorePackageRoot { get; }
    public string VfsProjectionRoot { get; }
    public string? UserCfgPath { get; }
    public string? InstalledPackagesPath { get; }
    public string? CommunityFolder { get; }
    public string? Community2024Folder { get; }
    public string? Official2024Folder { get; }
    public string? WasmCommunityFolder { get; }
    public string? WasmOfficialFolder { get; }
    public string? LegacyCommunityFolder { get; }
    public string? LegacyOfficialFolder { get; }
    public IReadOnlyList<string> PackageRoots { get; }
    public IReadOnlyList<string> AircraftSearchRoots { get; }

    private MsfsInstallLayout(
        string localAppData, string applicationData, string storePackageFamily,
        string storePackageRoot, string vfsProjectionRoot,
        string? userCfgPath, string? installedPackagesPath,
        string? communityFolder, string? community2024Folder,
        string? official2024Folder, string? wasmCommunityFolder, string? wasmOfficialFolder,
        string? legacyCommunityFolder, string? legacyOfficialFolder,
        IReadOnlyList<string> packageRoots, IReadOnlyList<string> aircraftSearchRoots,
        SimulatorPathsConfig? config)
    {
        _localAppData = localAppData;
        _applicationData = applicationData;
        _storePackageFamily = storePackageFamily;
        _config = config;
        StorePackageRoot = storePackageRoot;
        VfsProjectionRoot = vfsProjectionRoot;
        UserCfgPath = userCfgPath;
        InstalledPackagesPath = installedPackagesPath;
        CommunityFolder = communityFolder;
        Community2024Folder = community2024Folder;
        Official2024Folder = official2024Folder;
        WasmCommunityFolder = wasmCommunityFolder;
        WasmOfficialFolder = wasmOfficialFolder;
        LegacyCommunityFolder = legacyCommunityFolder;
        LegacyOfficialFolder = legacyOfficialFolder;
        PackageRoots = packageRoots;
        AircraftSearchRoots = aircraftSearchRoots;
    }

    public static MsfsInstallLayout Discover() => Discover(new SimulatorPathsConfig());

    public static MsfsInstallLayout Discover(SimulatorPathsConfig config)
    {
        var localAppData = config.GetLocalAppData();
        var applicationData = config.GetApplicationData();
        var storePackageFamily = config.GetStorePackageFamily();
        var storePackageRoot = Path.Combine(localAppData, "Packages", storePackageFamily);
        var vfsProjectionRoot = config.VfsProjectionRoot ?? Path.Combine(storePackageRoot, "LocalState", "VFSProjection");

        string? userCfgPath = config.UserCfgPath;
        if (string.IsNullOrWhiteSpace(userCfgPath))
            userCfgPath = FindFirstExistingFile(DefaultUserCfgCandidates(localAppData, applicationData, storePackageFamily));

        var installedPackagesPath = config.InstalledPackagesPath;
        if (string.IsNullOrWhiteSpace(installedPackagesPath) && !string.IsNullOrEmpty(userCfgPath))
            installedPackagesPath = ParseInstalledPackagesPath(userCfgPath);
        if (string.IsNullOrWhiteSpace(installedPackagesPath))
        {
            var fallback = Path.Combine(storePackageRoot, "LocalCache", "Packages");
            if (Directory.Exists(fallback)) installedPackagesPath = fallback;
        }

        var communityFolder = config.CommunityFolder ?? CombineIfRootExists(installedPackagesPath, "Community");
        var community2024Folder = config.Community2024Folder ?? CombineIfRootExists(installedPackagesPath, "Community2024");
        var official2024Folder = CombineIfRootExists(installedPackagesPath, "Official2024");
        var wasmCommunityFolder = Path.Combine(storePackageRoot, "LocalState", "WASM", "Community");
        var wasmOfficialFolder = Path.Combine(storePackageRoot, "LocalState", "WASM", "Official");
        var legacyCommunityFolder = Path.Combine(localAppData, "MSFS2024", "Community");
        var legacyOfficialFolder = Path.Combine(localAppData, "MSFS2024", "Official");

        var packageRoots = ExistingDirectories(community2024Folder, communityFolder, official2024Folder, legacyCommunityFolder, legacyOfficialFolder);

        var aircraftSearchRoots = ExistingDirectories(
            Path.Combine(vfsProjectionRoot, "simobjects", "airplanes"),
            Path.Combine(vfsProjectionRoot, "simobjects", "groundvehicles"),
            Path.Combine(vfsProjectionRoot, "simobjects", "vehicles"),
            vfsProjectionRoot,
            community2024Folder, communityFolder, official2024Folder,
            wasmCommunityFolder, wasmOfficialFolder, legacyCommunityFolder, legacyOfficialFolder);

        if (config.AdditionalAircraftSearchRoots != null)
        {
            var additional = new List<string>(aircraftSearchRoots);
            foreach (var root in config.AdditionalAircraftSearchRoots)
                if (Directory.Exists(root) && !additional.Contains(root, StringComparer.OrdinalIgnoreCase))
                    additional.Add(root);
            aircraftSearchRoots = additional;
        }

        return new MsfsInstallLayout(localAppData, applicationData, storePackageFamily, storePackageRoot,
            vfsProjectionRoot, userCfgPath, installedPackagesPath, communityFolder, community2024Folder,
            official2024Folder, wasmCommunityFolder, wasmOfficialFolder, legacyCommunityFolder,
            legacyOfficialFolder, packageRoots, aircraftSearchRoots, config);
    }

    public string? FindLatestFlightPlan()
    {
        var candidates = new[]
        {
            _config?.FlightPlanPath,
            Path.Combine(_applicationData, "Microsoft Flight Simulator 2024", "LocalState", "CUSTOMFLIGHT.PLN"),
            Path.Combine(StorePackageRoot, "LocalState", "CUSTOMFLIGHT.PLN"),
            Path.Combine(_localAppData, "Packages", _storePackageFamily, "LocalState", "CUSTOMFLIGHT.PLN"),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;
        return null;
    }

    internal static IEnumerable<string> DefaultUserCfgCandidates(string localAppData, string applicationData, string storePackageFamily)
    {
        yield return Path.Combine(applicationData, "Microsoft Flight Simulator 2024", "UserCfg.opt");
        yield return Path.Combine(localAppData, "Packages", storePackageFamily, "LocalCache", "UserCfg.opt");
        yield return Path.Combine(localAppData, "Packages", storePackageFamily, "LocalState", "UserCfg.opt");
    }

    internal static string? ParseInstalledPackagesPath(string userCfgPath)
    {
        try
        {
            foreach (var line in File.ReadLines(userCfgPath))
            {
                var t = line.Trim();
                if (!t.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
                var q1 = t.IndexOf('"');
                if (q1 >= 0)
                {
                    var q2 = t.IndexOf('"', q1 + 1);
                    if (q2 > q1) return t.Substring(q1 + 1, q2 - q1 - 1);
                }
                var parts = t.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) return parts[1].Trim().Trim('"');
            }
        }
        catch { }
        return null;
    }

    private static string? FindFirstExistingFile(IEnumerable<string> candidates)
    {
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    private static string? CombineIfRootExists(string? root, string child)
        => string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, child);

    private static IReadOnlyList<string> ExistingDirectories(params string?[] candidates)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c) && seen.Add(c))
                roots.Add(c);
        return roots;
    }
}
