using System;
using System.Collections.Generic;
using System.IO;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Resolves the MSFS 2024 install layout from the locations documented by the SDK:
/// the active package root comes from UserCfg.opt's InstalledPackagesPath, while the
/// runtime-resolved content tree comes from the Store package's LocalState VFS projection.
/// </summary>
internal sealed class MsfsInstallLayout
{
    internal const string StorePackageFamily = "Microsoft.Limitless_8wekyb3d8bbwe";

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
    public string? Official2020Folder { get; }
    public string? Official2024Folder { get; }
    public string? WasmCommunityFolder { get; }
    public string? WasmOfficialFolder { get; }
    public string? LegacyCommunityFolder { get; }
    public string? LegacyOfficialFolder { get; }
    public IReadOnlyList<string> PackageRoots { get; }
    public IReadOnlyList<string> AircraftSearchRoots { get; }

    private MsfsInstallLayout(
        string localAppData,
        string applicationData,
        string storePackageFamily,
        string storePackageRoot,
        string vfsProjectionRoot,
        string? userCfgPath,
        string? installedPackagesPath,
        string? communityFolder,
        string? community2024Folder,
        string? official2020Folder,
        string? official2024Folder,
        string? wasmCommunityFolder,
        string? wasmOfficialFolder,
        string? legacyCommunityFolder,
        string? legacyOfficialFolder,
        IReadOnlyList<string> packageRoots,
        IReadOnlyList<string> aircraftSearchRoots,
        SimulatorPathsConfig? config = null)
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
        Official2020Folder = official2020Folder;
        Official2024Folder = official2024Folder;
        WasmCommunityFolder = wasmCommunityFolder;
        WasmOfficialFolder = wasmOfficialFolder;
        LegacyCommunityFolder = legacyCommunityFolder;
        LegacyOfficialFolder = legacyOfficialFolder;
        PackageRoots = packageRoots;
        AircraftSearchRoots = aircraftSearchRoots;
    }

    public static MsfsInstallLayout Discover()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Discover(localAppData, applicationData);
    }

    public static MsfsInstallLayout Discover(SimulatorPathsConfig config)
    {
        var localAppData = config.GetLocalAppData();
        var applicationData = config.GetApplicationData();
        return Discover(localAppData, applicationData, config);
    }

    internal static MsfsInstallLayout Discover(string localAppData, string applicationData)
    {
        return Discover(localAppData, applicationData, null);
    }

    internal static MsfsInstallLayout Discover(string localAppData, string applicationData, SimulatorPathsConfig? config)
    {
        var storePackageFamily = config?.GetStorePackageFamily() ?? StorePackageFamily;
        var storePackageRoot = Path.Combine(localAppData, "Packages", storePackageFamily);

        var vfsProjectionRoot = config?.VfsProjectionRoot ?? Path.Combine(storePackageRoot, "LocalState", "VFSProjection");

        string? userCfgPath;
        if (!string.IsNullOrWhiteSpace(config?.UserCfgPath))
        {
            userCfgPath = config.UserCfgPath;
        }
        else
        {
            userCfgPath = FindFirstExistingFile(DefaultUserCfgCandidates(localAppData, applicationData, storePackageFamily));
        }

        var installedPackagesPath = config?.InstalledPackagesPath;
        if (string.IsNullOrWhiteSpace(installedPackagesPath) && !string.IsNullOrEmpty(userCfgPath))
        {
            installedPackagesPath = ParseInstalledPackagesPath(userCfgPath);
        }

        if (string.IsNullOrWhiteSpace(installedPackagesPath))
        {
            var fallbackPackages = Path.Combine(storePackageRoot, "LocalCache", "Packages");
            if (Directory.Exists(fallbackPackages))
                installedPackagesPath = fallbackPackages;
        }

        var communityFolder = config?.CommunityFolder ?? CombineIfRootExists(installedPackagesPath, "Community");
        var community2024Folder = config?.Community2024Folder ?? CombineIfRootExists(installedPackagesPath, "Community2024");
        var official2020Folder = CombineIfRootExists(installedPackagesPath, "Official2020");
        var official2024Folder = CombineIfRootExists(installedPackagesPath, "Official2024");
        var wasmCommunityFolder = Path.Combine(storePackageRoot, "LocalState", "WASM", "Community");
        var wasmOfficialFolder = Path.Combine(storePackageRoot, "LocalState", "WASM", "Official");
        var legacyCommunityFolder = Path.Combine(localAppData, "MSFS2024", "Community");
        var legacyOfficialFolder = Path.Combine(localAppData, "MSFS2024", "Official");

        var packageRoots = ExistingDirectories(
            community2024Folder,
            communityFolder,
            official2024Folder,
            official2020Folder,
            legacyCommunityFolder,
            legacyOfficialFolder);

        var aircraftSearchRoots = ExistingDirectories(
            Path.Combine(vfsProjectionRoot, "simobjects", "airplanes"),
            Path.Combine(vfsProjectionRoot, "simobjects", "groundvehicles"),
            Path.Combine(vfsProjectionRoot, "simobjects", "vehicles"),
            vfsProjectionRoot,
            community2024Folder,
            communityFolder,
            official2024Folder,
            official2020Folder,
            wasmCommunityFolder,
            wasmOfficialFolder,
            legacyCommunityFolder,
            legacyOfficialFolder);

        if (config?.AdditionalAircraftSearchRoots != null)
        {
            var additional = new List<string>(aircraftSearchRoots);
            foreach (var root in config.AdditionalAircraftSearchRoots)
            {
                if (Directory.Exists(root) && !additional.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    additional.Add(root);
                }
            }
            aircraftSearchRoots = additional;
        }

        return new MsfsInstallLayout(
            localAppData,
            applicationData,
            storePackageFamily,
            storePackageRoot,
            vfsProjectionRoot,
            userCfgPath,
            installedPackagesPath,
            communityFolder,
            community2024Folder,
            official2020Folder,
            official2024Folder,
            wasmCommunityFolder,
            wasmOfficialFolder,
            legacyCommunityFolder,
            legacyOfficialFolder,
            packageRoots,
            aircraftSearchRoots,
            config);
    }

    public string? PreferredCommunityInstallFolder()
    {
        var preferred = FirstExistingDirectory(Community2024Folder, CommunityFolder);
        if (!string.IsNullOrEmpty(preferred))
            return preferred;

        if (!string.IsNullOrWhiteSpace(InstalledPackagesPath))
            return Path.Combine(InstalledPackagesPath, "Community2024");

        var fallbackPackages = Path.Combine(StorePackageRoot, "LocalCache", "Packages");
        return Directory.Exists(fallbackPackages)
            ? Path.Combine(fallbackPackages, "Community2024")
            : null;
    }

    public string? FindLatestFlightPlan()
    {
        foreach (var candidate in EnumerateFlightPlanCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    internal IEnumerable<string> EnumerateFlightPlanCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_config?.FlightPlanPath))
        {
            yield return _config.FlightPlanPath;
        }

        yield return Path.Combine(_applicationData, "Microsoft Flight Simulator 2024", "LocalState", "CUSTOMFLIGHT.PLN");
        yield return Path.Combine(StorePackageRoot, "LocalState", "CUSTOMFLIGHT.PLN");
        yield return Path.Combine(_localAppData, "Packages", _storePackageFamily, "LocalState", "CUSTOMFLIGHT.PLN");
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
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase))
                    continue;

                var firstQuote = trimmed.IndexOf('"');
                if (firstQuote >= 0)
                {
                    var secondQuote = trimmed.IndexOf('"', firstQuote + 1);
                    if (secondQuote > firstQuote)
                        return trimmed.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                }

                var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                    return parts[1].Trim().Trim('"');
            }
        }
        catch
        {
            // The runtime falls back to the conventional LocalCache path when UserCfg.opt
            // is unreadable or absent.
        }

        return null;
    }

    private static string? FindFirstExistingFile(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? CombineIfRootExists(string? root, string child)
        => string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, child);

    private static string? FirstExistingDirectory(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static IReadOnlyList<string> ExistingDirectories(params string?[] candidates)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                continue;

            if (seen.Add(candidate))
                roots.Add(candidate);
        }

        return roots;
    }
}
