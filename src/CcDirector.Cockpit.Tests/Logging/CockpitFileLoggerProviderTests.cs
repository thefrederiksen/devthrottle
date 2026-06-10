using CcDirector.Cockpit.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Cockpit.Tests.Logging;

// =====================================================================================
// CockpitFileLoggerProvider.RenderLine - the exact persisted text format the file sink writes
// ("LEVEL [ShortCategory] message", with the exception appended). These tests pin the contract
// the issue #199 proof relies on: action-logging lines (scope B), the blank-terminal WARNING
// (scope C / AC4), and exception rendering. They call the PRODUCTION renderer directly (no
// mirror), so any drift in the format is caught here.
// =====================================================================================
public sealed class CockpitFileLoggerProviderTests
{
    [Fact]
    public void RenderLine_Information_UsesInfoTagAndShortCategory()
    {
        var line = CockpitFileLoggerProvider.CockpitFileLogger.RenderLine(
            "CcDirector.Cockpit.Components.Pages.Cockpit",
            LogLevel.Information,
            "Cockpit: select session sid=abc source=rail director=http://host:7887",
            exception: null);

        Assert.Equal("INFO [Cockpit] Cockpit: select session sid=abc source=rail director=http://host:7887", line);
    }

    [Fact]
    public void RenderLine_BlankDirectorBaseWarning_UsesWarnTagAndCarriesState()
    {
        // The AC4 line the TerminalPane emits when DirectorBase is empty (blank-terminal failure
        // mode): it must persist as a WARNING carrying the session state.
        var line = CockpitFileLoggerProvider.CockpitFileLogger.RenderLine(
            "CcDirector.Cockpit.Components.TerminalPane",
            LogLevel.Warning,
            "TerminalPane empty DirectorBase sid=abc state=WaitingForInput - keystrokes disabled (no reachable Director endpoint)",
            exception: null);

        Assert.StartsWith("WARN [TerminalPane] TerminalPane empty DirectorBase sid=abc state=WaitingForInput", line);
    }

    [Fact]
    public void RenderLine_WithException_AppendsTypeAndMessage()
    {
        var line = CockpitFileLoggerProvider.CockpitFileLogger.RenderLine(
            "CcDirector.Cockpit.Components.TerminalPane",
            LogLevel.Error,
            "TerminalPane connect failed for sid=abc",
            new InvalidOperationException("boom"));

        Assert.Equal("ERROR [TerminalPane] TerminalPane connect failed for sid=abc :: InvalidOperationException: boom", line);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRACE")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRIT")]
    public void RenderLine_MapsEveryLevelToItsTag(LogLevel level, string expectedTag)
    {
        var line = CockpitFileLoggerProvider.CockpitFileLogger.RenderLine("X.Y", level, "msg", null);
        Assert.StartsWith(expectedTag + " [Y] msg", line);
    }
}
