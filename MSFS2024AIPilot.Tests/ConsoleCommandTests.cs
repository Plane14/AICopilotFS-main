using System;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The console's command arguments. The reload form is the one worth pinning: the reload
/// word is consumed from the argument before the ICAO is used, because a call that passed
/// the un-stripped argument and evaluated the strip in the same expression sent the
/// simulator an airport called "RELOAD KJFK".
/// </summary>
public class ConsoleCommandTests
{
    [Fact]
    public void AReloadArgumentIsStrippedBeforeTheIcaoIsUsed()
    {
        var arg = "reload KJFK";

        Assert.True(AirportCommandArguments.IsReloadCommand(ref arg));
        Assert.Equal("KJFK", arg);
    }

    [Fact]
    public void AReloadArgumentIsCaseInsensitive()
    {
        var arg = "Reload egll";

        Assert.True(AirportCommandArguments.IsReloadCommand(ref arg));
        Assert.Equal("egll", arg);
    }

    [Theory]
    [InlineData("")]
    [InlineData("KJFK")]
    [InlineData("reload")]
    [InlineData("refresh KJFK")]
    [InlineData("reloadall KJFK")]
    public void AnythingElseIsNotAReload(string text)
    {
        var arg = text;

        Assert.False(AirportCommandArguments.IsReloadCommand(ref arg));
        // The argument is left exactly as it was given.
        Assert.Equal(text, arg);
    }
}
