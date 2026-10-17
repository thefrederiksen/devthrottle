using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The two rules every systemd user unit we write keeps (issue devthrottle_internal#2475): never a command
/// that is only the runtime host, and a start limit so a unit that dies at once stops being restarted.
/// The live case was a launcher unit running <c>"/home/soren/.dotnet/dotnet"</c> alone, restarted 29,190 times.
/// </summary>
public sealed class SystemdUnitSafetyTests
{
    [Theory]
    [InlineData("\"/home/soren/.dotnet/dotnet\"")]
    [InlineData("/usr/bin/dotnet")]
    [InlineData("dotnet")]
    [InlineData("  \"/opt/dotnet/dotnet\"  ")]
    public void IsBareRuntimeHost_HostAlone_IsTrue(string execStart)
    {
        Assert.True(SystemdUnitSafety.IsBareRuntimeHost(execStart));
    }

    [Theory]
    [InlineData("\"/home/soren/.dotnet/dotnet\" /home/soren/.local/share/cc-director/launcher/cc-launcher.dll")]
    [InlineData("\"/home/soren/.local/share/cc-director/launcher/cc-launcher\" --managed")]
    [InlineData("\"/home/soren/.local/share/cc-director/launcher/cc-launcher\"")]
    [InlineData("%h/.local/share/cc-director/app/cc-director")]
    [InlineData("\"/opt/dotnet-tools/mydotnet\"")]
    [InlineData("")]
    [InlineData(null)]
    public void IsBareRuntimeHost_RealProgram_IsFalse(string? execStart)
    {
        Assert.False(SystemdUnitSafety.IsBareRuntimeHost(execStart));
    }

    [Fact]
    public void RequireRunnableCommand_HostWithNoProgram_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SystemdUnitSafety.RequireRunnableCommand("/home/soren/.dotnet/dotnet", null));

        Assert.Contains("only the runtime host", ex.Message);
    }

    [Fact]
    public void RequireRunnableCommand_HostWithProgram_DoesNotThrow()
    {
        SystemdUnitSafety.RequireRunnableCommand("/home/soren/.dotnet/dotnet", "/opt/app/app.dll");
    }

    [Fact]
    public void GatewayUnitContent_BareRuntimeHost_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => GatewaySystemdAutostart.UnitContent("/usr/bin/dotnet", null));
    }

    [Fact]
    public void GatewayUnitContent_CarriesTheStartLimitInTheUnitSection()
    {
        var unit = GatewaySystemdAutostart.UnitContent("/opt/gw/devthrottle-gateway", "--managed");

        var unitSection = unit[..unit.IndexOf("[Service]", StringComparison.Ordinal)];
        Assert.Contains("StartLimitIntervalSec=600\n", unitSection);
        Assert.Contains("StartLimitBurst=5\n", unitSection);
    }

    [Fact]
    public void GatewayUnitContent_IsLfOnly_WhateverTheCheckoutsLineEndings()
    {
        var unit = GatewaySystemdAutostart.UnitContent("/opt/gw/devthrottle-gateway", "--managed");

        Assert.DoesNotContain("\r", unit);
    }

    [Fact]
    public void Tokens_QuotedPathWithSpaces_StaysOneWord()
    {
        Assert.Equal(new[] { "/home/t/My Apps/dotnet", "app.dll" },
            SystemdUnitSafety.Tokens("\"/home/t/My Apps/dotnet\" app.dll"));
    }
}
