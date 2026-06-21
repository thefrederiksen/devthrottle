using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Wingman;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #531: the wingman as the translator of a working session. These tests exercise
/// the translation logic with a fake <see cref="IAgentBrain"/> - no live model, no audio -
/// which is exactly the testable foundation the Wingman Text tab is built on. They prove
/// the mechanical guarantees (faithful carry-through, context cleared every turn, fail-loud
/// on a broken contract, speech cleanup, and that the only dependency is a real-session
/// brain - never a <c>--print</c> process). Human judgement of summary quality comes from
/// the HTML QA report this file also emits.
/// </summary>
public sealed class WingmanTranslatorTests
{
    private readonly ITestOutputHelper _out;

    public WingmanTranslatorTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// A fake warm brain: wraps whatever spoken text the test configures in the shared
    /// answer markers (so the translator's extraction is exercised), and counts clears so a
    /// test can prove the context is reset after every translation.
    /// </summary>
    private sealed class FakeBrain : IAgentBrain
    {
        private readonly Func<string, string> _spokenForPrompt;
        public List<string> Asks { get; } = new();
        public int ClearCount { get; private set; }

        public FakeBrain(Func<string, string> spokenForPrompt) => _spokenForPrompt = spokenForPrompt;

        public string? SessionId => "fake-brain-session";

        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Asks.Add(prompt);
            var spoken = _spokenForPrompt(prompt);
            // Wrap in the shared answer markers the translator extracts between, exactly as
            // a real session is instructed to.
            var wrapped = $"{SessionAskRunner.AnswerBeginMarker}\n{spoken}\n{SessionAskRunner.AnswerEndMarker}";
            return Task.FromResult(new AskResult { Text = wrapped, ReplySeconds = 0.2 });
        }

        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default)
        {
            ClearCount++;
            return Task.FromResult(new ClearResult());
        }
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    private static WingmanTranslator BuildTranslator(FakeBrain brain)
        => new(_ => Task.FromResult<IAgentBrain>(brain), log: _ => { });

    [Fact]
    public async Task TranslateAsync_ReturnsTheSpokenTranslation_FromBetweenTheMarkers()
    {
        var brain = new FakeBrain(_ => "The login bug is fixed. All seventy-three tests passed.");
        var translator = BuildTranslator(brain);

        var result = await translator.TranslateAsync(
            "Did the login fix work?",
            "I patched the auth flow in `LoginService.cs` and the suite is green: 73/73.");

        Assert.Equal("The login bug is fixed. All seventy-three tests passed.", result.Spoken);
    }

    [Fact]
    public async Task TranslateAsync_ClearsTheContext_AfterEveryTranslation()
    {
        var brain = new FakeBrain(_ => "Done.");
        var translator = BuildTranslator(brain);

        await translator.TranslateAsync("q1", "reply one");
        await translator.TranslateAsync("q2", "reply two");

        // Keep alive, but clear between uses (issue #531): one clear per translation.
        Assert.Equal(2, brain.ClearCount);
    }

    [Fact]
    public async Task TranslateAsync_ClearsTheContext_EvenWhenTheAskThrows()
    {
        var brain = new ThrowingBrain();
        var translator = new WingmanTranslator(_ => Task.FromResult<IAgentBrain>(brain), log: _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync("q", "some reply"));

        Assert.Equal(1, brain.ClearCount);
    }

    private sealed class ThrowingBrain : IAgentBrain
    {
        public int ClearCount { get; private set; }
        public string? SessionId => "throwing";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
            => throw new InvalidOperationException("brain blew up");
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default)
        {
            ClearCount++;
            return Task.FromResult(new ClearResult());
        }
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    [Fact]
    public async Task TranslateAsync_CarriesTheAgentReplyVerbatim_IntoThePrompt()
    {
        var brain = new FakeBrain(_ => "ok");
        var translator = BuildTranslator(brain);
        const string reply = "I changed the timeout to 30 seconds and re-ran the failing case.";

        await translator.TranslateAsync("what did you change?", reply);

        var prompt = Assert.Single(brain.Asks);
        Assert.Contains(reply, prompt);
        Assert.Contains("FIDELITY", prompt); // the fidelity contract is present
        Assert.Contains("what did you change?", prompt); // the person's message is present
    }

    [Fact]
    public async Task TranslateAsync_EmptyReply_ThrowsBecauseThereIsNothingToTranslate()
    {
        var translator = BuildTranslator(new FakeBrain(_ => "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => translator.TranslateAsync("q", "   "));
    }

    [Fact]
    public async Task TranslateAsync_BrainReplyWithoutMarkers_UsesTheWholeReply()
    {
        // An LLM does not always emit the formatting markers. When they are absent the brain's
        // reply IS the spoken answer (it was told to output only that), so we use it rather than
        // 502 - the reliability fix for the explain/voice-turn path.
        var brain = new NoMarkersBrain();   // returns "just some text with no markers at all"
        var translator = new WingmanTranslator(_ => Task.FromResult<IAgentBrain>(brain), log: _ => { });
        var result = await translator.TranslateAsync("q", "a reply");
        Assert.Equal("just some text with no markers at all", result.Spoken);
    }

    private sealed class NoMarkersBrain : IAgentBrain
    {
        public string? SessionId => "nomarkers";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(new AskResult { Text = "just some text with no markers at all" });
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    [Fact]
    public async Task AskDirectAsync_AnswersThePersonDirectly_AndClearsContext()
    {
        var brain = new FakeBrain(_ => "I cannot edit files myself, but I can explain what the test does.");
        var translator = BuildTranslator(brain);

        var result = await translator.AskDirectAsync("Hey wingman, what does this test check?");

        Assert.Equal("I cannot edit files myself, but I can explain what the test does.", result.Spoken);
        Assert.Equal(1, brain.ClearCount);
        var prompt = Assert.Single(brain.Asks);
        Assert.Contains("Hey wingman, what does this test check?", prompt);
        Assert.Contains("do NOT edit files", prompt); // the direct-path contract
    }

    [Fact]
    public async Task AskAboutDevThrottleAsync_AnswersTheProductQuestion_AndClearsContext()
    {
        // Issue #472: the Cockpit Learning page Q&A path. The brain is grounded as DevThrottle's
        // in-product help and answers the question; the context is cleared after, like the others.
        var brain = new FakeBrain(_ => "DevThrottle runs and supervises many Claude Code sessions at once.");
        var translator = BuildTranslator(brain);

        var result = await translator.AskAboutDevThrottleAsync("What is DevThrottle?");

        Assert.Equal("DevThrottle runs and supervises many Claude Code sessions at once.", result.Spoken);
        Assert.Equal(1, brain.ClearCount);
        var prompt = Assert.Single(brain.Asks);
        Assert.Contains("What is DevThrottle?", prompt);                 // the user's question is carried
        Assert.Contains("DevThrottle's in-product help", prompt);        // the product grounding is present
    }

    [Fact]
    public async Task AskAboutDevThrottleAsync_EmptyQuestion_Throws()
    {
        var translator = BuildTranslator(new FakeBrain(_ => "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => translator.AskAboutDevThrottleAsync("   "));
    }

    [Fact]
    public void BuildDevThrottlePrompt_GroundsTheBrainAsDevThrottleHelp_AndCarriesTheQuestion()
    {
        var prompt = WingmanTranslator.BuildDevThrottlePrompt("How do I start a session?");

        Assert.Contains("DevThrottle's in-product help", prompt);
        Assert.Contains("Answer ONLY about DevThrottle", prompt);
        Assert.Contains("How do I start a session?", prompt);
        Assert.Contains(SessionAskRunner.AnswerBeginMarker, prompt);
        Assert.Contains(SessionAskRunner.AnswerEndMarker, prompt);
    }

    [Fact]
    public void CleanupForSpeech_StripsCodeFencesButKeepsInlineIdentifierText()
    {
        var input = "Here is the change:\n```csharp\nvar x = 1;\n```\nIt updates `timeoutMs` to thirty seconds.";
        var cleaned = WingmanTranslator.CleanupForSpeech(input);

        Assert.DoesNotContain("```", cleaned);
        Assert.DoesNotContain("var x = 1;", cleaned);
        Assert.Contains("timeoutMs", cleaned); // inline identifier text is the answer's content (issue #368)
    }

    [Fact]
    public void CleanupForSpeech_LeavesNonLatinTextUntouched()
    {
        const string korean = "로그인 버그를 수정했습니다. 모든 테스트가 통과했습니다.";
        Assert.Equal(korean, WingmanTranslator.CleanupForSpeech(korean));
    }

    /// <summary>
    /// Proves the only dependency of a translation is a real-session <see cref="IAgentBrain"/>.
    /// The whole pipeline runs to completion against a pure in-memory fake - no process is
    /// spawned, no <c>--print</c> CLI is invoked. The brain is configured elsewhere (issues
    /// #509/#510) to be a real session, never a metered print call (issue #511).
    /// </summary>
    [Fact]
    public async Task TranslateAsync_RunsEntirelyOverTheBrainSeam_NoPrintProcess()
    {
        var brain = new FakeBrain(_ => "All set.");
        var translator = BuildTranslator(brain);

        var result = await translator.TranslateAsync("status?", "Everything is committed and pushed.");

        Assert.Equal("All set.", result.Spoken);
        Assert.Single(brain.Asks);
    }

    /// <summary>
    /// Emits the HTML QA report (issue #531 proof target): each canned agent reply beside the
    /// wingman's spoken translation, so a human can judge fidelity and speakability at a
    /// glance. Here the spoken side is produced by a fake brain that echoes a representative
    /// short form, which proves the report format and the pipeline; a live capture against the
    /// real configured wingman reuses the same <see cref="WingmanQaReport"/> renderer.
    /// </summary>
    [Fact]
    public async Task Emits_WingmanText_QaReport_Html()
    {
        var fixtures = WingmanQaFixtures.All;
        // A stand-in "good" translation per fixture so the report renders end-to-end offline.
        var brain = new FakeBrain(prompt =>
        {
            foreach (var f in fixtures)
                if (prompt.Contains(f.AgentReply, StringComparison.Ordinal))
                    return f.ExpectedSpokenStandIn;
            return "(no match)";
        });
        var translator = BuildTranslator(brain);

        var rows = new List<WingmanQaRow>();
        foreach (var f in fixtures)
        {
            var r = await translator.TranslateAsync(f.UserMessage, f.AgentReply);
            rows.Add(new WingmanQaRow
            {
                Label = f.Label,
                UserMessage = f.UserMessage,
                AgentReply = f.AgentReply,
                Spoken = r.Spoken,
                ReplySeconds = r.ReplySeconds,
                SpokenChars = r.Spoken.Length,
            });

            // Speakability bound: a spoken turn for a back-and-forth must not balloon. The
            // fidelity prompt allows as many sentences as needed, but a translation many times
            // longer than the agent's own reply means it is not summarising - flag it.
            Assert.True(r.Spoken.Length <= Math.Max(600, f.AgentReply.Length),
                $"Fixture '{f.Label}' produced an over-long spoken translation ({r.Spoken.Length} chars).");
            Assert.DoesNotContain("```", r.Spoken); // never read code fences aloud
            Assert.False(string.IsNullOrWhiteSpace(r.Spoken)); // a non-empty reply yields a non-empty translation
        }

        var outDir = Path.Combine(FindRepoRoot(), "docs", "proof", "issue-531");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "wingman-text-qa.html");
        File.WriteAllText(outPath, WingmanQaReport.Render(rows, live: false), Encoding.UTF8);

        _out.WriteLine($"QA report written: {outPath}");
        Assert.True(File.Exists(outPath));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CcDirector.sln"))
               && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
