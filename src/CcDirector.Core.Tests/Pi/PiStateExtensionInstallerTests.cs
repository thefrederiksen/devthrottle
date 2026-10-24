using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Pi;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

public sealed partial class PiStateExtensionInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pi-state-extension-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void EnsureInstalled_NewDirectory_WritesTheEmbeddedExtension()
    {
        var path = PiStateExtensionInstaller.EnsureInstalled(_directory);

        Assert.Equal(Path.Combine(_directory, PiStateExtensionInstaller.ExtensionFileName), path);
        Assert.Equal(PiStateExtensionInstaller.ExtensionSource(), File.ReadAllText(path!));
    }

    [Fact]
    public void EnsureInstalled_ExistingCurrentExtension_LeavesItsWriteTimeUnchanged()
    {
        var path = PiStateExtensionInstaller.EnsureInstalled(_directory)!;
        var timestamp = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);

        var second = PiStateExtensionInstaller.EnsureInstalled(_directory);

        Assert.Equal(path, second);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void EnsureInstalled_ChangedExtension_ReplacesItWithTheCarriedSource()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, PiStateExtensionInstaller.ExtensionFileName);
        File.WriteAllText(path, "old extension");

        Assert.Equal(path, PiStateExtensionInstaller.EnsureInstalled(_directory));
        Assert.Equal(PiStateExtensionInstaller.ExtensionSource(), File.ReadAllText(path));
    }

    [Fact]
    public void AppendLaunchArgument_InstalledExtension_AddsTheExplicitPiFlag()
    {
        var result = PiStateExtensionInstaller.AppendLaunchArgument("--session-id abc", "/tmp/state reporter.ts");

        Assert.Equal("--session-id abc --extension \"/tmp/state reporter.ts\"", result);
    }

    [Fact]
    public void AppendLaunchArgument_FailedInstall_LeavesArgumentsUnchanged()
    {
        Assert.Equal("--session-id abc", PiStateExtensionInstaller.AppendLaunchArgument("--session-id abc", null));
    }

    [Theory]
    [InlineData("{\"type\":\"agent_start\"}", "turn_started")]
    [InlineData("{\"type\":\"agent_settled\",\"aborted\":false}", "turn_ended")]
    [InlineData("{\"type\":\"ui_prompt_start\",\"reason\":\"ui_prompt\",\"kind\":\"select\",\"title\":\"Choose model\"}", "menu_opened")]
    [InlineData("{\"type\":\"ui_prompt_end\",\"reason\":\"ui_prompt\",\"kind\":\"select\",\"title\":\"Choose model\"}", "menu_closed")]
    public void Extension_CapturedPiLifecyclePayload_MapsToTheContractEvent(string capturedPayload, string expected)
    {
        using var payload = JsonDocument.Parse(capturedPayload);
        var type = payload.RootElement.GetProperty("type").GetString();
        var source = PiStateExtensionInstaller.ExtensionSource();
        var literal = StateEventMap().Match(source).Groups[1].Value;
        var eventMap = JsonSerializer.Deserialize<Dictionary<string, string>>(literal);

        Assert.NotNull(eventMap);
        Assert.Equal(expected, eventMap[type!]);
    }

    [Fact]
    public void Extension_UnknownPiPayload_EmitsAnUnsupportedDiagnosticWithTheRawType()
    {
        var source = PiStateExtensionInstaller.ExtensionSource();

        Assert.Contains("event: \"unsupported\", detail: event.type || \"missing_event_type\"", source, StringComparison.Ordinal);
        Assert.Contains("cliVersion", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_DropContract_UsesCapabilityDirectorySequenceNamesAndAtomicRename()
    {
        var source = PiStateExtensionInstaller.ExtensionSource();

        Assert.Contains("CC_STATE_EVENT_DIR", source, StringComparison.Ordinal);
        Assert.Contains("CC_STATE_EVENT_TOKEN", source, StringComparison.Ordinal);
        Assert.Contains("basename(directory) !== `${sessionId}.${token}`", source, StringComparison.Ordinal);
        Assert.Contains("`${sequence}.state.json`", source, StringComparison.Ordinal);
        Assert.Contains("join(directory, \".sequence\")", source, StringComparison.Ordinal);
        Assert.Contains("renameSync(temporary, marker)", source, StringComparison.Ordinal);
        Assert.Contains("renameSync(temporary, destination)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_SequenceLock_RefusesToRemoveALockHeldByALiveProcess()
    {
        var source = PiStateExtensionInstaller.ExtensionSource();

        Assert.Contains("linkSync(candidate, lockPath)", source, StringComparison.Ordinal);
        Assert.Contains("process.kill(pid, 0)", source, StringComparison.Ordinal);
        Assert.Contains("!holderIsAlive(holder.pid)", source, StringComparison.Ordinal);
        Assert.Contains("if (holder.pid === process.pid) unlinkSync(lockPath)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_PermissionSignal_IsNotInventedFromAGenericConfirmation()
    {
        var source = PiStateExtensionInstaller.ExtensionSource();

        Assert.DoesNotContain("permission_requested", source, StringComparison.Ordinal);
        Assert.Contains("\"ui_prompt_start\": \"menu_opened\"", source, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"const stateEventMap = (\{[\s\S]*?\}) as const;")]
    private static partial Regex StateEventMap();
}
