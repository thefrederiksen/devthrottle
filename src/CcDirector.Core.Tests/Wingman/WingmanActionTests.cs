using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Tests for the structured-intent actuation path (Path A): the decision parser
/// (<see cref="WingmanService.ParseActionDecisionJson"/>) and the write chokepoint
/// (<see cref="WingmanActionExecutor"/>). The decision LLM call itself is not exercised
/// here (it spawns claude); these pin down the deterministic pieces around it.
/// </summary>
public sealed class WingmanActionTests
{
    // ---------- ParseActionDecisionJson ----------

    [Fact]
    public void Parse_submit_with_text_returns_submit()
    {
        var a = WingmanService.ParseActionDecisionJson(
            "{\"action\":\"submit\",\"text\":\"yes\",\"reason\":\"the answer is clearly yes\",\"confidence\":\"high\"}");
        Assert.Equal(WingmanAction.ActSubmit, a.Action);
        Assert.Equal("yes", a.Text);
        Assert.Equal("high", a.Confidence);
    }

    [Fact]
    public void Parse_type_with_text_returns_type()
    {
        var a = WingmanService.ParseActionDecisionJson("{\"action\":\"type\",\"text\":\"git status\"}");
        Assert.Equal(WingmanAction.ActType, a.Action);
        Assert.Equal("git status", a.Text);
    }

    [Fact]
    public void Parse_send_keys_returns_keys()
    {
        var a = WingmanService.ParseActionDecisionJson("{\"action\":\"send_keys\",\"keys\":[\"Down\",\"Enter\"]}");
        Assert.Equal(WingmanAction.ActSendKeys, a.Action);
        Assert.Equal(new[] { "Down", "Enter" }, a.Keys);
    }

    [Fact]
    public void Parse_fenced_json_is_unwrapped()
    {
        var a = WingmanService.ParseActionDecisionJson("```json\n{\"action\":\"submit\",\"text\":\"ok\"}\n```");
        Assert.Equal(WingmanAction.ActSubmit, a.Action);
        Assert.Equal("ok", a.Text);
    }

    [Theory]
    [InlineData("{\"action\":\"submit\"}")]            // submit without text
    [InlineData("{\"action\":\"type\",\"text\":\"\"}")] // type with empty text
    [InlineData("{\"action\":\"send_keys\",\"keys\":[]}")] // send_keys without keys
    [InlineData("{\"action\":\"frobnicate\",\"text\":\"x\"}")] // unknown action
    [InlineData("not json at all")]
    [InlineData("")]
    public void Parse_invalid_or_incomplete_decisions_fall_back_to_none(string raw)
    {
        Assert.Equal(WingmanAction.ActNone, WingmanService.ParseActionDecisionJson(raw).Action);
    }

    // ---------- BuildActionDecisionPrompt ----------

    [Fact]
    public void Prompt_includes_action_shape_and_cursor()
    {
        var ctx = new WingmanAskContext
        {
            AgentKind = "ClaudeCode",
            ActivityState = "WaitingForInput",
            ScreenRows = new[] { "> Continue?", "  bypass permissions on (shift+tab to cycle)" },
            CursorRow = 0,
            CursorCol = 11,
        };
        var prompt = WingmanService.BuildActionDecisionPrompt(ctx);
        Assert.Contains("none|type|send_keys|submit", prompt);
        Assert.Contains("cursor at row 0, col 11", prompt);
        Assert.Contains("> Continue?", prompt);
    }

    // ---------- WingmanActionExecutor ----------

    private static (SessionManager mgr, Session session, CircularTerminalBuffer buf) NewSession()
    {
        var mgr = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var backend = new BufferOnlyBackend();
        var session = mgr.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        var buf = session.Buffer ?? throw new InvalidOperationException("embedded session has no buffer");
        return (mgr, session, buf);
    }

    [Fact]
    public void Execute_submit_writes_text_then_enter_and_audits()
    {
        var (mgr, session, buf) = NewSession();
        try
        {
            var action = new WingmanAction { Action = WingmanAction.ActSubmit, Text = "hello", Reason = "obvious" };
            var result = WingmanActionExecutor.Execute(session, action);

            Assert.True(result.Performed);
            Assert.Equal(WingmanActResult.StatusOk, result.Status);

            var written = Encoding.UTF8.GetString(buf.DumpAll());
            Assert.Contains("hello", written);
            Assert.EndsWith("\r", written); // Enter appended

            Assert.Single(session.RecentWingmanActions);
            Assert.Equal(WingmanAction.ActSubmit, session.RecentWingmanActions[0].Action);
            Assert.NotNull(session.LastWingmanInjectionAt);
        }
        finally { mgr.Dispose(); }
    }

    [Fact]
    public void Execute_type_writes_text_without_enter()
    {
        var (mgr, session, buf) = NewSession();
        try
        {
            var result = WingmanActionExecutor.Execute(session, new WingmanAction { Action = WingmanAction.ActType, Text = "abc" });

            Assert.True(result.Performed);
            var written = Encoding.UTF8.GetString(buf.DumpAll());
            Assert.Equal("abc", written); // no trailing CR
        }
        finally { mgr.Dispose(); }
    }

    [Fact]
    public void Execute_send_keys_writes_mapped_bytes()
    {
        var (mgr, session, buf) = NewSession();
        try
        {
            var action = new WingmanAction { Action = WingmanAction.ActSendKeys };
            action.Keys.AddRange(new[] { "Down", "Enter" });
            var result = WingmanActionExecutor.Execute(session, action);

            Assert.True(result.Performed);
            var bytes = buf.DumpAll();
            Assert.Equal(new byte[] { 0x1B, 0x5B, 0x42, 0x0D }, bytes); // ESC [ B , CR
        }
        finally { mgr.Dispose(); }
    }

    [Fact]
    public void Execute_none_does_nothing()
    {
        var (mgr, session, buf) = NewSession();
        try
        {
            var result = WingmanActionExecutor.Execute(session, new WingmanAction { Action = WingmanAction.ActNone });

            Assert.False(result.Performed);
            Assert.Equal(WingmanActResult.StatusOk, result.Status);
            Assert.Empty(buf.DumpAll());
            Assert.Empty(session.RecentWingmanActions);
        }
        finally { mgr.Dispose(); }
    }

    [Fact]
    public void Execute_unknown_key_is_rejected_and_writes_nothing()
    {
        var (mgr, session, buf) = NewSession();
        try
        {
            var action = new WingmanAction { Action = WingmanAction.ActSendKeys };
            action.Keys.Add("Frobnicate");
            var result = WingmanActionExecutor.Execute(session, action);

            Assert.False(result.Performed);
            Assert.Equal(WingmanActResult.StatusBadRequest, result.Status);
            Assert.Empty(buf.DumpAll());
        }
        finally { mgr.Dispose(); }
    }

    [Fact]
    public void Execute_on_exited_session_reports_session_gone()
    {
        var (mgr, session, buf) = NewSession();
        try
        {
            session.Status = SessionStatus.Exited; // internal setter, visible to tests
            var result = WingmanActionExecutor.Execute(session, new WingmanAction { Action = WingmanAction.ActSubmit, Text = "x" });

            Assert.False(result.Performed);
            Assert.Equal(WingmanActResult.StatusSessionGone, result.Status);
            Assert.Empty(buf.DumpAll());
        }
        finally { mgr.Dispose(); }
    }

    [Fact]
    public void Execute_suppresses_a_repeat_on_an_unchanged_screen()
    {
        var (mgr, session, _) = NewSession();
        try
        {
            // Ctrl+C (0x03) is a C0 control the parser drops, so it does not change the
            // visible grid -- the screen hash is identical across both calls, which is
            // exactly the idempotency case the cooldown guards.
            var first = new WingmanAction { Action = WingmanAction.ActSendKeys };
            first.Keys.Add("Ctrl+C");
            var r1 = WingmanActionExecutor.Execute(session, first);
            Assert.True(r1.Performed);

            var second = new WingmanAction { Action = WingmanAction.ActSendKeys };
            second.Keys.Add("Ctrl+C");
            var r2 = WingmanActionExecutor.Execute(session, second);

            Assert.False(r2.Performed);
            Assert.Equal(WingmanActResult.StatusSuppressed, r2.Status);
        }
        finally { mgr.Dispose(); }
    }
}
