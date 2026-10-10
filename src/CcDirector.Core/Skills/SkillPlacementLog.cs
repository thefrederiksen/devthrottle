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
                var all = ReadUnlocked(path);
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
                File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
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
                return ReadUnlocked(PathFor(rootOverride));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillPlacementLog] could not read the placement record FAILED: {ex.Message}");
            return Array.Empty<SkillPlacementRecord>();
        }
    }

    private static List<SkillPlacementRecord> ReadUnlocked(string path)
    {
        if (!File.Exists(path))
            return new List<SkillPlacementRecord>();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new List<SkillPlacementRecord>();
        return JsonSerializer.Deserialize<List<SkillPlacementRecord>>(text, Json)
               ?? new List<SkillPlacementRecord>();
    }
}
