using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// What the content rule WOULD have decided, written down so the decision can be argued from
/// numbers rather than from impressions. One line of JSON per check, one file per session, at
/// <c>%LOCALAPPDATA%/cc-director/turn-detection-shadow/&lt;sessionId&gt;.jsonl</c>.
///
/// IT IS LOCAL ON PURPOSE. The obvious place for a shadow verdict is the activity ledger, and the
/// activity ledger is the wrong place: its rows live on the hosted Gateway, where a session key is
/// refused, so a verdict written only there is a number nobody on the machine that produced it can
/// read. The comparison this exists for has to be answerable on the owner's own Director.
///
/// ONE ROW PER CHECK, NEVER PER BYTE. A check is scheduled once per burst and pushed out by later
/// bytes inside its window, so a chattering agent costs one row per burst rather than one per
/// write. Each row carries both candidates' verdicts side by side and what the old byte rule would
/// have done, which is the whole point: a row with the byte rule opening and both candidates
/// holding is a phantom turn that would have been suppressed.
///
/// THE RULE SWITCH DEFAULTS OFF AND THIS LOG DEFAULTS ON. That pairing is deliberate - the owner's
/// Director produces the comparison numbers while behaving exactly as it does today.
///
/// Write-only and infrequent, so a single process-wide lock is plenty. Append failures are logged
/// and swallowed: an observation log must never affect the session it observes.
/// </summary>
public static class TurnDetectionShadowLog
{
    /// <summary>Set this to <c>0</c> to stop writing the shadow verdicts.</summary>
    public const string EnabledVariable = "CC_DIRECTOR_TURN_SHADOW";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Resolved per access, not a baked static field, so CC_DIRECTOR_ROOT redirects it
    /// under test. A baked field is captured at type load and no test can undo it - which is how
    /// the state-change log's own test came to write into the real running Director's data.</summary>
    private static string Root => CcStorage.TurnDetectionShadow();

    /// <summary>
    /// On by default. The rule it observes is off by default, so out of the box a Director behaves
    /// exactly as it does today AND produces the numbers that decide whether to turn the rule on.
    /// </summary>
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable(EnabledVariable) != "0";

    /// <summary>
    /// One check.
    /// </summary>
    /// <param name="T">ISO-8601 UTC timestamp of the check.</param>
    /// <param name="Agent">Which agent's terminal this was, because the marker list is per-agent.</param>
    /// <param name="Mode">How the check was reached: "byte" for an ordinary agent, "body" for one
    /// whose idle terminal never goes byte-silent.</param>
    /// <param name="Rule">Which candidate was AUTHORITATIVE at the time: "off", "row" or "size".</param>
    /// <param name="ByteRuleOpens">What today's rule would have done. Always true at a check, because
    /// a check only happens when a byte arrived at a settled session - which is exactly why the
    /// column is worth writing down rather than assuming.</param>
    /// <param name="RowRuleOpens">What the row candidate decided.</param>
    /// <param name="RowEvidence">The first row the row candidate called new, verbatim, so a wrong
    /// decision can be read back. Null when it decided nothing was gained.</param>
    /// <param name="SizeRuleOpens">What the size candidate decided, at <paramref name="SizeThreshold"/>.</param>
    /// <param name="ChangedCharacters">The size candidate's magnitude, whatever it decided. Recorded
    /// on every row so the threshold can be re-chosen from the log rather than re-run.</param>
    /// <param name="SizeThreshold">The threshold the size verdict was taken at.</param>
    /// <param name="SettledHash">Hash of the settled screen, and</param>
    /// <param name="CurrentHash">hash of the screen at the check - the pair is what matches a row
    /// back to a saved turn-review capture.</param>
    /// <param name="Bytes">How many bytes arrived in the burst that produced this check.</param>
    /// <param name="Submitted">True when a submission inside the window explains this wake, which
    /// makes the row a control rather than a candidate phantom.</param>
    public sealed record Record(
        string T,
        string Agent,
        string Mode,
        string Rule,
        bool ByteRuleOpens,
        bool RowRuleOpens,
        string? RowEvidence,
        bool SizeRuleOpens,
        int ChangedCharacters,
        int SizeThreshold,
        string? SettledHash,
        string? CurrentHash,
        long Bytes,
        bool Submitted);

    public static void Append(Guid sessionId, Record record)
    {
        if (!Enabled || record is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Root);
                var path = Path.Combine(Root, sessionId.ToString("N") + ".jsonl");
                File.AppendAllText(path, JsonSerializer.Serialize(record, Json) + "\n", Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnDetectionShadowLog] append failed for {sessionId}: {ex.Message}");
        }
    }
}
