using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Defect 23: PENDING DELETION IS A BADGE, NEVER A COLOUR (owner's ruling, 14 July 2026).
///
/// A session flagged for deletion MAY STILL BE WORKING - the Director's reaper explicitly waits out a
/// running final turn (<c>SessionManager.ReapPendingDeletions</c>). Under the law that session is BLUE,
/// with a badge. Pending deletion says nothing about what the agent is DOING.
///
/// What was wrong, and why it needed BOTH halves: the Gateway's fold never read <c>PendingDeletion</c>, so
/// on every Gateway-backed screen a flagged session kept its normal colour - while
/// <c>SessionDto.PendingDeletion</c>'s comment claimed the row "paints as a winding-down grey". No code
/// ever kept that promise. The only implementation was <c>Session.MarkForDeletion</c> calling
/// <c>SetStatusColor(Unknown, ...)</c> on itself - the Director deciding a colour, which law 2 forbids and
/// which nothing that paints reads (the Gateway is the single fold and reads the cooked StatusColor for
/// NOTHING). Two implementations, neither delivering what the DTO promised.
///
/// These tests drive the REAL producer (<c>Session.MarkForDeletion</c> on a live Session), then the REAL
/// wire mapper (<c>ControlEndpoints.Map</c> - the same one the Gateway aggregates and the desktop rail
/// folds through, via <c>SessionViewModel.FoldInput</c>), then the REAL fold. Nothing sets
/// <c>PendingDeletion</c> by hand, so these cannot pass on a fact production never emits - the failure
/// mode where a live consumer is fed a value the producer stopped sending.
/// </summary>
public sealed class PendingDeletionBadgeTests
{
    /// <summary>
    /// A minimal live backend that behaves the way Claude Code does when a prompt is submitted: the text is
    /// typed, and on Enter the agent writes it into its conversation file as a user line. That file is the
    /// only proof of arrival the Director accepts (issue #3290), so a backend that never wrote one made every
    /// send in this class wait out the whole arrival window and fail (issue #3386). These tests never exit the
    /// process; they only need a Session that has taken one real turn and can be driven to an activity state.
    /// </summary>
    private sealed class RunningBackend : ISessionBackend
    {
        private readonly string _conversationFile;
        private readonly StringBuilder _composer = new();

        public RunningBackend(string conversationFile) => _conversationFile = conversationFile;

        public int ProcessId => 1234;
        public string Status => "Running";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // required by the interface, unused here
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }

        public void Write(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            if (text == "\r") Submit(); else _composer.Append(text);
        }

        public Task SendTextAsync(string text) { _composer.Append(text); return Task.CompletedTask; }
        public Task SendEnterAsync() { Submit(); return Task.CompletedTask; }
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }

        private void Submit()
        {
            if (_composer.Length == 0) return;
            var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "user",
                ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = _composer.ToString() },
            });
            File.AppendAllText(_conversationFile, line + "\n", Encoding.UTF8);
            _composer.Clear();
        }
    }

    /// <summary>
    /// Drive a real Session into <paramref name="state"/>, optionally flag it for deletion through the REAL
    /// producer, and return what the wire carries.
    ///
    /// The session is driven through a real submitted turn first (<c>SendTextAsync</c>) so it is no longer
    /// brand-new. That matters: a brand-new session parked at its prompt folds to GREEN "Ready", not red -
    /// so without this a "flagged and waiting on the user" case would be testing the wrong session
    /// entirely. Driven, not hand-set (<c>IsBrandNew</c> is settable), to keep the producer real - and the
    /// send is proven the way production proves it, through the conversation file the backend writes.
    /// </summary>
    private static async Task<SessionDto> OnTheWireAsync(ActivityState state, bool flagForDeletion, string? reason = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-badge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var claudeSessionId = Guid.NewGuid().ToString();
            var conversationFile = Path.Combine(dir, claudeSessionId + ".jsonl");
            var backend = new RunningBackend(conversationFile);
            using var session = new Session(
                Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
                backend, SessionBackendType.ConPty);
            session.UpdateClaudeSessionPointer(claudeSessionId, conversationFile, "PendingDeletionBadgeTests");
            session.MarkRunning();
            await session.SendTextAsync("first turn");   // a real submitted turn: no longer brand-new
            session.ApplyTerminalActivityState(state);

            if (flagForDeletion)
                session.MarkForDeletion(reason);   // the real producer

            return ControlEndpoints.Map(session, directorId: "");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// THE HEADLINE: a WORKING session flagged for deletion is BLUE, and carries the badge fact.
    /// Blue because it is working - nothing outranks working; the badge because it is going.
    /// </summary>
    [Fact]
    public async Task WorkingSession_flaggedForDeletion_isBlue_withTheBadgeFact()
    {
        var dto = await OnTheWireAsync(ActivityState.Working, flagForDeletion: true, reason: "jobs-auto: nothing to report");

        Assert.True(dto.PendingDeletion);                            // the fact reached the wire...
        Assert.Equal("jobs-auto: nothing to report", dto.DeletionReason);
        Assert.Equal("blue", SessionOrdering.EffectiveColor(dto));   // ...and did NOT become a colour
        Assert.Equal("Working", SessionOrdering.StateLabel(dto));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(dto));
    }

    /// <summary>
    /// The Director writes NO colour for deletion. Its cooked StatusColor is unchanged by flagging - which
    /// is the whole of law 2 here: the Director reports the fact and decides nothing.
    ///
    /// Nothing that paints reads StatusColor any more, so this is belt and braces - but the deleted call
    /// was positive-evidence and therefore STICKY, which is a real behaviour worth pinning: it blocked the
    /// wingman's activity mapping from repainting a flagged session within one activity generation.
    /// </summary>
    [Fact]
    public async Task MarkForDeletion_doesNotTouchTheDirectorsColour()
    {
        var before = await OnTheWireAsync(ActivityState.Working, flagForDeletion: false);
        var after = await OnTheWireAsync(ActivityState.Working, flagForDeletion: true, reason: "done");

        Assert.False(before.PendingDeletion);
        Assert.True(after.PendingDeletion);
        Assert.Equal(before.StatusColor, after.StatusColor);   // the Director decided nothing
    }

    /// <summary>
    /// The other half of the ruling: a flagged session that is WAITING is still red "Needs you". The badge
    /// does not recede it, because pending deletion says nothing about what the agent is doing - and this
    /// session is doing the one thing that must never be hidden: waiting on the human.
    /// </summary>
    [Fact]
    public async Task WaitingSession_flaggedForDeletion_isStillRed_notAWindingDownGrey()
    {
        var dto = await OnTheWireAsync(ActivityState.WaitingForInput, flagForDeletion: true, reason: "done");

        Assert.True(dto.PendingDeletion);
        Assert.Equal("red", SessionOrdering.EffectiveColor(dto));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(dto));
    }

    /// <summary>
    /// The flag is INVISIBLE to the fold: flagged and unflagged fold identically in every state. This is
    /// the pin against someone "helpfully" adding a PendingDeletion branch to EffectiveColor - the exact
    /// change the ruling forbids, and the one that would re-open defect 23.
    /// </summary>
    [Theory]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.Starting)]
    [InlineData(ActivityState.WaitingForInput)]
    [InlineData(ActivityState.WaitingForPerm)]
    public async Task TheFlagIsInvisibleToTheFold_inEveryState(ActivityState state)
    {
        var flagged = await OnTheWireAsync(state, flagForDeletion: true, reason: "done");
        var plain = await OnTheWireAsync(state, flagForDeletion: false);

        Assert.True(flagged.PendingDeletion);
        Assert.False(plain.PendingDeletion);
        Assert.Equal(SessionOrdering.EffectiveColor(plain), SessionOrdering.EffectiveColor(flagged));
        Assert.Equal(SessionOrdering.StateLabel(plain), SessionOrdering.StateLabel(flagged));
        Assert.Equal(SessionOrdering.Classify(plain), SessionOrdering.Classify(flagged));
    }

    /// <summary>A flag with no reason still crosses the wire as a flag: the badge shows, the tooltip just
    /// has nothing extra to say. (The rail falls back to "Marked for deletion - reaping shortly".)</summary>
    [Fact]
    public async Task FlaggedWithNoReason_stillCarriesTheFact()
    {
        var dto = await OnTheWireAsync(ActivityState.Working, flagForDeletion: true, reason: null);

        Assert.True(dto.PendingDeletion);
        Assert.Null(dto.DeletionReason);
        Assert.Equal("blue", SessionOrdering.EffectiveColor(dto));
    }
}
