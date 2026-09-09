using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The fold that writes every sentence a stop will ever show (mission "Stop a session", Ruling 5). These are
/// the tests that matter most on this mission, because the command line, the Cockpit, the Director window
/// and the phone all render these strings VERBATIM - a wrong sentence here is a wrong sentence on four
/// surfaces, and no client test would catch it.
///
/// The sentences are asserted WHOLE rather than by fragment. A test that checks a headline "contains" the
/// short identifier passes on a headline that says the opposite of what happened.
/// </summary>
public sealed class SessionStopFoldTests
{
    private const string Sid = "9c41e7a2-1111-2222-3333-444455556666";

    private static DirectorStopResult Stopped(
        int? pid = 51884, bool rowRemoved = true, string? worktree = null, bool? dirty = null) => new()
        {
            Killed = true,
            Removed = rowRemoved,
            ProcessId = pid,
            ProcessEnded = true,
            RowRemoved = rowRemoved,
            WorktreePath = worktree,
            WorktreeHadUncommittedChanges = dirty,
            Verdict = SessionStopVerdict.Stopped,
        };

    private static DirectorStopResult AlreadyStopped(bool rowRemoved) => new()
    {
        Killed = true,
        Removed = rowRemoved,
        ProcessId = null,
        ProcessEnded = false,
        RowRemoved = rowRemoved,
        Verdict = SessionStopVerdict.AlreadyStopped,
    };

    // ---------------------------------------------------------------------------------------------------
    // The five headlines.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_live_process_was_ended_and_the_row_removed()
    {
        var folded = SessionStopFold.Fold(Sid, Stopped(), reason: "spawned into the wrong mode", stoppedBy: "machine token");

        Assert.Equal(SessionStopVerdict.Stopped, folded.Verdict);
        Assert.Equal("stopped 9c41e7a2 - process 51884 ended, row removed", folded.Headline);
    }

    [Fact]
    public void A_live_process_was_ended_and_there_was_no_row_to_remove()
    {
        var folded = SessionStopFold.Fold(Sid, Stopped(rowRemoved: false), reason: "why", stoppedBy: "machine token");

        Assert.Equal("stopped 9c41e7a2 - process 51884 ended, no row was left to remove", folded.Headline);
    }

    /// <summary>
    /// Clearing a leftover row is a thing that HAPPENED, and the sentence says so - an operator who is not
    /// told will keep looking for the row.
    /// </summary>
    [Fact]
    public void Nothing_was_running_and_the_row_it_left_behind_was_cleared()
    {
        var folded = SessionStopFold.Fold(Sid, AlreadyStopped(rowRemoved: true), reason: null, stoppedBy: "machine token");

        Assert.Equal(SessionStopVerdict.AlreadyStopped, folded.Verdict);
        Assert.Equal(
            "already stopped 9c41e7a2 - no process was running; the row it left behind has been cleared",
            folded.Headline);
    }

    [Fact]
    public void Nothing_was_running_and_there_was_no_row_either()
    {
        var folded = SessionStopFold.Fold(Sid, AlreadyStopped(rowRemoved: false), reason: null, stoppedBy: "machine token");

        Assert.Equal(
            "already stopped 9c41e7a2 - no process was running, and no row was left to remove",
            folded.Headline);
    }

    /// <summary>
    /// The headline that must never be folded together with "already stopped": that one means a machine WAS
    /// asked and looked. This one has to say, in so many words, that nothing looked.
    /// </summary>
    [Fact]
    public void Not_on_this_fleet_says_that_no_machine_was_asked_and_is_one_line()
    {
        var folded = SessionStopFold.NotOnFleet(Sid, reason: "tidying up", stoppedBy: "machine token");

        Assert.Equal(SessionStopVerdict.NotOnFleet, folded.Verdict);
        Assert.Equal(
            "not on this fleet - nothing in this account carries the id 9c41e7a2, so no machine was asked "
            + "and no machine's processes were searched",
            folded.Headline);
        Assert.DoesNotContain("\n", folded.Headline);
        Assert.DoesNotContain("\r", folded.Headline);
    }

    // ---------------------------------------------------------------------------------------------------
    // The worktree line - Ruling 2 requires it whenever the session held a worktree.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_dirty_worktree_is_named_and_said_to_be_left_untouched()
    {
        var folded = SessionStopFold.Fold(
            Sid, Stopped(worktree: @"C:\Repos\thing", dirty: true), reason: null, stoppedBy: "machine token");

        Assert.Equal(
            new[] { @"the worktree C:\Repos\thing was left untouched - it has uncommitted changes in it" },
            folded.Details.ToArray());
        Assert.True(folded.WorktreeHadUncommittedChanges);
    }

    [Fact]
    public void A_clean_worktree_says_it_had_no_uncommitted_changes()
    {
        var folded = SessionStopFold.Fold(
            Sid, Stopped(worktree: @"C:\Repos\thing", dirty: false), reason: null, stoppedBy: "machine token");

        Assert.Equal(
            new[] { @"the worktree C:\Repos\thing was left untouched - it had no uncommitted changes" },
            folded.Details.ToArray());
    }

    /// <summary>
    /// THE UNKNOWN CASE IS NOT THE CLEAN CASE, and this is the single most likely thing to be got wrong
    /// later - null and false are both falsy in every language this answer passes through, so a fold written
    /// with a plain "if dirty" would silently report a failed probe as a clean tree. That would tell an
    /// operator their work is safely committed on the strength of a check that never returned.
    /// </summary>
    [Fact]
    public void A_worktree_whose_state_could_not_be_determined_gets_its_own_sentence_and_never_the_clean_one()
    {
        var folded = SessionStopFold.Fold(
            Sid, Stopped(worktree: @"C:\Repos\thing", dirty: null), reason: null, stoppedBy: "machine token");

        Assert.Equal(
            new[] { @"the worktree C:\Repos\thing was left untouched - whether it has uncommitted changes could not be determined" },
            folded.Details.ToArray());
        Assert.DoesNotContain(folded.Details, d => d.Contains("had no uncommitted changes"));
        Assert.Null(folded.WorktreeHadUncommittedChanges);
    }

    [Fact]
    public void A_session_that_held_no_worktree_gets_no_worktree_line()
    {
        var folded = SessionStopFold.Fold(Sid, Stopped(), reason: null, stoppedBy: "machine token");

        Assert.Empty(folded.Details);
    }

    // ---------------------------------------------------------------------------------------------------
    // The reason line, and the order of the details.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_worktree_line_comes_first_and_the_reason_line_second()
    {
        var folded = SessionStopFold.Fold(
            Sid, Stopped(worktree: @"C:\Repos\thing", dirty: true),
            reason: "spawned into the wrong mode", stoppedBy: "machine token");

        Assert.Equal(
            new[]
            {
                @"the worktree C:\Repos\thing was left untouched - it has uncommitted changes in it",
                "reason: spawned into the wrong mode",
            },
            folded.Details.ToArray());
    }

    [Fact]
    public void No_reason_means_no_reason_line_and_never_an_invented_one()
    {
        var folded = SessionStopFold.Fold(Sid, Stopped(), reason: null, stoppedBy: "device browser dev-1");

        Assert.DoesNotContain(folded.Details, d => d.StartsWith("reason:", StringComparison.Ordinal));
        Assert.Null(folded.Reason);
    }

    // ---------------------------------------------------------------------------------------------------
    // The short identifier.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_guid_shortens_to_eight_characters()
        => Assert.Equal("9c41e7a2", SessionStopFold.ShortIdFor(Sid));

    [Theory]
    [InlineData("{9c41e7a2-1111-2222-3333-444455556666}")]
    [InlineData("9c41e7a2111122223333444455556666")]
    public void A_guid_written_another_way_shortens_to_the_same_eight_characters(string written)
        => Assert.Equal("9c41e7a2", SessionStopFold.ShortIdFor(written));

    /// <summary>
    /// ANYTHING THAT IS NOT A GUID PASSES THROUGH WHOLE. The not-on-this-fleet case is reached exactly when
    /// the caller typed something no session matched, and that is often a NAME - truncating it would print
    /// "nothing in this account carries the id Stop a s", which reads as a corrupted answer rather than an
    /// honest one.
    /// </summary>
    [Fact]
    public void A_typed_name_is_never_truncated()
    {
        Assert.Equal("Stop a session - Worker", SessionStopFold.ShortIdFor("Stop a session - Worker"));

        var folded = SessionStopFold.NotOnFleet("Stop a session - Worker", reason: "typo", stoppedBy: "machine token");
        Assert.Equal(
            "not on this fleet - nothing in this account carries the id Stop a session - Worker, so no "
            + "machine was asked and no machine's processes were searched",
            folded.Headline);
    }

    // ---------------------------------------------------------------------------------------------------
    // The facts underneath the sentences, and the compatibility pair.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_plain_facts_are_carried_through_beside_the_words()
    {
        var folded = SessionStopFold.Fold(
            Sid, Stopped(worktree: @"C:\Repos\thing", dirty: true), reason: "why", stoppedBy: "session abc");

        Assert.Equal(Sid, folded.SessionId);
        Assert.Equal("9c41e7a2", folded.ShortId);
        Assert.Equal(51884, folded.ProcessId);
        Assert.True(folded.ProcessEnded);
        Assert.True(folded.RowRemoved);
        Assert.Equal(@"C:\Repos\thing", folded.WorktreePath);
        Assert.Equal("why", folded.Reason);
        Assert.Equal("session abc", folded.StoppedBy);
        // The original pair, unchanged, for the callers that only ever read those.
        Assert.True(folded.Killed);
        Assert.True(folded.Removed);
    }

    // ---------------------------------------------------------------------------------------------------
    // An answer this Gateway cannot DESCRIBE. It is still a stop, and it is never a failure.
    //
    // The first implementation refused these with a 502. The Architect reversed it: the session really was
    // stopped, and reporting a failure for an operation that succeeded is the same false report this
    // mission exists to remove. They fold to stoppedNotDescribed instead.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_describable_answer_is_described()
    {
        Assert.True(SessionStopFold.CanDescribe(Stopped()));
        Assert.True(SessionStopFold.CanDescribe(AlreadyStopped(rowRemoved: true)));
    }

    /// <summary>
    /// An older Director answers only the original killed/removed pair. It says the verb RAN, never that a
    /// process was found - so this is neither "stopped" (which would assert a process nobody established)
    /// nor a failure (the session really is stopped).
    /// </summary>
    [Fact]
    public void An_older_Directors_answer_is_a_stop_that_could_not_be_described()
    {
        var legacy = new DirectorStopResult { Killed = true, Removed = true };

        Assert.False(SessionStopFold.CanDescribe(legacy));

        var folded = SessionStopFold.Fold(Sid, legacy, reason: "why", stoppedBy: "machine token");

        Assert.Equal(SessionStopVerdict.StoppedNotDescribed, folded.Verdict);
        Assert.Contains("older version", folded.Headline);
        Assert.Contains("could not say what it found", folded.Headline);
        // It leads with the SUCCESS. An operator must not read this as a failed stop.
        Assert.StartsWith("stopped ", folded.Headline);
        // The compatibility pair carries through from whatever the old Director did report.
        Assert.True(folded.Killed);
        Assert.True(folded.Removed);
    }

    /// <summary>
    /// Nothing is described under this verdict, and nothing is INVENTED either. The empty values mean "not
    /// established", never "established to be false", and the worktree sentence Ruling 2 requires must not
    /// appear - there is no worktree to name.
    /// </summary>
    [Fact]
    public void A_stop_that_could_not_be_described_names_no_process_and_no_worktree()
    {
        var folded = SessionStopFold.Fold(
            Sid, new DirectorStopResult { Killed = true, Removed = true }, reason: "why", stoppedBy: "me");

        Assert.Null(folded.ProcessId);
        Assert.False(folded.ProcessEnded);
        Assert.Null(folded.WorktreePath);
        Assert.Null(folded.WorktreeHadUncommittedChanges);
        Assert.DoesNotContain(folded.Details, d => d.Contains("worktree"));
        // The caller's own words ARE known to this Gateway, so the reason line still belongs.
        Assert.Contains("reason: why", folded.Details);
    }

    /// <summary>A Director that answered nothing readable at all. The tunnel still returned Ok, so the verb
    /// ran; only the description is missing.</summary>
    [Fact]
    public void A_missing_answer_is_a_stop_that_could_not_be_described()
    {
        Assert.False(SessionStopFold.CanDescribe(null));

        var folded = SessionStopFold.Fold(Sid, new DirectorStopResult(), reason: null, stoppedBy: "me");

        Assert.Equal(SessionStopVerdict.StoppedNotDescribed, folded.Verdict);
    }

    /// <summary>
    /// "stopped" means a live process was found and ended, so the identifier of that process is part of the
    /// claim. An answer that says one was ended but not which cannot produce that headline - and is not
    /// refused for it either.
    /// </summary>
    [Fact]
    public void A_stop_that_claims_a_process_but_names_none_is_not_described()
    {
        Assert.False(SessionStopFold.CanDescribe(Stopped(pid: null)));

        var folded = SessionStopFold.Fold(Sid, Stopped(pid: null), reason: "why", stoppedBy: "me");

        Assert.Equal(SessionStopVerdict.StoppedNotDescribed, folded.Verdict);
        Assert.Null(folded.ProcessId);
    }

    // ---------------------------------------------------------------------------------------------------
    // The SECOND cause of "stopped, not described": a CURRENT Director that looked and could not read.
    //
    // Added after inspection 1 (findings I2 and I3). One verdict word, three causes, told apart in words -
    // and, unlike the older-Director cause, the fields under this one carry facts the stop DID establish.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>A Director that read what it could and says, in its own sentence, what it could not.</summary>
    private static DirectorStopResult CouldNotRead(string reason, int? pid = 51884, string? worktree = null,
        bool? dirty = null) => new()
        {
            Killed = true,
            Removed = true,
            ProcessId = pid,
            ProcessEnded = false,
            RowRemoved = true,
            WorktreePath = worktree,
            WorktreeHadUncommittedChanges = dirty,
            NotDescribedReason = reason,
            Verdict = SessionStopVerdict.StoppedNotDescribed,
        };

    /// <summary>
    /// The word is now one a CURRENT Director sends. It is still not a description of a process - which is
    /// what the word means - but it is not the silence of an old Director either, and the fold must not
    /// print the old Director's sentence over the top of the Director's own words.
    /// </summary>
    [Fact]
    public void A_current_Director_that_could_not_read_the_check_says_so_in_its_own_words()
    {
        var answer = CouldNotRead(
            "whether process 51884 was running could not be read before the stop - could not read process "
            + "51884 on this machine (Win32Exception: Access is denied)");

        Assert.False(SessionStopFold.CanDescribe(answer));

        var folded = SessionStopFold.Fold(Sid, answer, reason: "wrong repository", stoppedBy: "machine token");

        Assert.Equal(SessionStopVerdict.StoppedNotDescribed, folded.Verdict);
        Assert.Equal(
            "stopped 9c41e7a2 - whether process 51884 was running could not be read before the stop - could "
            + "not read process 51884 on this machine (Win32Exception: Access is denied)",
            folded.Headline);
        // It must NOT be the older-Director sentence, which would blame the wrong thing entirely.
        Assert.DoesNotContain("older version", folded.Headline);
    }

    /// <summary>
    /// WHAT WAS ESTABLISHED IS STILL REPORTED. The process identifier came off the row, the row really was
    /// removed, and the worktree really was probed - Ruling 2 makes that sentence mandatory whenever the
    /// session held a tree, and it is the line that tells an operator about work they are walking away from.
    /// Only ProcessEnded stays empty, because nothing established it.
    /// </summary>
    [Fact]
    public void A_stop_that_could_not_be_described_still_reports_the_facts_it_did_establish()
    {
        var answer = CouldNotRead(
            "whether process 51884 was running could not be read before the stop - the handle was refused",
            worktree: @"C:\repos\thing", dirty: true);

        var folded = SessionStopFold.Fold(Sid, answer, reason: "wrong repository", stoppedBy: "machine token");

        Assert.Equal(51884, folded.ProcessId);
        Assert.True(folded.RowRemoved);
        Assert.Equal(@"C:\repos\thing", folded.WorktreePath);
        Assert.True(folded.WorktreeHadUncommittedChanges);
        Assert.False(folded.ProcessEnded);   // nothing established it, and it is never assumed
        Assert.Contains(@"the worktree C:\repos\thing was left untouched - it has uncommitted changes in it",
            folded.Details);
        Assert.Contains("reason: wrong repository", folded.Details);
    }

    /// <summary>
    /// A session that carried no process identifier at all - the remote workflow backend while a remote run
    /// is going, and the pipe and studio backends always. Nothing was checked, so nothing may be claimed,
    /// and the sentence says which of the three causes this is.
    /// </summary>
    [Fact]
    public void A_session_with_no_process_identifier_says_nothing_was_checked()
    {
        var answer = CouldNotRead(
            "this session carried no process identifier, so no process could be checked - whether anything "
            + "was running, and whether anything has ended, are not known",
            pid: null);

        var folded = SessionStopFold.Fold(Sid, answer, reason: "finished with it", stoppedBy: "machine token");

        Assert.Equal(SessionStopVerdict.StoppedNotDescribed, folded.Verdict);
        Assert.Equal(
            "stopped 9c41e7a2 - this session carried no process identifier, so no process could be checked - "
            + "whether anything was running, and whether anything has ended, are not known",
            folded.Headline);
        Assert.Null(folded.ProcessId);
        Assert.False(folded.ProcessEnded);
        Assert.True(folded.RowRemoved);
    }

    /// <summary>
    /// The older-Director cause is UNTOUCHED by the second one. Its headline is the same sentence it has
    /// always been, and its fields stay empty - there, nothing was established at all.
    /// </summary>
    [Fact]
    public void An_older_Directors_answer_keeps_its_own_headline_and_its_empty_fields()
    {
        var legacy = new DirectorStopResult { Killed = true, Removed = true };

        var folded = SessionStopFold.Fold(Sid, legacy, reason: "why", stoppedBy: "me");

        Assert.Contains("older version", folded.Headline);
        Assert.Null(folded.ProcessId);
        Assert.False(folded.RowRemoved);
        Assert.Null(folded.WorktreePath);
        Assert.DoesNotContain(folded.Details, d => d.Contains("worktree"));
    }

    // ---------------------------------------------------------------------------------------------------
    // Who asked.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_actor_names_the_calling_session_when_a_session_key_was_used()
        => Assert.Equal($"session {Sid}",
            SessionStopFold.ActorFor(Sid, deviceType: null, deviceId: null, credentialAuthenticated: true));

    [Fact]
    public void The_actor_names_the_device_when_a_device_key_was_used()
        => Assert.Equal("device phone dev-7",
            SessionStopFold.ActorFor(null, "phone", "dev-7", credentialAuthenticated: true));

    [Fact]
    public void The_actor_is_the_machine_token_when_that_is_what_authenticated()
        => Assert.Equal("machine token",
            SessionStopFold.ActorFor(null, null, null, credentialAuthenticated: true));

    /// <summary>
    /// Nothing authenticated means nothing authenticated. The audit trail records "unknown" rather than a
    /// plausible guess at who it might have been, because a guessed actor is worse than an absent one - it
    /// is read later as a fact.
    /// </summary>
    [Fact]
    public void The_actor_is_unknown_when_nothing_authenticated_the_request()
        => Assert.Equal("unknown",
            SessionStopFold.ActorFor(null, null, null, credentialAuthenticated: false));

    // ---------------------------------------------------------------------------------------------------
    // The refusal sentence.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The sentence has to name the REASON as the thing that is missing, and must NOT name a client's flag -
    /// the Gateway does not know what the command line calls its options, and the command line adds that
    /// itself.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_reason_and_no_flag()
    {
        Assert.Contains("reason", SessionStopFold.ReasonMissing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--", SessionStopFold.ReasonMissing);
    }

    /// <summary>When no reason was given the trail says so in words. It never fabricates one.</summary>
    [Fact]
    public void The_audit_detail_for_a_reasonless_stop_says_that_none_was_given()
        => Assert.Contains("no reason was given", SessionStopFold.NoReasonGivenDetail);
}
