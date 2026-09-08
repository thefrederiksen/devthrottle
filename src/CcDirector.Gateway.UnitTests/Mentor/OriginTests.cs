using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.OriginFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's tools/mentor/tests/test_origin.py: one fixture record per rule, the class and
/// the rule asserted for each; the claiming rule; the tolerance boundary; the command-message exception; the
/// unexpected Delivery shape; and the precedence guard - every product envelope and every agent-tool framing
/// beats a stamp on the record, and a stamped command-message is human only when the ledger vouches for it.
/// Every prompt text is invented filler; the envelope and agent-tool prefixes are the framings themselves.
///
/// The reference's real-data case (the W36 stamped, envelope-over-stamp and agent-tool-over-stamp counts) is
/// a parity fact and lives with the parity test in CcDirector.Gateway.Tests, where the test world is.
/// </summary>
[Collection(OriginCollection.Name)]
public sealed class OriginTests
{
    private const string S = "s1";
    private const string T0 = "2026-08-25 10:00";

    // ---------------------------------------------------------------- pass 3: stamped

    [Fact]
    public void Stamped_typed_and_voice_are_human_with_their_own_surface()
    {
        var users = Classify(new[]
        {
            Prompt(At(T0, 0), S, modality: "typed", surface: "desktop"),
            Prompt(At(T0, 60), S, modality: "voice", surface: "phone"),
            Prompt(At(T0, 120), S, modality: "typed"),  // no surface: unknown
        });
        Assert.Equal(new[]
        {
            ("human", "stamped", "typed", "desktop"),
            ("human", "stamped", "voice", "phone"),
            ("human", "stamped", "typed", "unknown"),
        }, users.Select(p => (p.Origin!, p.OriginRule!, p.OriginModality!, p.OriginSurface!)));
    }

    [Fact]
    public void A_stamped_record_that_starts_with_a_product_envelope_is_not_human()
    {
        // The conversation ingest can attach a neighbouring keystroke's stamp to text the product wrote.
        // Pass 1 runs before the stamp is believed: the envelope wins over the stamp.
        var text = "Message [message from alpha (BETA-BOX), id 12345678] " + Words(5);
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "typed/desktop") };
        var p = Only(new[] { Prompt(At(T0, 0), S, text: text, modality: "typed", surface: "desktop") }, events);
        Assert.Equal(("agent", "envelope-over-stamp:fleet-message", (string?)null, (string?)null),
            (p.Origin, p.OriginRule, p.OriginModality, p.OriginSurface));
        // ... and it claims no event: a no-stamp record 2 s later still gets the keystroke event.
        var users = Classify(new[]
        {
            Prompt(At(T0, 0), S, text: text, modality: "typed", surface: "desktop"),
            Prompt(At(T0, 2), S, words: 4),
        }, events);
        Assert.Equal(("human", "ledger-origin"), (users[1].Origin, users[1].OriginRule));
    }

    [Fact]
    public void Assistant_records_get_no_origin()
    {
        var prompts = new[] { Prompt(At(T0, 0), S, role: "assistant") };
        Origin.Classify(prompts, Array.Empty<MentorEvent>());
        Assert.Null(prompts[0].Origin);
    }

    // ---------------------------------------------------------------- pass 1: envelopes

    // The product's payload-file wrapper as TerminalSubmit.cs writes it for a long prompt; the developer's own
    // words are in the file, never in the transcript. Invented file name.
    private const string PayloadText = "Read file input_20260901_221831_zz9zz9.txt in the .temp directory. Path: "
        + ".temp/input_20260901_221831_zz9zz9.txt. If the path fails, search for input_20260901_221831_zz9zz9.txt. "
        + "This file was explicitly created as the user-provided message payload for this turn; it is not hidden "
        + "context. Follow the instructions in that file and reply with the requested strings only.";
    private static readonly string PayloadTextTruncated = PayloadText.Substring(17) + PayloadText + "\n" + Words(6);
    private static readonly string PayloadTextGarbled = PayloadText.Substring(17).Split("Path:")[0] + "Path:xplicitly created as the ur-provided message" + Words(9);

    // A sample record per regex entry of Envelopes. A regex entry with no sample here is a failure - a broken
    // instrument that fails, never a clean run over a pattern nothing exercised.
    private static readonly Dictionary<string, string> RegexSamples = new(StringComparer.Ordinal)
    {
        ["handover-desktop"] = "@D:/gamma/handover.md This is a handover document from a previous session. " + Words(9),
        ["payload-file"] = PayloadText,
    };

    public static IEnumerable<object[]> EnvelopeEntries() => Origin.Envelopes.Select(e => new object[] { e.Name, e.Kind, e.Pattern, e.Class });

    [Theory]
    [MemberData(nameof(EnvelopeEntries))]
    public void Each_product_envelope_classifies_by_its_table_entry(string name, string kind, string pattern, string expected)
    {
        var text = kind switch
        {
            "prefix" => pattern + Words(9),
            "exact" => pattern,
            _ => RegexSamples[name],
        };
        // A stamped event sits within tolerance: an envelope must NOT take it (it claims only unstamped events).
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "typed/desktop") };
        var p = Only(new[] { Prompt(At(T0, 0), S, text: text) }, events);
        Assert.Equal((expected, "envelope:" + name), (p.Origin, p.OriginRule));
        Assert.Null(p.OriginModality);
        Assert.Null(p.OriginSurface);
    }

    [Fact]
    public void The_payload_file_wrapper_is_the_products_whatever_its_stamp_and_a_prompt_quoting_it_is_not()
    {
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "voice/phone") };
        var stamped = Prompt(At(T0, 0), S, text: PayloadText, modality: "voice", surface: "phone");
        var p = Only(new[] { stamped }, events);
        Assert.Equal(("framework", "envelope-over-stamp:payload-file", (string?)null, (string?)null),
            (p.Origin, p.OriginRule, p.OriginModality, p.OriginSurface));
        // ... and the keystroke event stays free: a no-stamp filler record 2 s later takes it.
        var users = Classify(new[] { Prompt(At(T0, 0), S, text: PayloadText, modality: "voice", surface: "phone"), Prompt(At(T0, 2), S, words: 4) }, events);
        Assert.Equal("envelope-over-stamp:payload-file", users[0].OriginRule);
        Assert.Equal(("human", "ledger-origin", "voice"), (users[1].Origin, users[1].OriginRule, users[1].OriginModality));
        // The truncated double wrapper, stamped typed/unknown as the W36 record was.
        var truncated = Only(new[] { Prompt(At(T0, 0), S, text: PayloadTextTruncated, modality: "typed", surface: "unknown") }, events);
        Assert.Equal(("framework", "envelope-over-stamp:payload-file"), (truncated.Origin, truncated.OriginRule));
        // Unstamped: the same class, under the unstamped rule name.
        var unstamped = Only(new[] { Prompt(At(T0, 0), S, text: PayloadText) });
        Assert.Equal(("framework", "envelope:payload-file"), (unstamped.Origin, unstamped.OriginRule));
        // The negative control: FIVE of the developer's own words, then the wrapper quoted whole.
        var own = Words(5);
        Assert.True(own.Length < 100);
        var quoting = Only(new[] { Prompt(At(T0, 0), S, text: own + " " + PayloadText, modality: "typed", surface: "desktop") }, events);
        Assert.Equal(("human", "stamped"), (quoting.Origin, quoting.OriginRule));
        // The garbled record: the head literal intact, everything after "Path:" scrambled.
        var garbled = Only(new[] { Prompt(At(T0, 0), S, text: PayloadTextGarbled, modality: "typed", surface: "unknown") }, events);
        Assert.Equal(("framework", "envelope-over-stamp:payload-file"), (garbled.Origin, garbled.OriginRule));
        // Inspection 2 finding 2: the Inspector's three sentences. Only the product's DATED file name in the opening makes the wrapper.
        Assert.Null(Origin.EnvelopeOf("Please read notes.txt in the .temp directory. Path: .temp/notes.txt before continuing."));
        Assert.Null(Origin.EnvelopeOf("I wrote notes.txt in the .temp directory. Path: .temp/notes.txt is the path."));
        Assert.Null(Origin.EnvelopeOf("Here is why: Read file notes.txt in the .temp directory. Path: .temp/notes.txt"));
        Assert.Null(Origin.EnvelopeOf("Read file notes.txt in the repo. Path: docs/notes.txt. " + Words(6)));
        // Inspection 3 finding 2, anchored after inspection 4 finding 2: the four heads and the two corruptions.
        Assert.Equal("payload-file", Origin.EnvelopeOf(PayloadText));
        Assert.Equal("payload-file", Origin.EnvelopeOf(PayloadTextTruncated));
        Assert.Equal("payload-file", Origin.EnvelopeOf(PayloadTextGarbled));
        Assert.Equal("payload-file", Origin.EnvelopeOf(PayloadText.Substring(17)));                        // '0260901_...' (7 digits)
        Assert.Equal("payload-file", Origin.EnvelopeOf("put_" + PayloadText.Substring(16)));               // cut at 'put_' (12 off)
        Assert.Equal("payload-file", Origin.EnvelopeOf("Read file input_" + ReplaceFirst(PayloadText, "Read file input_20260901_221831_",
            "Read file input_20260901_221831_20260901_221831_")));
        Assert.Equal("payload-file", Origin.EnvelopeOf(ReplaceFirst(PayloadText.Substring(17), "_zz9zz9.txt", "_9zz9.txt")));
        Assert.Equal("payload-file", Origin.EnvelopeOf("0260902_001846_vqhn55.txt in the .temp directorls, search for iy. Path: "
            + ".temp/input_20260902_001846_vqhn55.txt" + Words(6)));
        Assert.Equal("payload-file", Origin.EnvelopeOf("0260902_024027_a1lmaw.txt in the .temp directory. P This file was eath: "
            + ".temp/input_20260902_024027_a1lmaw.txt" + Words(6)));
        // The definition's edges, so a change to any of them is a decision (inspection 4 finding 2).
        var name = "input_20260901_221831_zz9zz9.txt";
        var dated = "20260901_221831_zz9zz9.txt";
        Assert.Equal("payload-file", Origin.EnvelopeOf(dated + " in the .temp directory."));                  // at the record start
        Assert.Equal("payload-file", Origin.EnvelopeOf("  " + dated + " in the .temp directory."));           // leading whitespace is not a head
        Assert.Null(Origin.EnvelopeOf("x" + dated + " in the .temp directory."));                            // one character in front
        Assert.Null(Origin.EnvelopeOf("My export " + dated + " vanished. Could cleaning the .temp folder have deleted it?"));
        Assert.Null(Origin.EnvelopeOf("Read file " + dated + " in the .temp directory."));                    // 'Read file ' without 'input_' is not a known head
        Assert.Equal("payload-file", Origin.EnvelopeOf("Read file " + name + " " + new string('y', 119) + ".temp"));   // 120 characters between
        Assert.Null(Origin.EnvelopeOf("Read file " + name + " " + new string('y', 120) + ".temp"));                    // 121
        Assert.Null(Origin.EnvelopeOf("Read file " + name + " in the temp directory. Path: temp/" + name));
        Assert.Null(Origin.EnvelopeOf("Read file input_2026_221831_zz9zz9.txt in the .temp directory."));   // not the dated shape
        Assert.Equal(120, Origin.PayloadTempWindow);
    }

    private static string ReplaceFirst(string text, string old, string replacement)
    {
        var at = text.IndexOf(old, StringComparison.Ordinal);
        return at < 0 ? text : text.Substring(0, at) + replacement + text.Substring(at + old.Length);
    }

    [Fact]
    public void Envelope_claims_only_an_unstamped_event_and_leaves_the_stamped_one()
    {
        // Envelope at t, unstamped UserInput event at t+2, stamped event at t+1. The envelope takes the
        // unstamped one; a later no-stamp record at t+3 still finds the stamped event and becomes human.
        var prompts = new[]
        {
            Prompt(At(T0, 0), S, text: "Message [message from alpha (BETA-BOX), id 12345678] " + Words(5)),
            Prompt(At(T0, 3), S, words: 4),
        };
        var events = new[]
        {
            Turn(At(T0, 1), S, sendSource: null, inputOrigin: "typed/desktop"),
            Turn(At(T0, 2), S, sendSource: "UserInput", inputOrigin: null),
        };
        var users = Classify(prompts, events);
        Assert.Equal(("agent", "envelope:fleet-message"), (users[0].Origin, users[0].OriginRule));
        Assert.Equal(("human", "ledger-origin"), (users[1].Origin, users[1].OriginRule));
    }

    // ---------------------------------------------------------------- pass 2: agent-tool framings

    private const string CommandText = "<command-message>alpha</command-message><command-name>/alpha</command-name>";

    public static IEnumerable<object[]> AgentToolEntries() => Origin.AgentToolFramings.Select(e => new object[] { e.Name, e.Kind, e.Pattern });
    public static IEnumerable<object[]> NotCommandEntries() => Origin.AgentToolFramings.Where(e => e.Name != Origin.CommandMessageEntry).Select(e => new object[] { e.Name, e.Kind, e.Pattern });

    [Theory]
    [MemberData(nameof(AgentToolEntries))]
    public void Each_agent_tool_framing_is_framework_when_no_stamped_event_is_near(string name, string kind, string pattern)
    {
        Assert.Equal("prefix", kind);
        var text = pattern + Words(6);
        // An UNSTAMPED Framework event nearby must not change the answer: the framing wins before the ledger
        // (pass 2), and a command-message with no stamped event falls to the same class in pass 4.
        var events = new[] { Turn(At(T0, 1), S, sendSource: "Framework", inputOrigin: null) };
        var p = Only(new[] { Prompt(At(T0, 0), S, text: text) }, events);
        Assert.Equal(("framework", "agent-tool:" + name), (p.Origin, p.OriginRule));
        Assert.Null(p.OriginModality);
        Assert.Null(p.OriginSurface);
    }

    [Theory]
    [MemberData(nameof(NotCommandEntries))]
    public void Each_agent_tool_framing_beats_a_stamp_and_claims_no_event(string name, string kind, string pattern)
    {
        Assert.Equal("prefix", kind);
        var text = pattern + Words(6);
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "typed/desktop") };
        var p = Only(new[] { Prompt(At(T0, 0), S, text: text, modality: "typed", surface: "desktop") }, events);
        Assert.Equal(("framework", "agent-tool-over-stamp:" + name), (p.Origin, p.OriginRule));
        Assert.Null(p.OriginModality);
        Assert.Null(p.OriginSurface);
        // ... and the keystroke event stays free: a no-stamp filler record 2 s later takes it.
        var users = Classify(new[] { Prompt(At(T0, 0), S, text: text, modality: "typed", surface: "desktop"), Prompt(At(T0, 2), S, words: 4) }, events);
        Assert.Equal("agent-tool-over-stamp:" + name, users[0].OriginRule);
        Assert.Equal(("human", "ledger-origin", "typed", "desktop"),
            (users[1].Origin, users[1].OriginRule, users[1].OriginModality, users[1].OriginSurface));
    }

    [Fact]
    public void A_stamped_command_message_with_a_free_stamped_event_is_human_from_the_event()
    {
        // The record's own stamp says typed/desktop and the event says voice/phone: the answer must come
        // from the event (pass 4), never from the stamp the join copied onto the wrapper.
        var events = new[] { Turn(At(T0, 2), S, sendSource: null, inputOrigin: "voice/phone") };
        var p = Only(new[] { Prompt(At(T0, 0), S, text: CommandText, modality: "typed", surface: "desktop") }, events);
        Assert.Equal(("human", "ledger-origin", "voice", "phone"), (p.Origin, p.OriginRule, p.OriginModality, p.OriginSurface));
    }

    [Fact]
    public void A_stamped_command_message_with_no_free_stamped_event_is_framework_over_stamp()
    {
        // (a) no event at all
        var p = Only(new[] { Prompt(At(T0, 2), S, text: CommandText, modality: "typed", surface: "desktop") });
        Assert.Equal(("framework", "agent-tool-over-stamp:command-message", (string?)null, (string?)null),
            (p.Origin, p.OriginRule, p.OriginModality, p.OriginSurface));
        // (b) the only stamped event is already claimed by an earlier stamped filler record 1 s before it
        var prompts = new[]
        {
            Prompt(At(T0, 0), S, modality: "typed", surface: "desktop", words: 4),
            Prompt(At(T0, 2), S, text: CommandText, modality: "typed", surface: "desktop"),
        };
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "typed/desktop") };
        var users = Classify(prompts, events);
        Assert.Equal(("human", "stamped"), (users[0].Origin, users[0].OriginRule));
        Assert.Equal(("framework", "agent-tool-over-stamp:command-message", (string?)null), (users[1].Origin, users[1].OriginRule, users[1].OriginModality));
    }

    [Fact]
    public void Command_message_with_a_stamped_event_within_tolerance_is_human()
    {
        var events = new[] { Turn(At(T0, 2), S, sendSource: null, inputOrigin: "typed/desktop") };
        var p = Only(new[] { Prompt(At(T0, 0), S, text: CommandText) }, events);
        Assert.Equal(("human", "ledger-origin", "typed", "desktop"), (p.Origin, p.OriginRule, p.OriginModality, p.OriginSurface));
    }

    [Fact]
    public void Command_message_without_a_stamped_event_is_framework()
    {
        var events = new[] { Turn(At(T0, 2), S, sendSource: "UserInput", inputOrigin: null) };  // unstamped: not enough
        var p = Only(new[] { Prompt(At(T0, 0), S, text: CommandText) }, events);
        Assert.Equal(("framework", "agent-tool:command-message"), (p.Origin, p.OriginRule));
    }

    // ---------------------------------------------------------------- pass 4: the ledger

    [Fact]
    public void Ledger_origin_makes_a_no_stamp_record_human_with_the_events_modality_and_surface()
    {
        var events = new[] { Turn(At(T0, 5), S, sendSource: null, inputOrigin: "voice/cockpit") };
        var p = Only(new[] { Prompt(At(T0, 0), S, words: 3) }, events);
        Assert.Equal(("human", "ledger-origin", "voice", "cockpit"), (p.Origin, p.OriginRule, p.OriginModality, p.OriginSurface));
    }

    [Fact]
    public void Tolerance_is_23_seconds_inclusive()
    {
        Assert.Equal(23, Origin.LedgerJoinSeconds);
        var within = Only(new[] { Prompt(At(T0, 0), S, words: 3) }, new[] { Turn(At(T0, 23), S, sendSource: null, inputOrigin: "typed/desktop") });
        Assert.Equal("ledger-origin", within.OriginRule);
        var beyond = Only(new[] { Prompt(At(T0, 0), S, words: 3) }, new[] { Turn(At(T0, 24), S, sendSource: null, inputOrigin: "typed/desktop") });
        Assert.Equal(("unresolved", "no-ledger-event"), (beyond.Origin, beyond.OriginRule));
    }

    [Fact]
    public void A_stamped_record_claims_its_event_so_one_keystroke_event_never_makes_two_humans()
    {
        var prompts = new[] { Prompt(At(T0, 0), S, modality: "typed", surface: "desktop", words: 3), Prompt(At(T0, 2), S, words: 3) };
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "typed/desktop") };
        var users = Classify(prompts, events);
        Assert.Equal("stamped", users[0].OriginRule);
        Assert.Equal(("unresolved", "no-ledger-event"), (users[1].Origin, users[1].OriginRule));
    }

    [Fact]
    public void Ledger_framework_and_agent_send_sources_classify()
    {
        var prompts = new[] { Prompt(At(T0, 0), S, words: 3), Prompt(At(T0, 100), S, words: 3) };
        var events = new[]
        {
            Turn(At(T0, 1), S, sendSource: "Framework", inputOrigin: null),
            Turn(At(T0, 101), S, sendSource: "Agent", inputOrigin: null),
        };
        var users = Classify(prompts, events);
        Assert.Equal(("framework", "ledger-framework"), (users[0].Origin, users[0].OriginRule));
        Assert.Equal(("agent", "ledger-agent"), (users[1].Origin, users[1].OriginRule));
    }

    [Fact]
    public void Userinput_without_origin_is_unresolved_2639_and_unstamped_null_is_unresolved()
    {
        var prompts = new[] { Prompt(At(T0, 0), S, words: 3), Prompt(At(T0, 100), S, words: 3) };
        var events = new[]
        {
            Turn(At(T0, 1), S, sendSource: "UserInput", inputOrigin: null),
            Turn(At(T0, 101), S, sendSource: null, inputOrigin: null),
        };
        var users = Classify(prompts, events);
        Assert.Equal(("unresolved", "ledger-userinput-2639"), (users[0].Origin, users[0].OriginRule));
        Assert.Equal(("unresolved", "ledger-unstamped"), (users[1].Origin, users[1].OriginRule));
    }

    [Fact]
    public void An_orphan_with_no_event_in_its_session_is_unresolved()
    {
        var events = new[] { Turn(At(T0, 1), "s9", sendSource: "Framework", inputOrigin: null) };  // another session
        var p = Only(new[] { Prompt(At(T0, 0), S, words: 3) }, events);
        Assert.Equal(("unresolved", "no-ledger-event"), (p.Origin, p.OriginRule));
    }

    [Fact]
    public void A_claimed_event_is_never_reused_by_a_later_record()
    {
        var prompts = new[] { Prompt(At(T0, 0), S, words: 3), Prompt(At(T0, 4), S, words: 3) };
        var events = new[] { Turn(At(T0, 2), S, sendSource: "Framework", inputOrigin: null) };
        var users = Classify(prompts, events);
        Assert.Equal("ledger-framework", users[0].OriginRule);
        Assert.Equal("no-ledger-event", users[1].OriginRule);
    }

    [Fact]
    public void An_unstamped_delivery_event_is_an_unexpected_shape_and_stops_the_run()
    {
        var events = new[] { Turn(At(T0, 1), S, sendSource: "Delivery", inputOrigin: null) };
        var error = Assert.Throws<MentorDataException>(() => Classify(new[] { Prompt(At(T0, 0), S, words: 3) }, events));
        Assert.Contains("Delivery with no InputOrigin", error.Message);
    }

    [Fact]
    public void An_unknown_input_origin_token_stops_the_run()
    {
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "typed/tablet") };
        var error = Assert.Throws<MentorDataException>(() => Classify(new[] { Prompt(At(T0, 0), S, words: 3) }, events));
        Assert.Contains("tablet", error.Message);
    }

    [Fact]
    public void Summary_line_carries_counts_only()
    {
        var prompts = new[]
        {
            Prompt(At(T0, 0), S, modality: "typed", surface: "desktop", text: "alpha beta gamma delta kappa"),
            Prompt(At(T0, 60), S, text: "Message [message from alpha (BETA-BOX), id 12345678] zulu tango"),
        };
        var counts = Origin.Classify(prompts, Array.Empty<MentorEvent>());
        var line = Origin.SummaryLine("one", counts);
        Assert.StartsWith("origin one: human 1, agent 1, framework 0, unresolved 0 [", line);
        foreach (var word in new[] { "alpha", "zulu", "tango", "BETA" })
            Assert.DoesNotContain(word, line);
        Assert.Equal(new Dictionary<string, int> { ["human"] = 1, ["agent"] = 1, ["framework"] = 0, ["unresolved"] = 0 }, Origin.ClassCounts(counts));
    }
}
