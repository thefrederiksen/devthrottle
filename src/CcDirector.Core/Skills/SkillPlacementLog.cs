using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>One recorded placement outcome, as it sits on disk between the launch and the next push.</summary>
public sealed class SkillPlacementRecord
{
    public string AgentKind { get; set; } = "";
    public int Held { get; set; }
    public int Reachable { get; set; }
    public bool StoreMissing { get; set; }
    public List<SkillPlacementRecordProblem> Problems { get; set; } = new();
    public DateTime ObservedAtUtc { get; set; }
}

/// <summary>One skill that did not arrive, as recorded.</summary>
public sealed class SkillPlacementRecordProblem
{
    public string SkillId { get; set; } = "";
    public string Target { get; set; } = "";
    public string Fault { get; set; } = "";
}

/// <summary>
/// The Director's local record of how skill placement last went, one entry per agent family.
///
/// WHY A FILE SITS BETWEEN THE TWO HALVES. Placement happens on the LAUNCH path, which must never touch
/// the network - a session waiting on an HTTP call to start is a worse product than a session with a
/// missing skill. Reporting happens on the NETWORK path, which must never be on the launch path. So the
/// launch writes a small local record and the existing Gateway cycle picks it up. Same two-half split the
/// rest of this feature uses, for the same reason.
///
/// LAST OUTCOME ONLY, PER AGENT. This answers "is placement working now", so a history would be storage
/// bought for a question nobody asks - and a stale entry that outlived its fix would report a problem that
/// no longer exists, which is worse than reporting nothing.
///
/// It never throws at its callers. A Director that cannot write this file has a disk problem, and losing a
/// diagnostic must not be able to stop a session starting or a Gateway cycle running.
/// </summary>
public static class SkillPlacementLog
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly object Gate = new();

    /// <summary>Where the record lives. Under the Director's own storage, never in a user directory.</summary>
    public static string PathFor(string? rootOverride = null) =>
        Path.Combine(rootOverride ?? CcStorage.Root(), "skills", "placement-state.json");

    /// <summary>
    /// Record the outcome for one agent, replacing that agent's previous entry. A placement that expected
    /// nothing - a raw terminal, or a machine whose library is empty - is NOT recorded: reporting "nothing
    /// to do" as a row would fill the fleet view with entries nobody can act on and bury the one that
    /// matters.
    /// </summary>
    public static void Record(SkillPlacement placement, string? rootOverride = null)
    {
        if (placement is null || placement.NothingExpected)
            return;

        try
        {
            lock (Gate)
            {
                var path = PathFor(rootOverride);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var all = ReadForWriting(path);
                all.RemoveAll(r => string.Equals(r.AgentKind, placement.Kind.ToString(), StringComparison.OrdinalIgnoreCase));
                all.Add(new SkillPlacementRecord
                {
                    AgentKind = placement.Kind.ToString(),
                    Held = placement.Held,
                    Reachable = placement.Reachable,
                    StoreMissing = placement.StoreMissing,
                    ObservedAtUtc = DateTime.UtcNow,
                    Problems = placement.Problems.Select(p => new SkillPlacementRecordProblem
                    {
                        SkillId = p.SkillId,
                        Target = p.Target,
                        Fault = p.Fault.ToString(),
                    }).ToList(),
                });
                // ATOMICALLY, BECAUSE THE WRITERS ARE PROCESSES AND NOT JUST THREADS (issue #4552).
                // The lock above is this process's. File.WriteAllText truncates and then writes, so two
                // Directors sharing a root - or a Director and a session with a temporary home under /tmp -
                // could interleave, and a second write shorter than the first left the tail of the first
                // behind it: one file holding two JSON documents, which every later read then logged as a
                // failure. About 2,000 of those reports came off one machine in three days.
                AtomicFileReplace.WriteAllText(path, JsonSerializer.Serialize(all, Json));
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillPlacementLog] could not record placement for {placement.Kind} FAILED: {ex.Message}");
        }
    }

    /// <summary>Everything recorded so far, newest entry per agent. Empty when nothing has been placed or
    /// the record cannot be read - never throws, because a diagnostic that breaks the caller reading it is
    /// worse than no diagnostic.</summary>
    public static IReadOnlyList<SkillPlacementRecord> ReadAll(string? rootOverride = null)
    {
        try
        {
            lock (Gate)
            {
                var path = PathFor(rootOverride);
                try
                {
                    return ReadUnlocked(path);
                }
                catch (JsonException ex)
                {
                    // THE READER HEALS, BECAUSE THE READER IS WHAT WAS STORMING (the review of #4552). This
                    // is called every sixty seconds by ControlApiHost.PushSkillPlacementAsync, and a strict
                    // read of a damaged file reported a fresh failure on every one of those ticks - which is
                    // the cadence of the 2,000 reports in three days. Healing only on the write path left a
                    // quiet machine, one that is not launching sessions, reporting for ever and never
                    // repairing.
                    //
                    // DELETING IT IS SAFE AND IS THE WHOLE POINT. The file holds one row per agent family,
                    // each rewritten by that agent's next placement; unparseable content is not recoverable
                    // by anybody, and keeping it buys nothing but another report a minute from now.
                    // not-an-error: a diagnostic nobody can read is removed so it stops lying; the damage it held was reported already
                    FileLog.Write($"[SkillPlacementLog] {path} is not readable JSON ({ex.Message}); it is "
                                  + "being removed so it stops being reported once a minute (issue #4552).");
                    TryDelete(path);
                    return Array.Empty<SkillPlacementRecord>();
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillPlacementLog] could not read the placement record FAILED: {ex.Message}");
            return Array.Empty<SkillPlacementRecord>();
        }
    }

    /// <summary>
    /// The record as it stands, for a write that is about to replace all of it - and EMPTY when it cannot be
    /// read, so a file that is already damaged is replaced instead of reported for ever (issue #4552).
    ///
    /// The atomic write above stops new damage; it cannot undo what is already on disk. Until this existed,
    /// an unparseable record threw here, the caller's catch swallowed it, and the write that would have
    /// healed the file never happened - so every placement from then on logged the same failure and changed
    /// nothing. One machine reported about 2,000 of them in three days. Losing the old rows is the point:
    /// each is one agent's last placement, each is rewritten by that agent's next one, and a diagnostic
    /// nobody can read is worth less than an empty one that starts working again.
    /// </summary>
    /// <summary>Remove the damaged record. A failure here is left to the caller's own catch: if it cannot be
    /// deleted it will be read again, and one more line is better than a throw out of a reader.</summary>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        // not-an-error: the record stays and is read again next tick; nothing is lost that was not already unreadable
        catch (Exception ex) { FileLog.Write($"[SkillPlacementLog] {path} could not be removed ({ex.Message})"); }
    }

    private static List<SkillPlacementRecord> ReadForWriting(string path)
    {
        try
        {
            return ReadUnlocked(path);
        }
        catch (JsonException ex)
        {
            // ONLY A JsonException, and the review of #4552 is why. This caught every Exception at first,
            // which meant a TRANSIENT failure - a sharing violation, a slow network home, a full disk - made
            // the very next placement wipe every other agent's row. Unparseable content is damage and cannot
            // get better; an IOException is a bad moment and must reach Record's own catch, which changes
            // nothing on disk.
            // The damage this repairs was already reported about 2,000 times from one machine in three days.
            // not-an-error: replacing a damaged diagnostic IS the repair; reporting it files the same fault again
            FileLog.Write($"[SkillPlacementLog] {path} is not readable JSON ({ex.Message}); this placement "
                          + "replaces the whole record rather than reporting it again (issue #4552).");
            return new List<SkillPlacementRecord>();
        }
    }

    private static List<SkillPlacementRecord> ReadUnlocked(string path)
    {
        if (!File.Exists(path))
            return new List<SkillPlacementRecord>();
        var text = ReadSharingDelete(path);
        if (string.IsNullOrWhiteSpace(text))
            return new List<SkillPlacementRecord>();
        return JsonSerializer.Deserialize<List<SkillPlacementRecord>>(text, Json)
               ?? new List<SkillPlacementRecord>();
    }

    /// <summary>
    /// Read the record while another process may be replacing it. <see cref="AtomicFileReplace"/> renames a
    /// new file over this one, and on Windows that rename is refused unless every open reader asked for
    /// <see cref="FileShare.Delete"/> - which <c>File.ReadAllText</c> does not. A reader that does not ask
    /// for it turns the atomic write it was meant to cooperate with back into a failure.
    /// </summary>
    private static string ReadSharingDelete(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
