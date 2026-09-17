using System.Text.Json;

namespace CcDirector.Avalonia.DevReports;

/// <summary>
/// Every decision the Director's reports pane makes about the page it hosts, as PURE functions
/// (issue #3019). Nothing here touches a web view, a window or the user interface thread, so each rule is
/// proven directly rather than inferred from a control that happened to behave.
///
/// WHAT THE BRIDGE IS FOR. The page the pane shows is served by the Gateway and holds no credential of its
/// own. It asks its host for one, and the Director answers with the key it already holds
/// (<c>GatewayConfig.Load().Token</c>) - in memory, to the page, and nowhere else.
///
/// WHAT IT MUST REFUSE, AND WHY. The report inside that page is written by an agent and lives in a sandboxed
/// frame with no same-origin privilege (CONTRACT.md section 4). The frame must have NO path to this bridge at
/// all, so:
///
///  - Only a message from the TOP-LEVEL document at exactly the pane address earns an answer. WebView2's
///    <c>WebMessageReceived</c> already fires for the top-level document only - the pane never subscribes to
///    <c>FrameCreated</c> and never adds a host object to script - and <see cref="EarnsTheKey"/> is the second
///    lock on the same door: the source address has to be the address the Gateway handed back.
///  - Only the kind <c>dev-report-host-ready</c> earns an answer. Anything else is logged and ignored.
///  - A top-level navigation anywhere but the pane address is cancelled (<see cref="NavigationIsAllowed"/>).
///    A page that has gone somewhere else is not the page we were handed, and a key posted to it would be a
///    key handed to whatever now occupies the pane.
///
/// The key itself never reaches a log line, a screenshot, an error message or a file. This class composes the
/// one message that carries it and returns it to the caller; it writes nothing anywhere.
/// </summary>
public static class DevReportPaneBridge
{
    /// <summary>The only message kind the pane answers: the page saying it has loaded and has no key.</summary>
    public const string HostReadyKind = "dev-report-host-ready";

    /// <summary>The kind of the one message the pane sends: the key, for one session.</summary>
    public const string HostKeyKind = "dev-report-host-key";

    /// <summary>
    /// The empty document. A web view can start on it before the pane navigates, and it holds no content, no
    /// script of ours and no session - so a navigation TO it is allowed rather than fought with, and it can
    /// never earn the key because <see cref="EarnsTheKey"/> demands the exact pane address. Named here, not
    /// matched loosely, so nothing else about: is admitted with it.
    /// </summary>
    public const string EmptyDocument = "about:blank";

    /// <summary>
    /// Whether a web message earns the Gateway key: it came from the top-level document at exactly the pane
    /// address, and its body is the one kind the pane answers.
    /// </summary>
    /// <param name="messageSourceUrl">The address of the document that sent the message.</param>
    /// <param name="paneUrl">The pane address the Gateway handed back.</param>
    /// <param name="messageJson">The message body, as the page sent it.</param>
    /// <param name="refusal">Why it earned nothing; empty when it did. Never contains the key.</param>
    public static bool EarnsTheKey(string? messageSourceUrl, string paneUrl, string? messageJson, out string refusal)
    {
        if (!SameAddress(messageSourceUrl, paneUrl))
        {
            refusal = $"the message came from {Describe(messageSourceUrl)}, which is not the pane address";
            return false;
        }

        var kind = ReadKind(messageJson);
        if (!string.Equals(kind, HostReadyKind, StringComparison.Ordinal))
        {
            refusal = $"the message kind was {Describe(kind)}, not {HostReadyKind}";
            return false;
        }

        refusal = "";
        return true;
    }

    /// <summary>
    /// Whether the top-level document may navigate to this address: only back to the pane address itself, or
    /// to the empty document. Everything else is cancelled, so the pane shows the page it was given or
    /// nothing at all - it never becomes a general browser holding the owner's Gateway key.
    /// </summary>
    public static bool NavigationIsAllowed(string? targetUrl, string paneUrl)
    {
        if (string.Equals(targetUrl?.Trim(), EmptyDocument, StringComparison.OrdinalIgnoreCase)) return true;
        return SameAddress(targetUrl, paneUrl);
    }

    /// <summary>
    /// The one message the pane sends, carrying the key for one session. The session identifier travels with
    /// the key so the page can refuse a key meant for a different session rather than reading with it.
    /// </summary>
    /// <exception cref="ArgumentException">The key or the session identifier is missing.</exception>
    public static string ComposeKeyMessage(string key, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("There is no Gateway key to hand the page.", nameof(key));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("There is no session to hand a key for.", nameof(sessionId));

        return JsonSerializer.Serialize(new { kind = HostKeyKind, key, sessionId });
    }

    /// <summary>
    /// The <c>kind</c> of a message the page sent, or null when the message is not an object with a string
    /// kind. A body that is not JavaScript Object Notation at all is not an error to raise - it is simply not
    /// a message this pane answers - so this returns null rather than throwing.
    /// </summary>
    public static string? ReadKind(string? messageJson)
    {
        if (string.IsNullOrWhiteSpace(messageJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return null;
            return kind.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two addresses name the same page: same scheme, same host, same port and the same path. The
    /// query and the fragment are not compared, because they carry no part of WHICH session's page this is -
    /// the session identifier is the last path segment - and a single-page application writes to them. One
    /// trailing slash on the path is the same page; a deeper path is not.
    /// </summary>
    public static bool SameAddress(string? candidate, string paneUrl)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(paneUrl)) return false;
        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(paneUrl.Trim(), UriKind.Absolute, out var b)) return false;

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
               && a.Port == b.Port
               && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    /// <summary>
    /// A short, safe description of something that arrived, for a log line. Never a credential - the only
    /// values passed here are an address the page was at and the kind it claimed, both of which the page
    /// chose. It is capped because the page chose them: a report that wants to fill the Director's log picks
    /// a megabyte-long kind, and a refusal reason is a diagnosis, not a transcript.
    /// </summary>
    private static string Describe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(nothing)";
        var text = value.Trim();
        return text.Length <= DescriptionLimit ? text : text[..DescriptionLimit] + "... (truncated)";
    }

    /// <summary>How much of a page-chosen string a refusal reason repeats.</summary>
    private const int DescriptionLimit = 200;
}
