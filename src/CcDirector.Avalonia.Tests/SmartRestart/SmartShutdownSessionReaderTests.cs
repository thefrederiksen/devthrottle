using System.Reflection;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// The helper that turns the Director's own <see cref="Session"/> objects into the plain description
/// the Smart shutdown dialog is given. These run on REAL Session objects, so a change to how the helper
/// reads a session is watched here.
///
/// ONE THING IS FORCED, AND IT IS SAID PLAINLY. Session.PendingInteraction has a private setter and
/// nothing in the product sets it today (PendingInteraction.cs says so), so there is no honest way to
/// put a session into "a question box is open". The test sets the property by reflection. It proves
/// the helper reads the property correctly; it does NOT prove the product ever fills it, and today it
/// does not, so in the running application the "Answer these first?" section will not appear until
/// the terminal detection that repopulates the property is built.
/// </summary>
public class SmartShutdownSessionReaderTests
{
    private static Session SessionIn(ActivityState state, string? customName, string repoPath = @"C:\test\repo") =>
        new(Guid.NewGuid(), repoPath, repoPath, null, new InertBackend(), null, state,
            DateTimeOffset.UtcNow, customName, null);

    private static void OpenQuestionBox(Session session)
    {
        var property = typeof(Session).GetProperty(nameof(Session.PendingInteraction))!;
        var setter = property.GetSetMethod(nonPublic: true);
        Assert.NotNull(setter);
        setter!.Invoke(session, [new PendingInteraction
        {
            Kind = PendingInteractionKind.Question,
            CreatedAt = DateTimeOffset.UtcNow,
            Prompt = "Which branch?",
        }]);
    }

    [Fact]
    public void Read_WorkingAndStartingSessions_AreWorking()
    {
        var described = SmartShutdownSessionReader.Read(
        [
            SessionIn(ActivityState.Working, "builder"),
            SessionIn(ActivityState.Starting, "just opened"),
        ]);

        Assert.Equal(2, described.Count);
        Assert.All(described, s => Assert.True(s.IsWorking));
        Assert.All(described, s => Assert.False(s.HasQuestionBoxOpen));
    }

    [Fact]
    public void Read_WaitingIdleAndPermissionSessions_AreWaiting()
    {
        var described = SmartShutdownSessionReader.Read(
        [
            SessionIn(ActivityState.WaitingForInput, "waiting for input"),
            SessionIn(ActivityState.Idle, "idle"),
            SessionIn(ActivityState.WaitingForPerm, "waiting for permission"),
        ]);

        Assert.Equal(3, described.Count);
        Assert.All(described, s => Assert.False(s.IsWorking));
    }

    [Fact]
    public void Read_SessionWithPendingInteraction_HasQuestionBoxOpen()
    {
        var asking = SessionIn(ActivityState.WaitingForInput, "asking");
        var quiet = SessionIn(ActivityState.WaitingForInput, "quiet");
        OpenQuestionBox(asking);

        var described = SmartShutdownSessionReader.Read([asking, quiet]);

        Assert.True(described.Single(s => s.DisplayName == "asking").HasQuestionBoxOpen);
        Assert.False(described.Single(s => s.DisplayName == "quiet").HasQuestionBoxOpen);
    }

    [Fact]
    public void Read_NamedAndUnnamedSessions_UseTheNameTheRailShows()
    {
        var described = SmartShutdownSessionReader.Read(
        [
            SessionIn(ActivityState.Working, "Billing - Developer - the invoice page"),
            SessionIn(ActivityState.Working, null, @"D:\ReposFred\devthrottle\"),
        ]);

        Assert.Equal("Billing - Developer - the invoice page", described[0].DisplayName);
        Assert.Equal("devthrottle", described[1].DisplayName);
    }

    [Fact]
    public void Read_ExitedSession_IsLeftOut()
    {
        var described = SmartShutdownSessionReader.Read(
        [
            SessionIn(ActivityState.Exited, "gone"),
            SessionIn(ActivityState.Working, "here"),
        ]);

        Assert.Equal("here", Assert.Single(described).DisplayName);
    }

    [Fact]
    public void Read_ItsDescription_DrivesTheCountsTheDialogShows()
    {
        var asking = SessionIn(ActivityState.WaitingForInput, "asking");
        OpenQuestionBox(asking);

        var viewModel = new SmartShutdownViewModel(
            SmartShutdownSessionReader.Read(
            [
                SessionIn(ActivityState.Working, "one"),
                SessionIn(ActivityState.Working, "two"),
                asking,
            ]),
            SmartShutdownDoor.WindowClose);

        Assert.Equal("3 sessions are running", viewModel.SessionCountText);
        Assert.Equal("2 working, 1 waiting", viewModel.WorkingWaitingText);
        Assert.Equal(new[] { "asking" }, viewModel.QuestionBoxSessionNames);
    }

    /// <summary>An inert backend: the Session needs one, these tests never run a process.</summary>
    private sealed class InertBackend : ISessionBackend
    {
        public int ProcessId => 1234;
        public string Status => "Inert";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface; nothing raises them here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
