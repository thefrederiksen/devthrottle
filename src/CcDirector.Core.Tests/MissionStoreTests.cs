using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for <see cref="MissionStore"/> - the durable record store behind the first-class Mission
/// (fleet.html). Mirrors <see cref="SessionStateStoreTests"/>: each test uses a
/// throwaway temp file and asserts the create / get / list / delete API plus survival across a fresh store
/// instance (the "survives a restart" guarantee), and the session-side MissionId/MissionName round-trip
/// through <see cref="PersistedSession"/>.
///
/// These are the SINGLE-TENANT behaviours - one owner, everything Local - which is what a Director and a
/// self-host Gateway are. The partitioning the store gained in #1039 is exercised separately in
/// <see cref="MissionStoreTenantPartitionTests"/>.
/// </summary>
public class MissionStoreTests
{
    /// <summary>A single-tenant store: one owner, so unattributed rows are that owner's.</summary>
    private static MissionStore NewStore(string path) => new(path, adoptUnattributedAs: TenantId.Local);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json");

    [Fact]
    public void Create_MintsIdAndPersists_GetReturnsIt()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);

            var mission = store.Create(TenantId.Local, "Session Lifecycle");

            Assert.NotEqual(Guid.Empty, mission.MissionId);
            Assert.Equal("Session Lifecycle", mission.MissionName);

            var fetched = store.Get(TenantId.Local, mission.MissionId);
            Assert.NotNull(fetched);
            Assert.Equal(mission.MissionId, fetched.MissionId);
            Assert.Equal("Session Lifecycle", fetched.MissionName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // Create_WithParent_NestsUnderIt was removed on 2026-08-07 along with mission nesting itself. Missions
    // are flat: the parent link was specified, built and tested, then never used once, so it went rather
    // than being carried indefinitely. See Mission.cs.

    [Fact]
    public void Create_BlankName_Throws()
    {
        var store = NewStore(TempPath());
        Assert.Throws<ArgumentException>(() => store.Create(TenantId.Local, "   "));
    }

    [Fact]
    public void List_ReturnsEveryMission_OldestFirst()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var first = store.Create(TenantId.Local, "First");
            var second = store.Create(TenantId.Local, "Second");

            var all = store.List(TenantId.Local);

            Assert.Equal(2, all.Count);
            Assert.Equal(first.MissionId, all[0].MissionId);
            Assert.Equal(second.MissionId, all[1].MissionId);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var store = NewStore(TempPath());
        Assert.Null(store.Get(TenantId.Local, Guid.NewGuid()));
    }

    [Fact]
    public void Missions_SurviveANewStoreInstance_PersistReload()
    {
        var tempFile = TempPath();
        try
        {
            var created = NewStore(tempFile).Create(TenantId.Local, "Durable Mission");

            // A brand-new store instance against the same file models a Director restart.
            var reopened = NewStore(tempFile);
            var fetched = reopened.Get(TenantId.Local, created.MissionId);

            Assert.NotNull(fetched);
            Assert.Equal("Durable Mission", fetched.MissionName);
            Assert.Single(reopened.List(TenantId.Local));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Delete_RemovesMission_AndReturnsTrue()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Doomed Mission");

            var removed = store.Delete(TenantId.Local, mission.MissionId);

            Assert.True(removed);
            Assert.Null(store.Get(TenantId.Local, mission.MissionId));
            Assert.Empty(store.List(TenantId.Local));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Delete_UnknownId_ReturnsFalse()
    {
        var store = NewStore(TempPath());
        Assert.False(store.Delete(TenantId.Local, Guid.NewGuid()));
    }

    [Fact]
    public void PersistedSession_MissionAttachment_SurvivesRoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);
            var missionId = Guid.NewGuid();

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-session-mission",
                MissionId = missionId,
                MissionName = "Session Lifecycle",
                ActivityState = ActivityState.Idle,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            store.Save(new[] { session });
            var result = store.Load();

            Assert.True(result.Success);
            Assert.Single(result.Sessions);
            Assert.Equal(missionId, result.Sessions[0].MissionId);
            Assert.Equal("Session Lifecycle", result.Sessions[0].MissionName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // ---- the WHY, which lives ON the mission (Phase 1) ------------------------------------------------

    [Fact]
    public void SetWhy_StoresItAndSurvivesAFreshStore()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Release 2.0.1");
            var now = DateTimeOffset.UtcNow;

            var updated = store.SetWhy(TenantId.Local, mission.MissionId, "  So we can ship the video  ", now);

            Assert.NotNull(updated);
            Assert.Equal("So we can ship the video", updated.Why);
            Assert.Equal(now, updated.WhyUpdatedAt);

            // The whole point of moving it here: it is durable, keyed by the mission id.
            var reopened = NewStore(tempFile);
            var fetched = reopened.Get(TenantId.Local, mission.MissionId);
            Assert.NotNull(fetched);
            Assert.Equal("So we can ship the video", fetched.Why);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void SetWhy_BlankClearsItAndDropsTheTimestamp()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Release 2.0.1");
            store.SetWhy(TenantId.Local, mission.MissionId, "a reason", DateTimeOffset.UtcNow);

            var cleared = store.SetWhy(TenantId.Local, mission.MissionId, "   ", DateTimeOffset.UtcNow);

            Assert.NotNull(cleared);
            Assert.Equal("", cleared.Why);
            // Unset, not "set to empty at a moment" - so the card shows its flag with nothing behind it.
            Assert.Null(cleared.WhyUpdatedAt);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void SetWhy_UnknownMission_ReturnsNull()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            Assert.Null(store.SetWhy(TenantId.Local, Guid.NewGuid(), "why", DateTimeOffset.UtcNow));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ImportWhys_FillsByNormalizedName_AndIsIdempotent()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var release = store.Create(TenantId.Local, "Release 2.0.1");
            var other = store.Create(TenantId.Local, "Banya");

            var map = new Dictionary<string, string>
            {
                ["release 2.0.1"] = "So we can get the Video Competition started",
            };

            Assert.Equal(1, store.ImportWhys(TenantId.Local, map));
            Assert.Equal("So we can get the Video Competition started",
                store.Get(TenantId.Local, release.MissionId)!.Why);
            Assert.Equal("", store.Get(TenantId.Local, other.MissionId)!.Why);

            // Running it again fills nothing - the mission already has a WHY.
            Assert.Equal(0, store.ImportWhys(TenantId.Local, map));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ImportWhys_NeverOverwritesAWhyThatIsAlreadySet()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Release 2.0.1");
            store.SetWhy(TenantId.Local, mission.MissionId, "the current reason", DateTimeOffset.UtcNow);

            var filled = store.ImportWhys(TenantId.Local, new Dictionary<string, string>
            {
                ["release 2.0.1"] = "a stale reason from the old note store",
            });

            Assert.Equal(0, filled);
            Assert.Equal("the current reason", store.Get(TenantId.Local, mission.MissionId)!.Why);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ImportWhys_FillsEveryMissionSharingAName()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            // Two missions with the same name is exactly the case the old name-keyed store could not tell
            // apart - it showed one WHY on both cards. The migration preserves what was on screen.
            var first = store.Create(TenantId.Local, "Release");
            var second = store.Create(TenantId.Local, "release");

            var filled = store.ImportWhys(TenantId.Local, new Dictionary<string, string>
            {
                ["release"] = "one shared reason",
            });

            Assert.Equal(2, filled);
            Assert.Equal("one shared reason", store.Get(TenantId.Local, first.MissionId)!.Why);
            Assert.Equal("one shared reason", store.Get(TenantId.Local, second.MissionId)!.Why);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // ---- rename and ending a mission (Phase 2) -------------------------------------------------------

    [Fact]
    public void Rename_ChangesTheDisplayName_AndKeepsIdAndWhy()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Release 2.0.0");
            store.SetWhy(TenantId.Local, mission.MissionId, "the reason", DateTimeOffset.UtcNow);

            var renamed = store.Rename(TenantId.Local, mission.MissionId, "  Release 2.0.1  ");

            Assert.NotNull(renamed);
            Assert.Equal("Release 2.0.1", renamed.MissionName);
            // The identity does not move, and neither does the WHY - which is the whole reason the WHY had
            // to stop being keyed by name before rename existed.
            Assert.Equal(mission.MissionId, renamed.MissionId);
            Assert.Equal("the reason", renamed.Why);

            var reopened = NewStore(tempFile);
            Assert.Equal("Release 2.0.1", reopened.Get(TenantId.Local, mission.MissionId)!.MissionName);
            Assert.Equal("the reason", reopened.Get(TenantId.Local, mission.MissionId)!.Why);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Rename_BlankThrows_AndUnknownReturnsNull()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Release");

            Assert.Throws<ArgumentException>(() => store.Rename(TenantId.Local, mission.MissionId, "  "));
            Assert.Null(store.Rename(TenantId.Local, Guid.NewGuid(), "Anything"));
            // The refused rename changed nothing.
            Assert.Equal("Release", store.Get(TenantId.Local, mission.MissionId)!.MissionName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void A_new_mission_is_active_and_listed()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Release 2.0.1");

            Assert.Equal(MissionStates.Active, mission.State);
            Assert.True(mission.IsActive);
            Assert.Null(mission.StateChangedAt);
            Assert.Single(store.List(TenantId.Local));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(MissionStates.Complete)]
    [InlineData(MissionStates.Removed)]
    public void An_ended_mission_leaves_the_default_list_but_is_still_there(string ending)
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Remove the network port");
            var now = DateTimeOffset.UtcNow;

            var ended = store.SetState(TenantId.Local, mission.MissionId, ending, now);

            Assert.NotNull(ended);
            Assert.Equal(ending, ended.State);
            Assert.Equal(now, ended.StateChangedAt);
            Assert.False(ended.IsActive);

            // Gone from the default view...
            Assert.Empty(store.List(TenantId.Local));
            // ...but NOT gone. Removed is a soft delete: the record stays, and the id still resolves.
            Assert.NotNull(store.Get(TenantId.Local, mission.MissionId));
            Assert.Single(store.List(TenantId.Local, includeEnded: true));
            Assert.Single(store.List(TenantId.Local, state: ending));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void An_ended_mission_can_be_reopened()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Ended by mistake");
            store.SetState(TenantId.Local, mission.MissionId, MissionStates.Complete, DateTimeOffset.UtcNow);
            Assert.Empty(store.List(TenantId.Local));

            var reopened = store.SetState(TenantId.Local, mission.MissionId, MissionStates.Active,
                DateTimeOffset.UtcNow);

            Assert.NotNull(reopened);
            Assert.True(reopened.IsActive);
            Assert.Single(store.List(TenantId.Local));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void SetState_RejectsAnythingElse_AndUnknownReturnsNull()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Release");

            Assert.Throws<ArgumentException>(() =>
                store.SetState(TenantId.Local, mission.MissionId, "archived", DateTimeOffset.UtcNow));
            Assert.Null(store.SetState(TenantId.Local, Guid.NewGuid(), MissionStates.Complete,
                DateTimeOffset.UtcNow));
            Assert.True(store.Get(TenantId.Local, mission.MissionId)!.IsActive);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void A_record_written_before_states_existed_reads_as_active()
    {
        var tempFile = TempPath();
        try
        {
            // No State property at all - exactly what every missions.json on disk holds today.
            File.WriteAllText(tempFile,
                "[{\"MissionId\":\"" + Guid.NewGuid() + "\",\"MissionName\":\"Older Mission\"," +
                "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"TenantId\":null}]");

            var store = NewStore(tempFile);
            var listed = store.List(TenantId.Local);

            Assert.Single(listed);
            Assert.True(listed[0].IsActive);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
