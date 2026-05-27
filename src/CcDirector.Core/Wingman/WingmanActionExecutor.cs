using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// The SOLE place Wingman code writes to a session's PTY. Everything the Wingman wants to
/// do to a terminal (type, press keys, submit) funnels through <see cref="Execute"/>, so
/// the audit log, the self-injection guard, and the idempotency check live in exactly one
/// spot. The charter audit (<c>WingmanCharterAuditTests</c>) asserts this is the only file
/// under src/CcDirector.Core/Wingman/ that calls a Session write method.
///
/// The decision of WHAT to do is made elsewhere (a tool-less strong-model side-call in
/// <see cref="WingmanService.DecideSessionActionAsync"/>); this class only carries out a
/// validated <see cref="WingmanAction"/>. It never calls an LLM.
/// </summary>
public static class WingmanActionExecutor
{
    /// <summary>
    /// How long after an injection the <c>TerminalStateDetector</c> must ignore byte
    /// activity, so the Wingman does not react to the repaint/echo its own keystrokes
    /// cause (the same self-injection hazard the resize path guards against). Kept well
    /// under the detector's quiet threshold.
    /// </summary>
    public static readonly TimeSpan InjectionSuppression = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Refuse to act again on an identical screen within this window, so a repeated request
    /// (e.g. a double-tap) cannot re-inject onto a screen the Wingman just acted on before the
    /// agent has moved. Actuation is request-driven only (see WINGMAN.md invariant 8); this is
    /// not a throttle on any self-triggering, because there is none.
    /// </summary>
    public static readonly TimeSpan ActionCooldown = TimeSpan.FromSeconds(3);

    /// <summary>Named keys the Wingman may press, mapped to the bytes a real terminal sends.</summary>
    public static readonly IReadOnlyDictionary<string, byte[]> KeyChords =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"]     = new byte[] { 0x0D },
            ["Esc"]       = new byte[] { 0x1B },
            ["Tab"]       = new byte[] { 0x09 },
            ["Space"]     = new byte[] { 0x20 },
            ["Up"]        = new byte[] { 0x1B, 0x5B, 0x41 },
            ["Down"]      = new byte[] { 0x1B, 0x5B, 0x42 },
            ["Right"]     = new byte[] { 0x1B, 0x5B, 0x43 },
            ["Left"]      = new byte[] { 0x1B, 0x5B, 0x44 },
            ["Ctrl+C"]    = new byte[] { 0x03 },
            ["Backspace"] = new byte[] { 0x7F },
        };

    /// <summary>
    /// Carry out <paramref name="action"/> on <paramref name="session"/> and return what
    /// happened. Pure side effect on the session; never throws for an expected condition
    /// (exited session, unknown key, duplicate screen) - those come back as a status.
    /// </summary>
    public static WingmanActResult Execute(Session session, WingmanAction action)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(action);

        var result = new WingmanActResult
        {
            Action = action.Action,
            Text = action.Text,
            Reason = action.Reason,
        };
        result.Keys.AddRange(action.Keys);

        if (action.Action == WingmanAction.ActNone)
            return result; // Performed=false, Status="ok"

        if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
        {
            result.Status = WingmanActResult.StatusSessionGone;
            FileLog.Write($"[WingmanActionExecutor] session={session.Id} gone ({session.Status}); action={action.Action} dropped");
            return result;
        }

        // Idempotency / self-injection guard: don't re-act on a screen we just acted on.
        var (rows, _, _) = session.SnapshotScreenRowsWithCursor();
        var screenHash = HashRows(rows);
        if (screenHash == session.LastActedScreenHash
            && session.LastWingmanInjectionAt is DateTime last
            && DateTime.UtcNow - last < ActionCooldown)
        {
            result.Status = WingmanActResult.StatusSuppressed;
            FileLog.Write($"[WingmanActionExecutor] session={session.Id} suppressed (same screen within cooldown); action={action.Action}");
            return result;
        }

        if (!TryBuildBytes(action, out var bytes, out var detail))
        {
            result.Status = WingmanActResult.StatusBadRequest;
            result.Error = detail;
            FileLog.Write($"[WingmanActionExecutor] session={session.Id} bad action: {detail}");
            return result;
        }

        // Tell the detector the burst these bytes provoke is OURS, not the agent producing
        // output, then write through the same path a human keystroke uses.
        session.SuppressActivityFor(InjectionSuppression);
        session.SendInput(bytes);
        session.MarkWingmanInjection(screenHash);
        session.RecordWingmanAction(new Session.WingmanActionRecord(DateTime.UtcNow, action.Action, detail, action.Reason));
        FileLog.Write($"[WingmanActionExecutor] session={session.Id} performed {action.Action}: {detail}");

        result.Performed = true;
        return result;
    }

    /// <summary>Translate a validated action into the exact bytes to write. Returns false
    /// (with a reason in <paramref name="detail"/>) for an unknown key or empty payload.</summary>
    private static bool TryBuildBytes(WingmanAction action, out byte[] bytes, out string detail)
    {
        bytes = Array.Empty<byte>();
        detail = "";
        switch (action.Action)
        {
            case WingmanAction.ActType:
                if (string.IsNullOrEmpty(action.Text)) { detail = "type with empty text"; return false; }
                bytes = Encoding.UTF8.GetBytes(action.Text);
                detail = $"type \"{Trunc(action.Text)}\"";
                return true;

            case WingmanAction.ActSubmit:
                if (string.IsNullOrEmpty(action.Text)) { detail = "submit with empty text"; return false; }
                bytes = Encoding.UTF8.GetBytes(action.Text).Concat(KeyChords["Enter"]).ToArray();
                detail = $"submit \"{Trunc(action.Text)}\"";
                return true;

            case WingmanAction.ActSendKeys:
                if (action.Keys.Count == 0) { detail = "send_keys with no keys"; return false; }
                var buf = new List<byte>();
                foreach (var name in action.Keys)
                {
                    if (!KeyChords.TryGetValue(name, out var chord)) { detail = $"unknown key '{name}'"; return false; }
                    buf.AddRange(chord);
                }
                bytes = buf.ToArray();
                detail = $"keys [{string.Join(", ", action.Keys)}]";
                return true;

            default:
                detail = $"unknown action '{action.Action}'";
                return false;
        }
    }

    private static string HashRows(IReadOnlyList<string> rows)
    {
        var joined = string.Join("\n", rows.Where(r => !string.IsNullOrWhiteSpace(r)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash, 0, 8); // 16 hex chars is plenty to compare screens
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
