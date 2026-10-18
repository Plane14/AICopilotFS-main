using System;
using System.IO;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

public class MsfsInstallLayoutTests
{
    [Fact]
    public void ParseInstalledPackagesPath_ReadsTheQuotedUserCfgValue()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "usercfg-" + Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            var userCfg = Path.Combine(dir, "UserCfg.opt");
            File.WriteAllText(userCfg, """
                SomeOtherSetting 1
                InstalledPackagesPath "D:\MSFS Packages"
                """);

            Assert.Equal(@"D:\MSFS Packages", MsfsInstallLayout.ParseInstalledPackagesPath(userCfg));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Discover_UsesInstalledPackagesPathAndPrefersCommunity2024()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "layout-" + Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            var localAppData = Path.Combine(root, "Local");
            var appData = Path.Combine(root, "Roaming");
            var storeRoot = Path.Combine(localAppData, "Packages", MsfsInstallLayout.StorePackageFamily);
            var packagesRoot = Path.Combine(root, "PackagesRoot");
            var vfsAirplanes = Path.Combine(storeRoot, "LocalState", "VFSProjection", "simobjects", "airplanes");

            Directory.CreateDirectory(vfsAirplanes);
            Directory.CreateDirectory(Path.Combine(storeRoot, "LocalCache"));
            Directory.CreateDirectory(Path.Combine(packagesRoot, "Community"));
            Directory.CreateDirectory(Path.Combine(packagesRoot, "Community2024"));
            Directory.CreateDirectory(Path.Combine(packagesRoot, "Official2020"));
            Directory.CreateDirectory(Path.Combine(packagesRoot, "Official2024"));

            var userCfg = Path.Combine(storeRoot, "LocalCache", "UserCfg.opt");
            File.WriteAllText(userCfg, $"InstalledPackagesPath \"{packagesRoot}\"");

            var layout = MsfsInstallLayout.Discover(localAppData, appData);

            Assert.Equal(userCfg, layout.UserCfgPath);
            Assert.Equal(packagesRoot, layout.InstalledPackagesPath);
            Assert.Equal(Path.Combine(packagesRoot, "Community2024"), layout.PreferredCommunityInstallFolder());
            Assert.Equal(vfsAirplanes, layout.AircraftSearchRoots.First());
            Assert.Contains(Path.Combine(packagesRoot, "Community2024"), layout.PackageRoots);
            Assert.Contains(Path.Combine(packagesRoot, "Official2024"), layout.PackageRoots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindLatestFlightPlan_PrefersRoamingCopyBeforeStoreLocalState()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "plans-" + Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            var localAppData = Path.Combine(root, "Local");
            var appData = Path.Combine(root, "Roaming");
            var roamingFlight = Path.Combine(appData, "Microsoft Flight Simulator 2024", "LocalState", "CUSTOMFLIGHT.PLN");
            var storeFlight = Path.Combine(localAppData, "Packages", MsfsInstallLayout.StorePackageFamily, "LocalState", "CUSTOMFLIGHT.PLN");

            Directory.CreateDirectory(Path.GetDirectoryName(roamingFlight)!);
            Directory.CreateDirectory(Path.GetDirectoryName(storeFlight)!);
            File.WriteAllText(storeFlight, "<SimBase.Document />");
            File.WriteAllText(roamingFlight, "<SimBase.Document />");

            var layout = MsfsInstallLayout.Discover(localAppData, appData);

            Assert.Equal(roamingFlight, layout.FindLatestFlightPlan());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
