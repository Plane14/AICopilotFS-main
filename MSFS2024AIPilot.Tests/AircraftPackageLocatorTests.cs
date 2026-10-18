using System;
using System.IO;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

public class AircraftPackageLocatorTests
{
    private static string VfsAirplanesRoot()
    {
        var layout = MsfsInstallLayout.Discover();
        return Path.Combine(layout.VfsProjectionRoot, "simobjects", "airplanes");
    }

    [Fact]
    public void LocateAircraftDirectory_FindsAircraftInVfsProjectionAirplanes()
    {
        var title = "Test Aircraft " + Guid.NewGuid().ToString("N");
        var packageDir = Path.Combine(VfsAirplanesRoot(), "TestAircraft_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "aircraft.cfg"), $"[FLTSIM.0]\nTitle = \"{title}\"\n");

        try
        {
            var found = AircraftPackageLocator.LocateAircraftDirectory(title);
            Assert.NotNull(found);
            Assert.Equal(packageDir, found);
        }
        finally
        {
            if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);
        }
    }

    [Fact]
    public void LocateAircraftDirectory_AcceptsFilePathRootOverride()
    {
        var packageDir = Path.Combine(VfsAirplanesRoot(), "TestAircraft2_" + Guid.NewGuid().ToString("N"));
        var title = "Another Test Aircraft " + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(packageDir);
        var cfgPath = Path.Combine(packageDir, "aircraft.cfg");
        File.WriteAllText(cfgPath, $"[FLTSIM.0]\nTitle = \"{title}\"\n");

        try
        {
            var found = AircraftPackageLocator.LocateAircraftDirectory(title, cfgPath);
            Assert.NotNull(found);
            Assert.Equal(packageDir, found);
        }
        finally
        {
            if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);
        }
    }

    [Fact]
    public void LocateAircraftDirectory_PrefersVfsProjectionOverLocalCachePackageMirror()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var slug = "TestAircraft3_" + Guid.NewGuid().ToString("N");
        var title = "VFS Priority " + Guid.NewGuid().ToString("N");
        var localCacheDir = Path.Combine(
            localAppData,
            "Packages",
            "Microsoft.Limitless_8wekyb3d8bbwe",
            "LocalCache",
            "Packages",
            "Community",
            slug);
        var vfsDir = Path.Combine(VfsAirplanesRoot(), slug);
        var localCacheCfg = Path.Combine(localCacheDir, "aircraft.cfg");
        var vfsCfg = Path.Combine(vfsDir, "aircraft.cfg");

        Directory.CreateDirectory(localCacheDir);
        Directory.CreateDirectory(vfsDir);
        File.WriteAllText(localCacheCfg, $"[FLTSIM.0]\nTitle = \"{title}\"\n");
        File.WriteAllText(vfsCfg, $"[FLTSIM.0]\nTitle = \"{title}\"\n");

        try
        {
            var found = AircraftPackageLocator.LocateAircraftDirectory(title);
            Assert.NotNull(found);
            Assert.Equal(vfsDir, found);
        }
        finally
        {
            if (Directory.Exists(localCacheDir)) Directory.Delete(localCacheDir, recursive: true);
            if (Directory.Exists(vfsDir)) Directory.Delete(vfsDir, recursive: true);
        }
    }

    [Fact]
    public void LocateAircraftDirectory_EmptyTitleDoesNotGuessFirstAircraft()
    {
        var packageDir = Path.Combine(VfsAirplanesRoot(), "TestAircraft4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "aircraft.cfg"), "[FLTSIM.0]\nTitle = \"Some Aircraft\"\n");

        try
        {
            var found = AircraftPackageLocator.LocateAircraftDirectory(string.Empty);
            Assert.Null(found);
        }
        finally
        {
            if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);
        }
    }

    [Fact]
    public void LocateAircraftDirectory_UsesRootOverrideWhenTitleIsEmpty()
    {
        var packageDir = Path.Combine(VfsAirplanesRoot(), "TestAircraft5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "aircraft.cfg"), "[FLTSIM.0]\nTitle = \"Some Aircraft\"\n");

        try
        {
            var found = AircraftPackageLocator.LocateAircraftDirectory(string.Empty, packageDir);
            Assert.NotNull(found);
            Assert.Equal(packageDir, found);
        }
        finally
        {
            if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);
        }
    }

    [Fact]
    public void LocateAircraftDirectory_SearchesInsideRootOverrideContainer()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "locator-root-" + Guid.NewGuid().ToString("N"))).FullName;
        var title = "Override Root Aircraft " + Guid.NewGuid().ToString("N");
        var packageDir = Path.Combine(root, "slugged-aircraft");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "aircraft.cfg"), $"[FLTSIM.0]\nTitle = \"{title}\"\n");

        try
        {
            var found = AircraftPackageLocator.LocateAircraftDirectory(title, root);
            Assert.NotNull(found);
            Assert.Equal(packageDir, found);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LocateAircraftDirectory_IgnoresNonAircraftRootOverride()
    {
        var packageDir = Path.Combine(VfsAirplanesRoot(), "TrafficHelper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDir);

        try
        {
            var found = AircraftPackageLocator.LocateAircraftDirectory("Any Aircraft", packageDir);
            Assert.Null(found);
        }
        finally
        {
            if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);
        }
    }
}
