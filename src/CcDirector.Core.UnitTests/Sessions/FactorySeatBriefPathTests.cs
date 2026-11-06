using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A SEAT IS TOLD WHERE ITS BRIEF IS (issue #4609). On 2026-11-05 a factory seat was started with "Do what your brief
/// says, from STEP 1" and nothing saying where the brief was. It started in its kit's empty work/ folder with the brief
/// one level up, ran on a weak model, asked a person for the brief, and did nothing all night while a customer waited.
///
/// The fix is that every first prompt of a seat opens with the full path of its brief. These tests send the first prompt
/// through the real <see cref="Session.DeliverPrePromptAsync"/> - the one method every schedule, check, talk and restore
/// goes through - into a backend that records what reached the agent, with the kit written by the real kit writer.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatBriefPathTests : IDisposable
{
    private const string StartPrompt = "Najah has emailed and has been quiet for 30 minutes. Do what your brief says, from STEP 1.";

    private readonly string _root = Directory.CreateTempSubdirectory("dt-brief-path-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private static FactoryKitResponse Kit(string kind, bool withBrief)
    {
        var files = new List<FactoryKitFileDto>
        {
            new() { Path = "kit.json", Text = "{}\n" },
            new() { Path = "rules.json", Text = "{\"permissions\":{}}\n" },
        };
        if (withBrief) files.Add(new() { Path = "brief.md", Text = "STEP 1. Read the customer's mail.\n" });
        return new FactoryKitResponse { Factory = "cpmc", Seat = "forward-engineer", Version = 6, Kind = kind, Files = files };
    }

    /// <summary>A seat session as the Director makes one: its kit written, the session working in the kit's work/
    /// folder, and the seat facts stamped as the create command stamps them.</summary>
    private (Session Session, RecordingBackend Backend, string KitDirectory) Seat(string kind = FactoryKits.Seat, bool withBrief = true)
    {
        var id = Guid.NewGuid();
        var kitDirectory = FactoryRunKit.Write(id, Kit(kind, withBrief), root: _root);
        var backend = new RecordingBackend();
        var session = new Session(id, _root, FactoryRunKit.WorkDirectoryOf(kitDirectory), null, backend, SessionBackendType.Pipe)
        {
            FactoryKitDirectory = kitDirectory,
        };
        session.StampFactorySeat("forward-engineer", 6, kind);
        return (session, backend, kitDirectory);
    }

    [Fact]
    public async Task DeliverPrePromptAsync_SeatKit_OpensWithTheFullPathOfTheBriefThenTheStartPrompt()
    {
        // Arrange
        var (session, backend, kitDirectory) = Seat();
        using var _ = session;
        var brief = Path.GetFullPath(Path.Combine(kitDirectory, "brief.md"));

        // Act
        await session.DeliverPrePromptAsync(StartPrompt, TimeSpan.FromMilliseconds(1));

        // Assert
        var sent = Assert.Single(backend.Sent);
        Assert.Equal(
            $"Read your brief in full before you do anything else, then do what it says. Your brief is this file:\n{brief}\n\n" + StartPrompt,
            sent);
        Assert.True(Path.IsPathFullyQualified(brief));
        Assert.True(File.Exists(brief));
        Assert.Null(session.FirstPromptFailedAtUtc);
    }

    [Fact]
    public async Task DeliverPrePromptAsync_RulesOnlyKit_SendsThePromptUnchanged()
    {
        // Arrange - a child of a seat carries rules only, and no brief.
        var (session, backend, _) = Seat(FactoryKits.RulesOnly, withBrief: false);
        using var _s = session;

        // Act
        await session.DeliverPrePromptAsync(StartPrompt, TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(StartPrompt, Assert.Single(backend.Sent));
    }

    [Fact]
    public async Task DeliverPrePromptAsync_NotASeat_SendsThePromptUnchanged()
    {
        // Arrange
        var backend = new RecordingBackend();
        using var session = new Session(Guid.NewGuid(), _root, _root, null, backend, SessionBackendType.Pipe);

        // Act
        await session.DeliverPrePromptAsync(StartPrompt, TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(StartPrompt, Assert.Single(backend.Sent));
    }

    [Fact]
    public async Task DeliverPrePromptAsync_SeatKitWithNoBriefOnDisk_SendsNothingAndRecordsTheFirstPromptAsNotDelivered()
    {
        // Arrange
        var (session, backend, kitDirectory) = Seat(withBrief: false);
        using var _ = session;

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DeliverPrePromptAsync(StartPrompt, TimeSpan.FromMilliseconds(1)));

        // Assert
        Assert.Empty(backend.Sent);
        Assert.Equal(
            $"The seat's kit at {kitDirectory} has no brief.md, so its first prompt was not sent: the seat would be told to read a brief that is not there.",
            ex.Message);
        Assert.NotNull(session.FirstPromptFailedAtUtc);
    }

    [Fact]
    public void WithBriefPath_KitUnderAFolderWithASpace_PutsTheWholePathAloneOnItsOwnLine()
    {
        // Arrange - the storage root sits under the user's folder, and an account name can have a space in it.
        var kitDirectory = FactoryRunKit.Write(Guid.NewGuid(), Kit(FactoryKits.Seat, withBrief: true),
            root: Path.Combine(_root, "Soren Frederiksen", "factory-run"));

        // Act
        var lines = FactoryRunKit.WithBriefPath(StartPrompt, FactoryKits.Seat, kitDirectory).Split('\n');

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine(kitDirectory, "brief.md")), lines[1]);
        Assert.Contains(' ', lines[1]);
        Assert.True(File.Exists(lines[1]));
    }

    [Fact]
    public void WithBriefPath_PromptAlreadyOpensWithTheLine_IsNotNamedTwice()
    {
        // Arrange
        var kitDirectory = FactoryRunKit.Write(Guid.NewGuid(), Kit(FactoryKits.Seat, withBrief: true), root: _root);
        var once = FactoryRunKit.WithBriefPath(StartPrompt, FactoryKits.Seat, kitDirectory);

        // Act
        var twice = FactoryRunKit.WithBriefPath(once, FactoryKits.Seat, kitDirectory);

        // Assert
        Assert.Equal(once, twice);
    }

    /// <summary>A one-call backend that records each prompt it is handed and starts nothing.</summary>
    private sealed class RecordingBackend : ISessionBackend
    {
        public List<string> Sent { get; } = new();
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text)
        {
            Sent.Add(text);
            return Task.CompletedTask;
        }
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
