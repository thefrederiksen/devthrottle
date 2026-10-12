using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The two things that made the terminal recorder a data-collection defect rather than a feature:
/// it ran on every install unless somebody found an environment variable named only in a source
/// comment, and what it wrote outlived the session it was about, forever.
///
/// CC_DIRECTOR_ROOT is pinned to a temp directory so config.json and the recordings folder read and
/// written here are this test's, never the real machine's.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class TerminalSessionRecorderPolicyTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevEnv;

    public TerminalSessionRecorderPolicyTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-recorder-policy-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        // The override is process-wide and the machine running this suite may well have it set -
        // it is how corpus collection was switched on before there was a setting - so a test about
        // the DEFAULT has to clear it, or it would read the machine's opinion and call it the default.
        _prevEnv = Environment.GetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable);
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, _prevEnv);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Recording_is_off_on_a_clean_install()
    {
        Assert.False(SessionRecordingConfig.IsEnabled());
    }

    [Fact]
    public void Recording_is_on_only_when_the_visible_setting_says_so()
    {
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            [SessionRecordingConfig.SectionName] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = true },
        });

        Assert.True(SessionRecordingConfig.IsEnabled());
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void The_environment_override_wins_in_both_directions(string value, bool expected)
    {
        // Config says the opposite of the override, so a test that passed by accident - because the
        // config default happened to agree - cannot pass here.
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            [SessionRecordingConfig.SectionName] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = !expected },
        });
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, value);

        Assert.Equal(expected, SessionRecordingConfig.IsEnabled());
    }

    [Fact]
    public void A_nonsense_override_says_so_rather_than_guessing()
    {
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, "maybe");

        var ex = Assert.Throws<InvalidOperationException>(() => SessionRecordingConfig.IsEnabled());
        Assert.Contains(SessionRecordingConfig.EnvironmentVariable, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing a session deletes what was recorded of it. Before this, removal disposed the writer
    /// and left the file: an install with recording switched on accumulated the screens of sessions
    /// that no longer existed, in a directory nothing ever swept.
    ///
    /// The directory is created here with the SAME spelling the recorder writes with, and the
    /// arrangement is asserted before the act - a purge aimed at a directory that never existed
    /// would otherwise pass this test while deleting nothing, which is exactly the mistake the
    /// first version of the purge made (it used the dashed spelling of the id).
    /// </summary>
    [Trait("Gate", "Quarantine")]
    [Fact]
    public void Removing_a_session_purges_its_recording()
    {
        var recordings = Path.Combine(_root, "recordings-under-test");
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        using var recorder = new TerminalSessionRecorder(manager, root: recordings);
        try
        {
            recorder.Start();
            var session = manager.CreateSession(Path.GetTempPath());

            var sessionDir = Path.Combine(recordings, session.Id.ToString("N"));
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "grid.jsonl"), "{\"rows\":[\"a secret on a screen\"]}\n");
            Assert.True(Directory.Exists(sessionDir), "the arrangement did not create the recording it is about to remove");

            manager.RemoveSession(session.Id);

            Assert.False(Directory.Exists(sessionDir),
                "the session was removed but its recorded screens are still on disk");
        }
        finally { manager.Dispose(); }
    }

    /// <summary>
    /// The pass-3 inspection's Finding 2: purge-on-removal only fires for sessions removed while
    /// THIS process is running. A recording whose session was removed before the upgrade - under
    /// the old default-on release, with no purge handler alive to notice - has no live session left
    /// to emit a removal event, so without a startup reconciliation it survives forever and the
    /// claim that installs no longer accumulate removed-session screens is false for exactly the
    /// installs the policy was written for. Startup must sweep those orphans, under the shipped
    /// default (capture OFF), while leaving live sessions' recordings and unrecognised directories
    /// alone.
    /// </summary>
    [Trait("Gate", "Quarantine")]
    [Fact]
    public void Startup_sweeps_recordings_orphaned_before_this_run()
    {
        var recordings = Path.Combine(_root, "recordings-startup-sweep");

        // The pre-upgrade world, arranged BEFORE the recorder exists: a recording whose session was
        // removed in a previous life, and a directory that is not a recording at all.
        var orphanDir = Path.Combine(recordings, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "grid.jsonl"), "{\"rows\":[\"screens of a session removed before the upgrade\"]}\n");
        var foreignDir = Path.Combine(recordings, "not-a-session-recording");
        Directory.CreateDirectory(foreignDir);
        File.WriteAllText(Path.Combine(foreignDir, "keep.txt"), "someone else's data at a name we do not recognise");

        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        using var recorder = new TerminalSessionRecorder(manager, root: recordings, captureEnabled: false);
        try
        {
            // A session alive at startup: its recording must survive the sweep.
            var live = manager.CreateSession(Path.GetTempPath());
            var liveDir = Path.Combine(recordings, live.Id.ToString("N"));
            Directory.CreateDirectory(liveDir);
            File.WriteAllText(Path.Combine(liveDir, "grid.jsonl"), "{\"rows\":[\"a live session's screens\"]}\n");

            recorder.Start();

            Assert.False(Directory.Exists(orphanDir),
                "the recording orphaned before this run is still on disk - the startup sweep did not run");
            Assert.True(Directory.Exists(liveDir),
                "the startup sweep deleted a LIVE session's recording");
            Assert.True(Directory.Exists(foreignDir),
                "the startup sweep deleted a directory it does not own");
        }
        finally { manager.Dispose(); }
    }

    /// <summary>
    /// Capture and purge are two different lifecycles: switching capture OFF must not switch the
    /// purge off with it. A recording made while capture WAS on (the old default-on release) still
    /// belongs to its session, and is deleted with it - while the capture-off recorder writes
    /// nothing new for the session that lived and died here.
    /// </summary>
    [Trait("Gate", "Quarantine")]
    [Fact]
    public void Capture_off_still_purges_a_preexisting_recording_and_records_nothing_new()
    {
        var recordings = Path.Combine(_root, "recordings-capture-off");
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        using var recorder = new TerminalSessionRecorder(manager, root: recordings, captureEnabled: false);
        try
        {
            recorder.Start();
            var session = manager.CreateSession(Path.GetTempPath());

            var sessionDir = Path.Combine(recordings, session.Id.ToString("N"));
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "grid.jsonl"), "{\"rows\":[\"left by the default-on release\"]}\n");

            manager.RemoveSession(session.Id);

            Assert.False(Directory.Exists(sessionDir),
                "capture is off, but the purge must still delete a removed session's recording");
            // And nothing NEW was recorded: the only entry ever under the root was the arranged one.
            Assert.False(Directory.Exists(recordings) && Directory.EnumerateFileSystemEntries(recordings).Any(),
                "capture is off, so the recorder must not have written anything of its own");
        }
        finally { manager.Dispose(); }
    }
}
