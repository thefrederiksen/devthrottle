using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The Repository screen's "in use" source reads the other Director slots on this machine from the
/// rosters they keep on disk, instead of downloading the Gateway's whole fleet list on every recompute.
/// These tests build a throwaway machine root with real journal files and a real git worktree, and use
/// no network of any kind.
/// </summary>
public sealed class MachineLiveSessionsTests : IDisposable
{
    private const int OwnPid = 1000;
    private const int SiblingPid = 2000;
    private const int DeadPid = 3000;

    private readonly string _root;

    public MachineLiveSessionsTests()
    {
        _root = TestTempRoot.For("ccd-machine-sessions-");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; } catch { Thread.Sleep(150); }
        }
    }

    private static bool AliveIsOwnOrSibling(int pid) => pid is OwnPid or SiblingPid;

    private string JournalDirOf(string? slug) => slug is null
        ? Path.Combine(_root, "config", "director", "crash-journal")
        : Path.Combine(_root, "instances", slug, "config", "director", "crash-journal");

    private void WriteJournal(string? slug, string fileName, int pid, params (string Name, string RepoPath)[] sessions)
    {
        var dir = JournalDirOf(slug);
        Directory.CreateDirectory(dir);
        var data = new DirectorCrashJournalData
        {
            DirectorId = Path.GetFileNameWithoutExtension(fileName),
            Pid = pid,
            MachineName = Environment.MachineName,
            Sessions = sessions.Select(s => new DirectorCrashJournalSession
            {
                SessionId = Guid.NewGuid().ToString(),
                Name = s.Name,
                RepoPath = s.RepoPath,
            }).ToList(),
        };
        File.WriteAllText(Path.Combine(dir, fileName), JsonSerializer.Serialize(data));
    }

    [Fact]
    public void ReadOtherDirectors_SiblingSlotInAnInstanceHome_ReturnsItsSessions()
    {
        WriteJournal("slot2", "dir-b.json", SiblingPid, ("Sibling work", @"C:\repos\a-wt"));

        var snapshot = MachineLiveSessions.ReadOtherDirectors(_root, OwnPid, AliveIsOwnOrSibling);

        var only = Assert.Single(snapshot.Sessions);
        Assert.Equal(@"C:\repos\a-wt", only.RepoPath);
        Assert.Equal("Sibling work", only.Label);
        Assert.Empty(snapshot.Unreadable);
    }

    [Fact]
    public void ReadOtherDirectors_LegacyFlatLayout_IsReadToo()
    {
        WriteJournal(null, "dir-flat.json", SiblingPid, ("Old layout", @"C:\repos\flat-wt"));

        var snapshot = MachineLiveSessions.ReadOtherDirectors(_root, OwnPid, AliveIsOwnOrSibling);

        Assert.Equal(@"C:\repos\flat-wt", Assert.Single(snapshot.Sessions).RepoPath);
    }

    [Fact]
    public void ReadOtherDirectors_OwnJournal_IsSkipped_BecauseOwnSessionsComeFromMemory()
    {
        WriteJournal("default", "dir-own.json", OwnPid, ("Mine", @"C:\repos\mine"));

        var snapshot = MachineLiveSessions.ReadOtherDirectors(_root, OwnPid, AliveIsOwnOrSibling);

        Assert.Empty(snapshot.Sessions);
    }

    [Fact]
    public void ReadOtherDirectors_DeadDirectorsJournal_IsNotALiveSession()
    {
        WriteJournal("slot3", "dir-dead.json", DeadPid, ("Crashed", @"C:\repos\gone"));

        var snapshot = MachineLiveSessions.ReadOtherDirectors(_root, OwnPid, AliveIsOwnOrSibling);

        Assert.Empty(snapshot.Sessions);
    }

    [Fact]
    public void ReadOtherDirectors_ClaimedDirtyJournal_IsSkipped()
    {
        WriteJournal("slot2", "dir-b.2000.dirty.json", SiblingPid, ("Recovered later", @"C:\repos\dirty"));

        var snapshot = MachineLiveSessions.ReadOtherDirectors(_root, OwnPid, AliveIsOwnOrSibling);

        Assert.Empty(snapshot.Sessions);
    }

    [Fact]
    public void ReadOtherDirectors_UnreadableJournal_IsReportedByName_NotDroppedSilently()
    {
        var dir = JournalDirOf("slot4");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dir-broken.json"), "{ this is not json");
        WriteJournal("slot2", "dir-b.json", SiblingPid, ("Sibling work", @"C:\repos\a-wt"));

        var snapshot = MachineLiveSessions.ReadOtherDirectors(_root, OwnPid, AliveIsOwnOrSibling);

        Assert.Single(snapshot.Sessions);
        Assert.Contains(snapshot.Unreadable, u => u.Contains("dir-broken.json"));
    }

    [Fact]
    public void ReadOtherDirectors_NoDirectorHasEverRunHere_ReturnsNothing()
    {
        var snapshot = MachineLiveSessions.ReadOtherDirectors(_root, OwnPid, AliveIsOwnOrSibling);

        Assert.Empty(snapshot.Sessions);
        Assert.Empty(snapshot.Unreadable);
    }

    [Fact]
    public void OnThisMachine_OwnSessionsAndSiblingSessions_AreBothReturned()
    {
        WriteJournal("slot2", "dir-b.json", SiblingPid, ("Sibling work", @"C:\repos\b-wt"));
        var own = new[] { new LiveSessionRef { RepoPath = @"C:\repos\a-wt", Label = "Mine (#1)" } };

        var all = MachineLiveSessions.OnThisMachine(own, _root, OwnPid, AliveIsOwnOrSibling);

        Assert.Equal(new[] { @"C:\repos\a-wt", @"C:\repos\b-wt" }, all.Select(s => s.RepoPath).ToArray());
    }

    // ---- against a real repository: the label the Repository screen shows ---------------------------

    [Fact]
    public async Task Inventory_WorktreesWithALiveLocalSession_AreStillLabelledInUse()
    {
        var origin = Path.Combine(_root, "origin.git");
        var primary = Path.Combine(_root, "primary");
        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", origin, primary);
        RunGit(primary, "config", "user.email", "test@cc-director.local");
        RunGit(primary, "config", "user.name", "CC Director Test");
        RunGit(primary, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(primary, "README.md"), "initial\n");
        RunGit(primary, "add", "-A");
        RunGit(primary, "commit", "-m", "initial commit");
        RunGit(primary, "push", "-u", "origin", "main");

        var ownWorktree = Path.Combine(_root, "wt-own");
        var siblingWorktree = Path.Combine(_root, "wt-sibling");
        var idleWorktree = Path.Combine(_root, "wt-idle");
        RunGit(primary, "worktree", "add", "--detach", ownWorktree, "main");
        RunGit(primary, "worktree", "add", "--detach", siblingWorktree, "main");
        RunGit(primary, "worktree", "add", "--detach", idleWorktree, "main");

        // A session in THIS Director (memory) and one in another Director slot (its journal on disk).
        WriteJournal("slot2", "dir-b.json", SiblingPid, ("Sibling work", siblingWorktree));
        var own = new[] { new LiveSessionRef { RepoPath = ownWorktree, Label = "Mine (#1)" } };
        var live = MachineLiveSessions.OnThisMachine(own, _root, OwnPid, AliveIsOwnOrSibling);

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(primary, fetchPrune: false, liveSessions: live);

        Assert.True(inventory.Success, inventory.Error);
        var inUse = inventory.InUseBySession.Select(w => WorktreeReaperService.NormalizePath(w.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(WorktreeReaperService.NormalizePath(ownWorktree), inUse);
        Assert.Contains(WorktreeReaperService.NormalizePath(siblingWorktree), inUse);
        Assert.DoesNotContain(WorktreeReaperService.NormalizePath(idleWorktree), inUse);
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {workingDirectory}: {error}");
    }
}
