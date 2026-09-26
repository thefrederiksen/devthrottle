using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The turn-verdict boundary audit: the order in which <c>TurnVerdictService.JudgeAsync</c> runs its checks,
/// reads and model call, DERIVED FROM THE SOURCE, and the charter checked to AGREE with it.
///
/// WHY IT READS THE CODE AND NOT JUST THE CHARTER. This boundary was described wrongly five times - in the
/// charter, in the specification and in the comment above the code - and the code was right every time. A test
/// that only checked the charter contains the right sentences would guard the DOCUMENT: it would stay green
/// while the code moved underneath it and the charter became false. So every order and every read count below
/// is computed from where the statements actually sit in <c>JudgeAsync</c>, and the charter is then asked to
/// say the same thing.
///
/// HOW A STEP IS FOUND. Each step is located by one distinctive statement (for example the screen read is the
/// line calling <c>ReadScreenAsync(</c>), searched only on lines that are not comments - so the boundary comment
/// block above the code can never stand in for the code it describes. A statement that is missing, or found more
/// times than expected, fails the test loudly rather than being guessed at.
///
/// WHAT THIS DOES NOT CATCH, stated so nobody relies on it for more:
///   - A STEP ADDED to the code that the charter never names. The test only locates the steps it knows; a new
///     check inserted between two of them leaves every located step where it was, and the test stays green.
///   - A step whose statement is still there but whose MEANING changed - for example the reuse lookup kept but
///     its early return removed. The test checks where statements sit, not what they do.
///   - The CONDITIONS on a step: that the settle wait is for a turn end only, that the second round of checks is
///     for an automatic request only, the voice-session exception to the switch, which triggers the ceiling binds.
///   - A switch check added AFTER the model call. The "not checked again" fact looks between the settle wait and
///     the model call, which is where a second check could stop a flight before it spends anything.
///   - It says nothing about a change nobody runs the default local gate on. It lives in
///     <c>CcDirector.Core.UnitTests</c>, which the default gate runs, with the charter audit it extends. Both were
///     first written into the PARKED <c>CcDirector.Core.Tests</c> and moved here, because a guard in a parked
///     suite reports nothing at commit time - which reads exactly like a guard that passed.
///   - It is brittle to harmless renames: renaming <c>_env.Latest</c> turns it red for a reason that is not a
///     boundary change. That red is loud and names the missing statement, which is the safe direction.
/// </summary>
public sealed class WingmanVerdictBoundaryAuditTests
{
    private readonly ITestOutputHelper _out;
    public WingmanVerdictBoundaryAuditTests(ITestOutputHelper output) => _out = output;

    private const string ServicePath = "src/CcDirector.Gateway/Wingman/TurnVerdictService.cs";
    private const string CharterPath = "docs/wingman/WINGMAN.md";
    private const string SpecPath = "docs/architecture/wingman/TURN_VERDICT.md";

    private const string CharterHeading = "### The order of the checks, and what each one costs";
    private const string SpecHeading = "## 6. The checks, in the order they run";
    private const string CommentBlockFirstLine = "THE BOUNDARY, IN THE ORDER THE CODE RUNS IT.";

    /// <summary>One step of the boundary: its name, the statement that locates it in the code, and the phrase
    /// that names it in the charter.</summary>
    private sealed record Step(string Name, string CodeMarker, string CharterPhrase);

    // The steps whose ORDER the charter states, each located once in the code. The switch is handled on its own
    // below, because it is referenced twice in JudgeAsync and only the first reference can stand a flight down.
    private static readonly Step SettleWait = new("settle wait", "DelayAsync(", "settle wait");
    private static readonly Step ScreenRead = new("screen read", "ReadScreenAsync(", "screen read");
    private static readonly Step ReuseCheck = new("reuse check", "_env.Latest(", "reuse check");
    // The conversation read is located by its ASSIGNMENT, because JudgeAsync reads the conversation in two places: this
    // one, step 5, and a lazy read inside the reuse check that runs only while a rate limit's named wait is held (the
    // Wingman-on-every-turn mission, slice I). The second is not left uncounted: the fact below pins it to the reuse
    // check and requires the charter to say so.
    private const string AnyConversationReadMarker = "_env.ReadConversation(";
    private static readonly Step ConversationRead = new("conversation read", "var conversation = _env.ReadConversation(", "conversation read");
    private static readonly Step AccountCeiling = new("account ceiling", ".MaxInFlight", "account ceiling");
    // Contract v4: the model call is made through AskCallAModelAsync, which JudgeAsync calls only for a stop no code
    // step decided. It is still the last step of the order the charter states.
    private static readonly Step ModelCall = new("model call", "AskCallAModelAsync(", "model call");

    private const string SwitchName = "judge switch";
    private const string SwitchMarker = "JudgeEnabled";
    private const string SettingsReadMarker = "_env.Settings(";
    private const string StateCheckMarker = "SessionStateSkipCause(";

    private static readonly Step[] UniqueSteps =
    {
        SettleWait, ScreenRead, ReuseCheck, ConversationRead, AccountCeiling, ModelCall,
    };

    // ------------------------------------------------------------------------------------------------ the facts

    [Fact]
    public void The_charter_states_the_steps_in_the_order_JudgeAsync_runs_them()
    {
        var code = JudgeAsyncCode();
        var positions = UniqueSteps.ToDictionary(s => s.Name, s => LocateOnce(code, s.CodeMarker, s.Name));
        positions[SwitchName] = FirstSwitchCheck(code);

        var codeOrder = positions.OrderBy(p => p.Value).Select(p => p.Key).ToList();

        var block = CharterBlock();
        AssertStepsAreNumberedInOrder(block);
        var phrases = UniqueSteps.ToDictionary(s => s.Name, s => s.CharterPhrase);
        phrases[SwitchName] = "judge switch";
        var charterOrder = phrases
            .Select(p => (Name: p.Key, Index: IndexOfPhrase(block, p.Value, p.Key)))
            .OrderBy(p => p.Index)
            .Select(p => p.Name)
            .ToList();

        _out.WriteLine("code order:    " + string.Join(" -> ", codeOrder.Select(n => $"{n} ({positions[n]})")));
        _out.WriteLine("charter order: " + string.Join(" -> ", charterOrder));

        Assert.True(codeOrder.SequenceEqual(charterOrder),
            "The charter's boundary block states the steps in a different order from the one JudgeAsync runs them in.\n" +
            "  code (from " + ServicePath + "): " + string.Join(" -> ", codeOrder) + "\n" +
            "  charter (" + CharterPath + "):  " + string.Join(" -> ", charterOrder) + "\n" +
            "The code is the fact. Correct the charter, the specification and the comment above the code together.");
    }

    [Fact]
    public void The_only_other_conversation_read_is_inside_the_reuse_check_and_the_charter_says_so()
    {
        var code = JudgeAsyncCode();
        var main = LocateOnce(code, ConversationRead.CodeMarker, ConversationRead.Name);
        var reuse = LocateOnce(code, ReuseCheck.CodeMarker, ReuseCheck.Name);
        var others = code.Where(l => l.Text.Contains(AnyConversationReadMarker, StringComparison.Ordinal) && l.Number != main)
            .Select(l => l.Number).ToList();

        Assert.True(others.Count == 1,
            $"JudgeAsync reads the conversation outside step 5 {others.Count} times (lines {string.Join(", ", others)}); the charter " +
            "names exactly one such read, inside the reuse check. A new read is a boundary change: say it in the charter first.");
        // The reuse check ends where step 4, the conversation read, begins: the speech re-attempt refusal that used
        // to sit between them left with the voice path's retry ledger (mission "Wingman error and retry").
        Assert.True(others[0] > reuse && others[0] < main,
            $"The second conversation read (line {others[0]}) is not inside the reuse check (between lines {reuse} and {main}), " +
            "which is the only place the charter says it happens.");

        var step3 = CharterSteps(CharterBlock()).Single(s => s.Number == 3).Text;
        Assert.Contains("reads the stored conversation as well", step3, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_read_count_the_charter_states_matches_where_the_code_reads()
    {
        var code = JudgeAsyncCode();
        var screen = LocateOnce(code, ScreenRead.CodeMarker, ScreenRead.Name);
        var conversation = LocateOnce(code, ConversationRead.CodeMarker, ConversationRead.Name);

        var located = UniqueSteps.ToDictionary(s => s.CharterPhrase, s => LocateOnce(code, s.CodeMarker, s.Name));
        located["judge switch"] = FirstSwitchCheck(code);
        // The held, live, brand-new, exited and working checks run twice, and the SECOND run is the last thing the
        // first step does - after the settle wait. Without it, moving that second run past the screen read would
        // leave step 1's "NO reads" false and this test green, because the settle wait would still be the last
        // statement step 1 names.
        located["held"] = LastStateCheck(code);

        var costWords = new Dictionary<string, int>(StringComparer.Ordinal) { ["NO"] = 0, ["ONE"] = 1, ["TWO"] = 2 };
        var costPattern = new Regex(@"costs? (NO|ONE|TWO) reads?", RegexOptions.None);

        var checkedSteps = 0;
        foreach (var (number, text) in CharterSteps(CharterBlock()))
        {
            var match = costPattern.Match(text);
            if (!match.Success) continue;

            var named = located.Keys.Where(phrase => text.Contains(phrase, StringComparison.Ordinal)).ToList();
            Assert.True(named.Count > 0,
                $"Charter step {number} states a read count but names no step this test can locate in the code: {text}");

            // The cost of a refusal at a step is what has been read by the time the LAST statement it names runs.
            var last = named.Max(phrase => located[phrase]);
            var actual = (last > screen ? 1 : 0) + (last > conversation ? 1 : 0);
            var claimed = costWords[match.Groups[1].Value];

            _out.WriteLine($"step {number}: names [{string.Join(", ", named)}], last at line {last}; charter says {claimed}, code says {actual}");
            Assert.True(claimed == actual,
                $"Charter step {number} says it costs {match.Groups[1].Value} read(s), but in {ServicePath} its last statement " +
                $"(line {last}) runs after {actual} of the two reads (screen read at line {screen}, conversation read at " +
                $"line {conversation}). Step text: {text}");
            checkedSteps++;
        }

        Assert.True(checkedSteps >= 3,
            $"Only {checkedSteps} read count(s) were found in the charter's boundary block; expected the three it states " +
            "(NO for the first checks, ONE for the reuse check, TWO for the ceiling). " +
            "A block that stopped stating costs would otherwise pass this test by saying nothing.");
    }

    [Fact]
    public void The_judge_switch_is_read_once_before_the_settle_wait_and_not_checked_again_before_the_model_call()
    {
        var code = JudgeAsyncCode();
        var settle = LocateOnce(code, SettleWait.CodeMarker, SettleWait.Name);
        var model = LocateOnce(code, ModelCall.CodeMarker, ModelCall.Name);

        var settingsReads = code.Where(l => l.Text.Contains(SettingsReadMarker, StringComparison.Ordinal)).Select(l => l.Number).ToList();
        Assert.True(settingsReads.Count == 1,
            $"JudgeAsync reads the account settings {settingsReads.Count} time(s) (lines {string.Join(", ", settingsReads)}); " +
            "the charter says the switch is checked ONCE in the flight, from settings read when the flight starts.");
        Assert.True(settingsReads[0] < settle,
            $"The account settings are read at line {settingsReads[0]}, after the settle wait at line {settle}.");

        var first = FirstSwitchCheck(code);
        Assert.True(first < settle,
            $"The judge switch is first checked at line {first}, after the settle wait at line {settle}; the charter says before.");

        var reChecks = code
            .Where(l => l.Number > settle && l.Number < model && l.Text.Contains(SwitchMarker, StringComparison.Ordinal))
            .Select(l => l.Number)
            .ToList();
        Assert.True(reChecks.Count == 0,
            $"The judge switch is checked again between the settle wait (line {settle}) and the model call (line {model}), " +
            $"at line(s) {string.Join(", ", reChecks)}. The charter says it is NOT, and that a switch turned off during a flight " +
            "does not stop that flight. One of them is now wrong.");

        var step1 = CharterSteps(CharterBlock()).First(s => s.Number == 1).Text;
        Assert.Contains("checked ONCE in the flight, before the settle wait", step1, StringComparison.Ordinal);
        Assert.Contains("NOT the switch", step1, StringComparison.Ordinal);
    }

    [Fact]
    public void The_charter_the_specification_and_the_code_comment_state_the_boundary_identically()
    {
        var charter = CharterBlock();
        var spec = FencedBlockAfter(ReadRepoFile(SpecPath), SpecHeading, SpecPath);
        var comment = CodeCommentBlock(charter.Count);

        Assert.True(charter.SequenceEqual(spec),
            $"{SpecPath} section 6 states the boundary differently from {CharterPath}:\n" + FirstDifference(charter, spec));
        Assert.True(charter.SequenceEqual(comment),
            $"The comment above JudgeAsync in {ServicePath} states the boundary differently from {CharterPath}:\n" + FirstDifference(charter, comment));
    }

    // ------------------------------------------------------------------------------------------------ reading the code

    /// <summary>A line of JudgeAsync that is not a comment, with its one-based line number in the file.</summary>
    private sealed record CodeLine(int Number, string Text);

    /// <summary>
    /// The non-comment lines of JudgeAsync. The method ends at the next member declared at the class's own
    /// indentation, which does not depend on which method happens to follow it.
    /// </summary>
    private static List<CodeLine> JudgeAsyncCode()
    {
        var lines = ReadRepoFile(ServicePath).Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("private async Task<TurnVerdictOutcome> JudgeAsync(", StringComparison.Ordinal));
        Assert.True(start >= 0, $"JudgeAsync was not found in {ServicePath}; this audit cannot read the boundary it guards.");

        var member = new Regex(@"^    (private|public|internal|protected)\s", RegexOptions.None);
        var end = Array.FindIndex(lines, start + 1, l => member.IsMatch(l));
        Assert.True(end > start, $"The end of JudgeAsync was not found in {ServicePath}.");

        var code = new List<CodeLine>();
        for (var i = start; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            code.Add(new CodeLine(i + 1, lines[i]));
        }
        return code;
    }

    private static int LocateOnce(List<CodeLine> code, string marker, string name)
    {
        var hits = code.Where(l => l.Text.Contains(marker, StringComparison.Ordinal)).Select(l => l.Number).ToList();
        Assert.True(hits.Count == 1,
            hits.Count == 0
                ? $"The {name} ('{marker}') was not found in JudgeAsync in {ServicePath}. If it was renamed or moved out of " +
                  "the method, update this audit's marker AND check the charter still describes the code."
                : $"The {name} ('{marker}') is in JudgeAsync {hits.Count} times (lines {string.Join(", ", hits)}); this audit " +
                  "cannot tell which one the charter means.");
        return hits[0];
    }

    /// <summary>The LAST run of the held, live, brand-new, exited and working checks. They run once before the settle
    /// wait and, for an automatic request, once after it; the charter's first step covers both.</summary>
    private static int LastStateCheck(List<CodeLine> code)
    {
        var hits = code.Where(l => l.Text.Contains(StateCheckMarker, StringComparison.Ordinal)).Select(l => l.Number).ToList();
        Assert.True(hits.Count > 0, $"The held, live, brand-new, exited and working checks ('{StateCheckMarker}') were not found in JudgeAsync in {ServicePath}.");
        return hits[^1];
    }

    /// <summary>The first reference to the switch - the check that can stand a flight down. Later references are
    /// checked by the switch fact, not ordered.</summary>
    private static int FirstSwitchCheck(List<CodeLine> code)
    {
        var hits = code.Where(l => l.Text.Contains(SwitchMarker, StringComparison.Ordinal)).Select(l => l.Number).ToList();
        Assert.True(hits.Count > 0, $"The judge switch ('{SwitchMarker}') was not found in JudgeAsync in {ServicePath}.");
        return hits[0];
    }

    private List<string> CodeCommentBlock(int length)
    {
        var lines = ReadRepoFile(ServicePath).Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains(CommentBlockFirstLine, StringComparison.Ordinal));
        Assert.True(start >= 0, $"The boundary comment block was not found in {ServicePath}.");
        var comment = new Regex(@"^\s*// ?(.*)$", RegexOptions.None);
        return lines.Skip(start).Take(length)
            .Select(l => comment.Match(l) is { Success: true } m ? m.Groups[1].Value : "<not a comment line: " + l.Trim() + ">")
            .ToList();
    }

    // ------------------------------------------------------------------------------------------------ reading the charter

    private static List<string> CharterBlock() => FencedBlockAfter(ReadRepoFile(CharterPath), CharterHeading, CharterPath);

    private static List<string> FencedBlockAfter(string text, string heading, string path)
    {
        text = text.Replace("\r\n", "\n");
        var at = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The heading '{heading}' was not found in {path}.");
        var open = text.IndexOf("```\n", at, StringComparison.Ordinal);
        Assert.True(open >= 0, $"No fenced block follows '{heading}' in {path}.");
        var body = open + 4;
        var close = text.IndexOf("\n```", body, StringComparison.Ordinal);
        Assert.True(close > body, $"The fenced block after '{heading}' in {path} is not closed.");
        return text[body..close].Split('\n').ToList();
    }

    /// <summary>The numbered steps of a boundary block, each with its continuation lines joined on.</summary>
    private static List<(int Number, string Text)> CharterSteps(List<string> block)
    {
        var start = new Regex(@"^(\d+)\. (.*)$", RegexOptions.None);
        var steps = new List<(int Number, string Text)>();
        foreach (var line in block)
        {
            var m = start.Match(line);
            if (m.Success)
                steps.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value));
            else if (steps.Count > 0 && line.StartsWith("   ", StringComparison.Ordinal))
                steps[^1] = (steps[^1].Number, steps[^1].Text + " " + line.Trim());
        }
        return steps;
    }

    private static void AssertStepsAreNumberedInOrder(List<string> block)
    {
        var numbers = CharterSteps(block).Select(s => s.Number).ToList();
        Assert.True(numbers.Count > 0, $"The charter's boundary block in {CharterPath} has no numbered steps.");
        Assert.True(numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)),
            $"The charter's boundary steps are not numbered 1 to {numbers.Count} in order ({string.Join(", ", numbers)}), " +
            "so the order they are written in is not the order they are numbered in.");
    }

    private static int IndexOfPhrase(List<string> block, string phrase, string name)
    {
        var index = string.Join("\n", block).IndexOf(phrase, StringComparison.Ordinal);
        Assert.True(index >= 0, $"The charter's boundary block in {CharterPath} does not name the {name} ('{phrase}').");
        return index;
    }

    private static string FirstDifference(List<string> expected, List<string> actual)
    {
        for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            var e = i < expected.Count ? expected[i] : "<no line>";
            var a = i < actual.Count ? actual[i] : "<no line>";
            if (!string.Equals(e, a, StringComparison.Ordinal))
                return $"  line {i + 1}\n    charter: {e}\n    other:   {a}";
        }
        return "  (no line differs)";
    }

    // ------------------------------------------------------------------------------------------------ the repository

    private static string ReadRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate " + relative + " by walking up from " + AppContext.BaseDirectory);
    }
}
