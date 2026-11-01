using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Configuration for MSFS 2024 simulator paths. All paths are optional; when not specified,
/// the default discovery logic from <see cref="MsfsInstallLayout"/> is used.
/// </summary>
public sealed class SimulatorPathsConfig
{
    /// <summary>
    /// The LocalApplicationData folder (typically %LOCALAPPDATA%). If not set, uses
    /// Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).
    /// </summary>
    [JsonPropertyName("localAppData")]
    public string? LocalAppData { get; set; }

    /// <summary>
    /// The ApplicationData folder (typically %APPDATA%). If not set, uses
    /// Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).
    /// </summary>
    [JsonPropertyName("applicationData")]
    public string? ApplicationData { get; set; }

    /// <summary>
    /// The Microsoft Store package family name. Defaults to "Microsoft.Limitless_8wekyb3d8bbwe".
    /// </summary>
    [JsonPropertyName("storePackageFamily")]
    public string? StorePackageFamily { get; set; }

    /// <summary>
    /// Explicit path to the installed packages root (from UserCfg.opt InstalledPackagesPath).
    /// When set, this overrides the automatic discovery from UserCfg.opt.
    /// </summary>
    [JsonPropertyName("installedPackagesPath")]
    public string? InstalledPackagesPath { get; set; }

    /// <summary>
    /// Explicit path to the Community folder for add-ons.
    /// </summary>
    [JsonPropertyName("communityFolder")]
    public string? CommunityFolder { get; set; }

    /// <summary>
    /// Explicit path to the Community2024 folder for MSFS 2024 add-ons.
    /// </summary>
    [JsonPropertyName("community2024Folder")]
    public string? Community2024Folder { get; set; }

    /// <summary>
    /// Explicit path to the VFS projection root (simobjects, etc.).
    /// </summary>
    [JsonPropertyName("vfsProjectionRoot")]
    public string? VfsProjectionRoot { get; set; }

    /// <summary>
    /// Explicit path to the UserCfg.opt file.
    /// </summary>
    [JsonPropertyName("userCfgPath")]
    public string? UserCfgPath { get; set; }

    /// <summary>
    /// Explicit path to the flight plan file (CUSTOMFLIGHT.PLN).
    /// </summary>
    [JsonPropertyName("flightPlanPath")]
    public string? FlightPlanPath { get; set; }

    /// <summary>
    /// Explicit path to the MSFS 2024 executable (for panel building).
    /// </summary>
    [JsonPropertyName("msfsExecutablePath")]
    public string? MsfsExecutablePath { get; set; }

    /// <summary>
    /// Explicit path to the MSFS SDK directory (for panel building).
    /// </summary>
    [JsonPropertyName("msfsSdkPath")]
    public string? MsfsSdkPath { get; set; }

    /// <summary>
    /// Additional aircraft search roots to include.
    /// </summary>
    [JsonPropertyName("additionalAircraftSearchRoots")]
    public List<string>? AdditionalAircraftSearchRoots { get; set; }

    /// <summary>
    /// Gets the effective LocalAppData path, falling back to the system default.
    /// </summary>
    public string GetLocalAppData() => LocalAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Gets the effective ApplicationData path, falling back to the system default.
    /// </summary>
    public string GetApplicationData() => ApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// Gets the effective StorePackageFamily, falling back to the default.
    /// </summary>
    public string GetStorePackageFamily() => StorePackageFamily ?? MsfsInstallLayout.StorePackageFamily;

    /// <summary>
    /// Loads configuration from a JSON file.
    /// </summary>
    public static SimulatorPathsConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return new SimulatorPathsConfig();
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        return JsonSerializer.Deserialize<SimulatorPathsConfig>(json, options) ?? new SimulatorPathsConfig();
    }

    /// <summary>
    /// Saves configuration to a JSON file (for generating a template).
    /// </summary>
    public void SaveToFile(string path)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Creates a default configuration template with comments.
    /// </summary>
    public static string GetDefaultConfigTemplate()
    {
        return @"{
  // MSFS 2024 Simulator Paths Configuration
  // All paths are optional - defaults will be auto-discovered when not specified.
  
  // LocalApplicationData folder (typically %LOCALAPPDATA%)
  ""localAppData"": null,
  
  // ApplicationData folder (typically %APPDATA%)
  ""applicationData"": null,
  
  // Microsoft Store package family name (default: Microsoft.Limitless_8wekyb3d8bbwe)
  ""storePackageFamily"": null,
  
  // Explicit installed packages root (overrides UserCfg.opt discovery)
  ""installedPackagesPath"": null,
  
  // Explicit Community folder path
  ""communityFolder"": null,
  
  // Explicit Community2024 folder path (MSFS 2024 specific)
  ""community2024Folder"": null,
  
  // Explicit VFS projection root (simobjects, etc.)
  ""vfsProjectionRoot"": null,
  
  // Explicit UserCfg.opt path
  ""userCfgPath"": null,
  
  // Explicit flight plan path (CUSTOMFLIGHT.PLN)
  ""flightPlanPath"": null,
  
  // MSFS 2024 executable path (for panel building)
  ""msfsExecutablePath"": null,
  
  // MSFS SDK path (for panel building)
  ""msfsSdkPath"": null,
  
  // Additional aircraft search roots
  ""additionalAircraftSearchRoots"": []
}";
    }
}