using System.Net;
using System.Text;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #531 QA tooling: the fixtures the Wingman Text translation is judged against and
/// the HTML report renderer that puts each agent reply beside the wingman's spoken
/// translation. The renderer is shared by the offline test (fake brain, proves the format)
/// and by a live capture against the real configured wingman (reuses Render with live=true).
/// Kept ASCII-only so it renders correctly in every terminal and viewer.
/// </summary>
public static class WingmanQaFixtures
{
    public sealed class Fixture
    {
        public string Label { get; init; } = "";
        public string UserMessage { get; init; } = "";
        public string AgentReply { get; init; } = "";
        /// <summary>A representative good spoken form, used only to render the report offline.</summary>
        public string ExpectedSpokenStandIn { get; init; } = "";
    }

    /// <summary>
    /// The cases that matter for the user's bar: a direct answer must keep its facts; written
    /// content must be carried, not gutted; a question the agent asked must be surfaced; code
    /// must be described, never read; non-Latin text must survive; a long multi-finding reply
    /// must stay faithful while readable.
    /// </summary>
    public static readonly IReadOnlyList<Fixture> All = new[]
    {
        new Fixture
        {
            Label = "Direct yes/no answer with facts",
            UserMessage = "Did the build pass?",
            AgentReply = "Yes. The build is clean: zero warnings, zero errors, and all 73 tests passed in 0.4 seconds.",
            ExpectedSpokenStandIn = "Yes, the build is clean. Zero warnings, zero errors, and all 73 tests passed in under half a second.",
        },
        new Fixture
        {
            Label = "Agent wrote real content (must be carried)",
            UserMessage = "Summarize what you found in the auth module.",
            AgentReply = "I found three issues in the auth module. First, the session token is never refreshed, so long sessions silently expire. Second, the password reset link does not expire. Third, failed-login attempts are not rate limited, so the endpoint is open to brute force.",
            ExpectedSpokenStandIn = "I found three issues in the auth module. The session token is never refreshed, so long sessions silently expire. The password reset link never expires. And failed logins are not rate limited, so the endpoint is open to brute force.",
        },
        new Fixture
        {
            Label = "Agent asked the user a question (must surface it)",
            UserMessage = "Go ahead and clean up the old migrations.",
            AgentReply = "Before I delete anything: there are two old migration files that look unused, but one of them is referenced in a comment in the seed script. Do you want me to delete both, keep the referenced one, or stop and let you check first?",
            ExpectedSpokenStandIn = "Before deleting anything, I need a decision. There are two old migration files that look unused, but one is referenced in the seed script. Do you want me to delete both, keep the referenced one, or stop so you can check first?",
        },
        new Fixture
        {
            Label = "Code-heavy reply (describe, do not read code)",
            UserMessage = "What did you change to fix the timeout?",
            AgentReply = "I changed the client timeout. Here is the diff:\n```csharp\n- var timeout = TimeSpan.FromSeconds(5);\n+ var timeout = TimeSpan.FromSeconds(30);\n```\nThat raises the request timeout from five to thirty seconds, which stops the slow upload from being cancelled.",
            ExpectedSpokenStandIn = "I raised the request timeout from five seconds to thirty seconds, which stops the slow upload from being cancelled.",
        },
        new Fixture
        {
            Label = "Non-Latin reply (must survive and translate in-language)",
            UserMessage = "로그인 버그 고쳤어?",
            AgentReply = "네, 로그인 버그를 고쳤습니다. 세션 토큰 갱신 로직을 추가했고, 모든 테스트가 통과했습니다.",
            ExpectedSpokenStandIn = "네, 로그인 버그를 고쳤습니다. 세션 토큰 갱신 로직을 추가했고 모든 테스트가 통과했습니다.",
        },
        new Fixture
        {
            Label = "Long multi-step reply (faithful but readable)",
            UserMessage = "What's the status of the release?",
            AgentReply = "The release is almost ready. I have merged the four open pull requests, bumped the version to 2.4.0, regenerated the changelog, and run the full test suite which is green. The only thing left is the signing step, which needs the release certificate that is not on this machine. Once that certificate is available, the installer can be built and published.",
            ExpectedSpokenStandIn = "The release is almost ready. The four open pull requests are merged, the version is bumped to 2.4.0, the changelog is regenerated, and the full test suite is green. The only thing left is signing, which needs the release certificate that is not on this machine. Once that is available, the installer can be built and published.",
        },
    };
}

/// <summary>One row of the QA report: an agent reply and the wingman's spoken translation.</summary>
public sealed class WingmanQaRow
{
    public string Label { get; init; } = "";
    public string UserMessage { get; init; } = "";
    public string AgentReply { get; init; } = "";
    public string Spoken { get; init; } = "";
    public double ReplySeconds { get; init; }
    public int SpokenChars { get; init; }
}

/// <summary>Renders the issue #531 QA report as a single self-contained HTML page.</summary>
public static class WingmanQaReport
{
    public static string Render(IReadOnlyList<WingmanQaRow> rows, bool live)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />");
        sb.Append("<title>Wingman Text - QA report (issue #531)</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#0b1020;color:#e7ecf5;}");
        sb.Append("header{padding:20px 28px;background:#121a33;border-bottom:1px solid #243154;}");
        sb.Append("h1{margin:0 0 4px;font-size:20px;}");
        sb.Append(".sub{color:#9fb0d0;font-size:13px;}");
        sb.Append(".badge{display:inline-block;padding:2px 10px;border-radius:12px;font-size:12px;font-weight:600;}");
        sb.Append(".live{background:#0e8a16;color:#fff;}.offline{background:#5a4b00;color:#ffe08a;}");
        sb.Append("main{padding:18px 28px;}");
        sb.Append(".row{background:#121a33;border:1px solid #243154;border-radius:10px;margin:0 0 16px;overflow:hidden;}");
        sb.Append(".row h2{margin:0;padding:12px 16px;font-size:15px;background:#18223f;border-bottom:1px solid #243154;}");
        sb.Append(".ask{padding:10px 16px;color:#9fb0d0;font-size:13px;border-bottom:1px solid #243154;}");
        sb.Append(".cols{display:grid;grid-template-columns:1fr 1fr;gap:0;}");
        sb.Append(".col{padding:14px 16px;}.col.left{border-right:1px solid #243154;}");
        sb.Append(".lbl{font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:#7e90b5;margin-bottom:6px;}");
        sb.Append("pre{white-space:pre-wrap;word-wrap:break-word;font-family:Consolas,monospace;font-size:13px;margin:0;color:#cdd8ef;}");
        sb.Append(".spoken{font-size:15px;line-height:1.5;color:#fff;}");
        sb.Append(".meta{padding:8px 16px;font-size:12px;color:#7e90b5;border-top:1px solid #243154;}");
        sb.Append("</style></head><body>");

        sb.Append("<header><h1>Wingman Text - QA report</h1>");
        sb.Append("<div class=\"sub\">Issue #531 - the wingman as the translator of a session. ");
        sb.Append("Left: the coding agent's actual written reply. Right: the wingman's spoken translation. ");
        sb.Append("Judge each pair for fidelity (does the spoken side keep the answer and the facts?) and ");
        sb.Append("speakability (could you say it out loud in a back-and-forth?).</div>");
        sb.Append(live
            ? "<p><span class=\"badge live\">LIVE</span> Spoken side produced by the real configured wingman session.</p>"
            : "<p><span class=\"badge offline\">OFFLINE</span> Spoken side is a representative stand-in (fake brain); this run proves the pipeline and report format, not live model quality.</p>");
        sb.Append("</header><main>");

        foreach (var r in rows)
        {
            sb.Append("<div class=\"row\">");
            sb.Append("<h2>").Append(Esc(r.Label)).Append("</h2>");
            sb.Append("<div class=\"ask\">Person said: ").Append(Esc(r.UserMessage)).Append("</div>");
            sb.Append("<div class=\"cols\">");
            sb.Append("<div class=\"col left\"><div class=\"lbl\">Agent reply (").Append(r.AgentReply.Length).Append(" chars)</div><pre>").Append(Esc(r.AgentReply)).Append("</pre></div>");
            sb.Append("<div class=\"col\"><div class=\"lbl\">Wingman spoken (").Append(r.SpokenChars).Append(" chars)</div><div class=\"spoken\">").Append(Esc(r.Spoken)).Append("</div></div>");
            sb.Append("</div>");
            sb.Append("<div class=\"meta\">Brain latency: ").Append(r.ReplySeconds.ToString("F1")).Append("s</div>");
            sb.Append("</div>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");
}
