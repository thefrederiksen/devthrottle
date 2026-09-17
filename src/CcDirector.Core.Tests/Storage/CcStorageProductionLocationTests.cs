using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Pins WHERE the user's data lives when CC_DIRECTOR_ROOT is not set - which is always, in the product.
///
/// The burn-down moved 22 call sites off hand-rolled paths and onto CcStorage. Every one of those
/// folders already held real user data on real machines, so the refactor is only safe if each method
/// resolves to the exact path its old hand-rolled expression produced. A silent change here does not
/// throw or fail a test elsewhere - it just quietly orphans the user's existing recordings, logs, hooks
/// or transcripts and starts writing somewhere new, which is the kind of bug you find months later.
///
/// Each expectation is the literal path the old code composed, written out rather than derived from
/// Base(), so this fails if Base() itself ever moves too.
///
/// The paths are resolved INSIDE the test, never in a MemberData/TheoryData member: those are evaluated
/// at discovery time, before this class's constructor clears the root, so they would capture whatever
/// CC_DIRECTOR_ROOT another test class happened to have set and assert nothing useful.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class CcStorageProductionLocationTests : IDisposable
{
    private readonly string? _prevRoot;

    public CcStorageProductionLocationTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", null); // the product never sets it
    }

    public void Dispose() => Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);

    private static string Local(params string[] parts)
        => Path.Combine(new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director",
        }.Concat(parts).ToArray());

    [Fact]
    public void Every_burned_down_folder_resolves_where_the_hand_rolled_code_put_it()
    {
        var expected = new (string Actual, string Expected, string Name)[]
        {
            (CcStorage.AgentPlugins(), Local("agent-plugins"), nameof(CcStorage.AgentPlugins)),
            (CcStorage.ClaudeHooks(), Local("claude-hooks"), nameof(CcStorage.ClaudeHooks)),
            (CcStorage.CodexHooks(), Local("codex-hooks"), nameof(CcStorage.CodexHooks)),
            (CcStorage.Dictation(), Local("dictation"), nameof(CcStorage.Dictation)),
            (CcStorage.DictationDictionary(), Local("dictation", "dictionary.yaml"), nameof(CcStorage.DictationDictionary)),
            (CcStorage.DictationRecordings(), Local("dictation", "recordings"), nameof(CcStorage.DictationRecordings)),
            (CcStorage.DictationSessions(), Local("dictation", "sessions"), nameof(CcStorage.DictationSessions)),
            (CcStorage.PiPreamble(), Local("pi-preamble"), nameof(CcStorage.PiPreamble)),
            // Remove-the-network-port mission, phase 3. Pinned for the same reason as everything else
            // here, and with one extra edge: the exact path of a session's drop file is stamped into
            // that session's ENVIRONMENT at launch and cannot be re-issued while it runs. So moving
            // either folder does not merely orphan files - it points every already-running session's
            // hook at a place the Director is no longer looking, silently.
            (CcStorage.SessionPreambles(), Local("session-preambles"), nameof(CcStorage.SessionPreambles)),
            (CcStorage.SessionPointers(), Local("session-pointers"), nameof(CcStorage.SessionPointers)),
            (CcStorage.SessionRecordings(), Local("session-recordings"), nameof(CcStorage.SessionRecordings)),
            (CcStorage.StateChanges(), Local("state-changes"), nameof(CcStorage.StateChanges)),
            (CcStorage.VoiceUtterances(), Local("voice-utterances"), nameof(CcStorage.VoiceUtterances)),
            (CcStorage.Transcripts(), Local("transcripts"), nameof(CcStorage.Transcripts)),
            (CcStorage.PromptLog(), Local("prompt-log"), nameof(CcStorage.PromptLog)),
            (CcStorage.TranscriptionHistory(), Local("transcription-history"), nameof(CcStorage.TranscriptionHistory)),
            (CcStorage.TranscriptionAudio(), Local("transcription-audio"), nameof(CcStorage.TranscriptionAudio)),
            (CcStorage.TerminalCaptures(), Local("terminal-captures"), nameof(CcStorage.TerminalCaptures)),
            (CcStorage.WebView2Card(), Local("webview2-card"), nameof(CcStorage.WebView2Card)),
            (CcStorage.Bin(), Local("bin"), nameof(CcStorage.Bin)),
            (CcStorage.ToolLogs("director"), Local("logs", "director"), "ToolLogs(director)"),
            // AgentBrain composes this by hand under an exemption; it must still land where it always has.
            (CcStorage.ToolLogs("agent-brain"), Local("logs", "agent-brain"), "ToolLogs(agent-brain)"),
        };

        var moved = expected
            .Where(e => !string.Equals(
                Path.TrimEndingDirectorySeparator(e.Actual),
                Path.TrimEndingDirectorySeparator(e.Expected),
                StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.Name}: expected {e.Expected} but got {e.Actual}")
            .ToList();

        Assert.True(moved.Count == 0,
            "These folders MOVED. Existing user data is still at the old path and would be silently "
            + "orphaned:\n  " + string.Join("\n  ", moved));
    }

    [Fact]
    public void VaultTranscripts_follows_the_vault_not_the_storage_root()
    {
        // RecordingEndpoints used to hardcode LocalAppData\cc-director\vault\transcripts, which ignored
        // CC_VAULT_PATH. Going through Vault() is what makes a relocated vault work; this pins that the
        // promotion target follows the vault override rather than the storage root.
        var prevVault = Environment.GetEnvironmentVariable("CC_VAULT_PATH");
        var vault = Path.Combine(Path.GetTempPath(), "ccd-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("CC_VAULT_PATH", vault);

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.Combine(vault, "transcripts")),
                Path.TrimEndingDirectorySeparator(CcStorage.VaultTranscripts()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_VAULT_PATH", prevVault);
        }
    }
}
