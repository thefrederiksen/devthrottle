using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.OriginFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's tools/mentor/tests/test_origin_strip.py, every case: origin pass 0
/// (<see cref="Origin.Strips"/>) cuts a framing the agent tool PREPENDS to the developer's own words, the
/// rest is the prompt with its words re-counted, and the record is dropped (framework) only when nothing
/// human remains.
///
/// THE RULE (the Architect's ruling on Mentor on the Gateway inspection 4 finding 1): NO PATH IS EVER
/// STRIPPED. What IS cut is the tool's own, by construction:
///
///     attachment-path   [Image #N] markers at the record start, and ONLY those: the marker RUN - the
///                       markers and the one whitespace character between two markers in a run - and not
///                       one character more. Markers with no word after them: framework.
///     expanded-command  a skill document the tool substituted for a slash command: the record begins with
///                       '# /name' AND carries a '## Input' heading; the cut ends with that LAST heading
///                       line's own newline and the developer's input is everything after it, character for
///                       character. A record beginning '# /name' with NO such heading is human, whole.
///     bash-input        the tool's wrapper for a shell command: framework, whole (an agent-tool framing).
///
/// The generated no-path property draws ELEVEN path families: the reference's nine plus the macOS
/// /Users/ and /Volumes/ families the sixth inspector asked for (W6 had not landed when this port was
/// written, so the port draws them itself). The widening controls at the end put the property red on
/// purpose - an attachment-path matcher widened in memory to cut /home/user/, /Users/ or /Volumes/ - and
/// assert the failure names the family, exactly as the reference's inspectors ran it.
///
/// Every stripped case asserts the stored text is the cut plus the text, character for character. Every
/// prompt text is invented filler except the framings, which carry no developer text.
/// </summary>
[Collection(OriginCollection.Name)]
public sealed class OriginStripTests
{
    private const string S = "s1";
    private const string T0 = "2026-08-25 10:00";
    private static readonly string WORDS = Words(9);
    private const string SCREENSHOT = "D:\\Zeta\\OneDrive\\Pictures\\Screenshots\\Screenshot 2026-08-25 100000.png";
    private const string UPLOAD = "D:\\Zeta\\OneDrive\\Pictures\\Screenshots\\upload-20260825-100100-001.jpg";
    private const string QUOTED = "\"D:\\Zeta\\zeta, inc\\Customer Data - Documents\\Draft 5\\brief_v1.2_results.docx\"";
    private const string FOLDER = "D:\\Zeta\\zeta, inc\\Development - Documents\\Consultants\\Eta";
    private const string PLAIN_FOLDER = "C:\\Users\\zeta\\Videos\\Capture\\2026-08-25_100000_video";
    private static readonly string SKILL_DOCUMENT = "# /loop - schedule a recurring or self-paced prompt\n\nParse the input below into `[interval] "
        + "<prompt>` and schedule it.\n\n## Parsing\n\n1. Leading token: " + Words(12) + "\n\n## Input\n\n";
    private static readonly string[] STAMPS = { "typed", "voice" };

    private static MentorRecord Classified(string text, string modality = "voice", string surface = "desktop", IEnumerable<MentorEvent>? events = null)
        => Only(new[] { Prompt(At(T0, 0), S, text: text, modality: modality, surface: surface) }, events);

    /// <summary>The record is human, its text is exactly the human part, its words are re-counted the
    /// product's way, and the stored text is the strip plus the text - nothing lost, nothing added.</summary>
    private static void AssertStripped(MentorRecord p, string original, string name, string human)
    {
        Assert.Equal(("human", "stamped"), (p.Origin, p.OriginRule));
        Assert.Equal(name, p.OriginStrip);
        Assert.Equal(human, p.Text);
        Assert.Equal(Origin.CountWords(human), p.Words);
        Assert.Equal(p.Text, original.Substring(p.OriginStripChars));
        Assert.Equal(Origin.CountWords(original.Substring(0, p.OriginStripChars)), p.OriginStripWords);
        Assert.Equal(original.Length, p.OriginStripChars + p.Text.Length);
        Assert.Equal(original, original.Substring(0, p.OriginStripChars) + p.Text);   // stored == removed + remaining
    }

    /// <summary>Nothing came off: the record is human, its text is the stored text, character for character.</summary>
    private static void AssertWhole(MentorRecord p, string original)
    {
        Assert.Equal(("human", "stamped"), (p.Origin, p.OriginRule));
        Assert.Null(p.OriginStrip);
        Assert.Equal((0, 0), (p.OriginStripChars, p.OriginStripWords));
        Assert.Equal(original, p.Text);
        Assert.Equal(Origin.CountWords(original), p.Words);
    }

    private static void AssertWholeOnEveryStamp(string original)
    {
        foreach (var modality in STAMPS)
            AssertWhole(Classified(original, modality: modality), original);
    }

    // ---------------------------------------------------------------- the third inspector's six cases (INSPECTION-3.md finding 1)

    [Fact]
    public void A_typed_record_keeps_its_leading_path_whole()
        => AssertWholeOnEveryStamp("D:\\typed\\notes.txt Please review these words");

    [Fact]
    public void A_spaced_folder_then_one_space_and_plain_words_is_kept_whole()
        => AssertWholeOnEveryStamp("D:\\my folder Please review these words");

    [Fact]
    public void A_spaced_folder_then_words_holding_a_dotted_token_is_kept_whole()
        => AssertWholeOnEveryStamp("D:\\my folder Please open notes.txt before continuing");

    [Fact]
    public void A_spaced_folder_then_words_holding_a_double_space_is_kept_whole()
        => AssertWholeOnEveryStamp("D:\\my folder Please review  this record");

    [Fact]
    public void A_marker_without_a_path_loses_the_marker_only()
    {
        var original = "[Image #1] Please inspect this record";
        foreach (var modality in STAMPS)
            AssertStripped(Classified(original, modality: modality), original, "attachment-path",
                " Please inspect this record");          // the space after the marker is not the marker's
    }

    [Fact]
    public void A_developers_own_slash_heading_with_no_input_heading_is_human_whole()
        => AssertWholeOnEveryStamp("# /loop - my own heading\n\nThese are my own words.");

    // ---------------------------------------------------------------- the fourth inspector's three voice cases (INSPECTION-4.md finding 1)

    [Fact]
    public void The_fourth_inspectors_three_voice_cases_stay_whole_nothing_removed()
    {
        foreach (var original in new[]
                 {
                     "D:\\product path with spaces look at app.py first",
                     "D:\\product path with spaces say \"quoted phrase\" next",
                     "D:\\product path with spaces explain this\nthen open app.py",
                 })
        {
            AssertWholeOnEveryStamp(original);
            Assert.Equal((null, 0), Origin.StripOf(original));
        }
    }

    // ---------------------------------------------------------------- the 2026-W36 shapes round 3 stripped: kept whole now, on every stamp

    [Fact]
    public void A_leading_path_with_an_extension_is_kept_whole()
    {
        foreach (var gap in new[] { " ", "  ", "   " })
            AssertWholeOnEveryStamp(SCREENSHOT + gap + WORDS);
    }

    [Fact]
    public void A_leading_quoted_path_is_kept_whole()
    {
        foreach (var gap in new[] { " ", "  " })
            AssertWholeOnEveryStamp(QUOTED + gap + WORDS);
    }

    [Fact]
    public void A_leading_path_ending_at_a_newline_is_kept_whole()
        => AssertWholeOnEveryStamp(FOLDER + "\n" + WORDS);

    [Fact]
    public void Several_leading_paths_are_kept_whole()
    {
        AssertWholeOnEveryStamp(SCREENSHOT + " " + SCREENSHOT.Replace("100000", "100010") + " " + SCREENSHOT.Replace("100000", "100020") + " " + WORDS);
        AssertWholeOnEveryStamp(SCREENSHOT + " " + FOLDER + "  " + WORDS);
    }

    [Fact]
    public void A_folder_path_with_or_without_a_boundary_is_kept_whole()
    {
        foreach (var original in new[]
                 {
                     FOLDER + "  " + WORDS,
                     FOLDER + " " + WORDS,
                     FOLDER + " " + QUOTED + " " + WORDS,
                     PLAIN_FOLDER + "  " + WORDS,
                     FOLDER + "  " + Words(3) + " notes.txt " + WORDS,
                     FOLDER + "  " + Words(2) + " v1.2 " + WORDS,
                     FOLDER + " \"quoted words\" end.txt " + WORDS,
                 })
            AssertWholeOnEveryStamp(original);
    }

    [Fact]
    public void A_path_alone_is_human_whole_on_every_stamp_and_claims_its_event()
    {
        foreach (var modality in STAMPS)
        {
            var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: modality + "/desktop") };
            var p = Classified(SCREENSHOT, modality: modality, events: events);
            AssertWhole(p, SCREENSHOT);
            Assert.Equal(modality, p.OriginModality);
        }
    }

    [Fact]
    public void An_unstamped_record_with_a_leading_path_keeps_it_under_the_ledger()
    {
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "voice/phone") };
        var original = UPLOAD + " " + WORDS;
        var p = Only(new[] { Prompt(At(T0, 0), S, text: original) }, events);
        Assert.Equal(("human", "ledger-origin", "voice", original), (p.Origin, p.OriginRule, p.OriginModality, p.Text));
        Assert.Null(p.OriginStrip);
        Assert.Equal(0, p.OriginStripChars);
    }

    // ---------------------------------------------------------------- the marker: cut, and only the marker

    [Fact]
    public void Image_markers_then_a_screenshot_path_lose_the_markers_only()
    {
        var original = "[Image #1] [Image #2]" + SCREENSHOT + "  " + WORDS;
        foreach (var modality in STAMPS)
        {
            var p = Classified(original, modality: modality);
            AssertStripped(p, original, "attachment-path", SCREENSHOT + "  " + WORDS);
            Assert.Equal("[Image #1] [Image #2]".Length, p.OriginStripChars);
            Assert.Equal(4, p.OriginStripWords);                      // [Image  #1]  [Image  #2]
        }
    }

    [Fact]
    public void One_marker_glued_to_an_upload_path_loses_the_marker_only()
    {
        var original = "[Image #1]" + UPLOAD + " " + WORDS;
        foreach (var modality in STAMPS)
            AssertStripped(Classified(original, modality: modality), original, "attachment-path", UPLOAD + " " + WORDS);
    }

    [Fact]
    public void A_marker_then_a_spaced_folder_loses_the_marker_only()
    {
        var original = "[Image #4]" + FOLDER + "  " + WORDS;
        foreach (var modality in STAMPS)
            AssertStripped(Classified(original, modality: modality), original, "attachment-path", FOLDER + "  " + WORDS);
    }

    [Fact]
    public void Markers_alone_are_framework_and_claim_no_event()
    {
        var events = new[] { Turn(At(T0, 1), S, sendSource: null, inputOrigin: "voice/desktop") };
        foreach (var (text, run) in new[] { ("[Image #1]", "[Image #1]"), ("[Image #1] [Image #2] ", "[Image #1] [Image #2]") })
        {
            var p = Classified(text, modality: "voice", events: events);
            Assert.Equal(("framework", "framing-only:attachment-path", (string?)null), (p.Origin, p.OriginRule, p.OriginModality));
            Assert.Equal((run.Length, 0), (p.OriginStripChars, p.Words));
            Assert.Equal(text.Substring(run.Length), p.Text);            // '' and ' ': the space after the run is not cut
            Assert.Equal(text, run + p.Text);
        }
        var users = Classify(new[]
        {
            Prompt(At(T0, 0), S, text: "[Image #1]", modality: "voice"),
            Prompt(At(T0, 2), S, words: 4),
        }, events);
        Assert.Equal("framing-only:attachment-path", users[0].OriginRule);
        Assert.Equal(("human", "ledger-origin"), (users[1].Origin, users[1].OriginRule));
    }

    // ---------------------------------------------------------------- expanded-command

    [Fact]
    public void The_real_loop_expansion_keeps_the_developers_input_after_its_heading()
    {
        var original = SKILL_DOCUMENT + "10m " + WORDS + "\n";
        foreach (var modality in STAMPS)
        {
            var p = Classified(original, modality: modality);
            AssertStripped(p, original, "expanded-command", "\n10m " + WORDS + "\n");   // the blank line after the heading is the input's
            Assert.Equal(10, p.Words);
            Assert.Equal(SKILL_DOCUMENT.Length - 1, p.OriginStripChars);                // through '## Input\n', not the blank line
        }
    }

    [Fact]
    public void An_expansion_with_no_word_in_its_tail_is_framework_whole()
    {
        foreach (var text in new[] { SKILL_DOCUMENT, SKILL_DOCUMENT + "   \n" })
        {
            var p = Classified(text);
            Assert.Equal(("framework", "framing-only:expanded-command", 0), (p.Origin, p.OriginRule, p.Words));
            Assert.Equal(SKILL_DOCUMENT.Length - 1, p.OriginStripChars);
            Assert.Equal(text.Substring(SKILL_DOCUMENT.Length - 1), p.Text);
            Assert.Equal(text, text.Substring(0, p.OriginStripChars) + p.Text);
        }
    }

    [Fact]
    public void The_boundary_is_the_last_input_heading()
    {
        var original = SKILL_DOCUMENT.Replace("## Parsing", "## Input") + WORDS;
        AssertStripped(Classified(original), original, "expanded-command", "\n" + WORDS);
    }

    // ---------------------------------------------------------------- the fifth inspector's ten cases (INSPECTION-5.md finding 1): the cut is the framing and nothing else

    private static readonly (string Original, string Removed, string Remains)[] FifthInspectorMarkerCases =
    {
        ("[Image #1]word", "[Image #1]", "word"),
        ("[Image #1] word", "[Image #1]", " word"),
        ("[Image #1]   word", "[Image #1]", "   word"),
        ("[Image #1]\n\n    indented word", "[Image #1]", "\n\n    indented word"),
    };
    private static readonly string[] FifthInspectorExpansionTails = { "word", " word", "   word", "\n    indented word", "\tword" };
    private const string EXPANSION = "# /loop heading\n\n## Input\n";

    [Fact]
    public void The_fifth_inspectors_marker_cases_lose_the_marker_and_keep_every_character_after_it()
    {
        foreach (var (original, removed, remains) in FifthInspectorMarkerCases)
        {
            Assert.Equal(original, removed + remains);                  // the table itself is consistent
            foreach (var modality in STAMPS)
            {
                var p = Classified(original, modality: modality);
                AssertStripped(p, original, "attachment-path", remains);
                Assert.Equal(removed.Length, p.OriginStripChars);
                Assert.Equal(("attachment-path", removed.Length), Origin.StripOf(original));
            }
        }
    }

    [Fact]
    public void The_fifth_inspectors_whitespace_led_marker_is_not_cut_at_all()
    {
        var original = "  [Image #1] word";
        AssertWholeOnEveryStamp(original);
        Assert.Equal((null, 0), Origin.StripOf(original));
        foreach (var lead in new[] { "\n", "\t", " \n " })
        {
            AssertWholeOnEveryStamp(lead + "[Image #1] [Image #2]" + SCREENSHOT + " " + WORDS);
            AssertWholeOnEveryStamp(lead + EXPANSION + WORDS);
        }
    }

    [Fact]
    public void The_fifth_inspectors_expansion_tails_keep_every_character_after_the_heading_line()
    {
        foreach (var tail in FifthInspectorExpansionTails)
        {
            var original = EXPANSION + tail;
            foreach (var modality in STAMPS)
            {
                var p = Classified(original, modality: modality);
                AssertStripped(p, original, "expanded-command", tail);
                Assert.Equal(EXPANSION.Length, p.OriginStripChars);
                Assert.Equal(("expanded-command", EXPANSION.Length), Origin.StripOf(original));
            }
        }
    }

    [Fact]
    public void An_expansion_keeps_blank_lines_indentation_and_tabs_in_the_developers_input()
    {
        var original = SKILL_DOCUMENT + "\n    first line, indented\n\tsecond line, tabbed\n\n\n  " + WORDS + "  \n";
        var p = Classified(original);
        AssertStripped(p, original, "expanded-command", "\n\n    first line, indented\n\tsecond line, tabbed\n\n\n  " + WORDS + "  \n");
    }

    [Fact]
    public void A_marker_run_is_the_markers_and_their_single_separators_only()
    {
        foreach (var (original, run) in new[]
                 {
                     ("[Image #1] [Image #2]" + SCREENSHOT, "[Image #1] [Image #2]"),
                     ("[Image #1]\n[Image #2] " + WORDS, "[Image #1]\n[Image #2]"),
                     ("[Image #3] [Image #4] [Image #5]\n" + WORDS, "[Image #3] [Image #4] [Image #5]"),
                     ("[Image #1]  [Image #2] " + WORDS, "[Image #1]"),
                     ("[Image #1] " + WORDS + " [Image #2]", "[Image #1]"),
                 })
        {
            Assert.Equal(("attachment-path", run.Length), Origin.StripOf(original));
            var p = Classified(original);
            AssertStripped(p, original, "attachment-path", original.Substring(run.Length));
            Assert.Equal(run.Length, p.OriginStripChars);
        }
    }

    /// <summary>The sixth inspector's separator table: a tab, a newline and a space are one character each and
    /// join a run; two spaces and a CRLF are not one character and stop it. Pinned so the .NET \s of the marker
    /// pattern is proven to be the Python one.</summary>
    [Fact]
    public void A_marker_run_separator_is_exactly_one_whitespace_character()
    {
        foreach (var (separator, cut) in new[] { (" ", 21), ("  ", 10), ("\t", 21), ("\n", 21), ("\r\n", 10) })
        {
            var original = "[Image #1]" + separator + "[Image #2] word";
            Assert.Equal(("attachment-path", cut), Origin.StripOf(original));
        }
        // The four ASCII separator controls are whitespace to Python and join a run; they would not to .NET's own \s.
        foreach (var control in new[] { '\x1c', '\x1d', '\x1e', '\x1f' })
            Assert.Equal(("attachment-path", 21), Origin.StripOf("[Image #1]" + control + "[Image #2] word"));
    }

    // ---------------------------------------------------------------- bash-input

    [Fact]
    public void The_bash_input_wrapper_is_the_tools_whole_record_whatever_its_stamp()
    {
        var p = Classified("<bash-input> explorer</bash-input>", modality: "typed");
        Assert.Equal(("framework", "agent-tool-over-stamp:bash-input"), (p.Origin, p.OriginRule));
        Assert.Null(p.OriginStrip);
        var unstamped = Only(new[] { Prompt(At(T0, 0), S, text: "<bash-input> explorer</bash-input>") });
        Assert.Equal(("framework", "agent-tool:bash-input"), (unstamped.Origin, unstamped.OriginRule));
    }

    // ---------------------------------------------------------------- the negative controls

    [Fact]
    public void A_path_a_heading_a_marker_or_a_wrapper_written_mid_sentence_is_untouched()
    {
        foreach (var text in new[]
                 {
                     Words(4) + " " + SCREENSHOT + " " + Words(3),
                     Words(2) + " " + QUOTED,
                     Words(3) + " [Image #1] " + Words(2),
                     "# Heading of my own\n\n" + WORDS,
                     "# /not-a-document",                          // a title with nothing after it
                     Words(3) + " <bash-input> ls</bash-input>",
                     "E:/unix/style/path.png " + WORDS,
                 })
        {
            foreach (var modality in STAMPS)
            {
                var p = Classified(text, modality: modality);
                Assert.True(p.OriginStrip is null && p.OriginStripChars == 0, text);
                Assert.True(p.Text == text && p.Words == Origin.CountWords(text), text);
                Assert.True(p.Origin is "human" or "framework", text);
            }
        }
        Assert.Equal("expanded-command", Origin.StripOf("# /loop\n\n## Input\n\n" + WORDS).Name);
        Assert.Equal((null, 0), Origin.StripOf("# /loop\n\n" + WORDS));
        Assert.Equal((null, 0), Origin.StripOf("# Autonomous loop tick " + WORDS));
    }

    // ---------------------------------------------------------------- the no-path ruling as a PROPERTY over path families (INSPECTION-5.md finding 2, INSPECTION-6.md finding 1)

    private static readonly string[] PathSegments =
    {
        "Zeta", "zeta, inc", "Development - Documents", "my folder", "Pictures", "Screenshots",
        "Consultants", "Eta", "notes", "Capture", "2026-08-25_100000_video", "Draft 5", "src", "tmp",
    };
    private static readonly string[] PathStems =
    {
        "Screenshot 2026-08-25 100000", "upload-20260825-100100-001", "brief_v1.2_results", "notes",
        "app", "report", "a b c", "README",
    };
    private static readonly string[] PathExtensions = { ".png", ".jpg", ".txt", ".docx", ".py", ".md", ".log" };
    private static readonly string[] PathGaps = { " ", "  ", "   ", "\n", "\t", " \n" };
    private static readonly string[] PathWords =
    {
        "please", "review", "these", "words", "and", "open", "the", "file", "first", "then", "explain",
        "this", "quoted", "phrase", "next", "look", "at", "app.py", "v1.2", "notes.txt",
    };

    private static string Choice(Random r, string[] pool) => pool[r.Next(pool.Length)];

    private static string Segments(Random r, string separator, bool spaces)
    {
        var pool = spaces ? PathSegments : PathSegments.Where(s => !s.Contains(' ')).ToArray();
        return string.Join(separator, Enumerable.Range(0, r.Next(1, 4)).Select(_ => Choice(r, pool)));
    }

    private static string Leaf(Random r, bool extension)
    {
        var stem = Choice(r, PathStems);
        return extension ? stem + Choice(r, PathExtensions) : stem;
    }

    /// <summary>Every path FAMILY the ruling names, as a generator over a seeded random source: the reference's
    /// nine and the two macOS families of INSPECTION-6.md finding 1. Every family must appear in every run.</summary>
    internal static readonly (string Family, Func<Random, bool, bool, string> Make)[] PathFamilies =
    {
        ("drive-letter", (r, sp, ext) => Choice(r, new[] { "C", "D", "E" }) + ":\\" + Segments(r, "\\", sp) + "\\" + Leaf(r, ext)),
        ("drive-letter-forward-slash", (r, sp, ext) => Choice(r, new[] { "C", "D", "E" }) + ":/" + Segments(r, "/", sp) + "/" + Leaf(r, ext)),
        ("quoted-drive-letter", (r, sp, ext) => "\"" + Choice(r, new[] { "C", "D" }) + ":\\" + Segments(r, "\\", sp) + "\\" + Leaf(r, ext) + "\""),
        ("unc", (r, sp, ext) => "\\\\" + Choice(r, new[] { "server", "nas01", "fileshare" }) + "\\" + Choice(r, new[] { "share", "public" }) + "\\" + Segments(r, "\\", sp) + "\\" + Leaf(r, ext)),
        ("posix-home", (r, sp, ext) => "/home/user/" + Segments(r, "/", sp) + "/" + Leaf(r, ext)),
        ("posix-tmp", (r, sp, ext) => "/tmp/" + Segments(r, "/", sp) + "/" + Leaf(r, ext)),
        ("posix-root", (r, sp, ext) => "/" + Segments(r, "/", sp) + "/" + Leaf(r, ext)),
        ("tilde", (r, sp, ext) => "~/" + Segments(r, "/", sp) + "/" + Leaf(r, ext)),
        ("bare-folder", (r, sp, ext) => Choice(r, new[] { "C", "D" }) + ":\\" + Segments(r, "\\", sp)),
        ("posix-users", (r, sp, ext) => "/Users/" + Choice(r, new[] { "zeta", "soren", "dev user" }) + "/" + Segments(r, "/", sp) + "/" + Leaf(r, ext)),
        ("posix-volumes", (r, sp, ext) => "/Volumes/" + Choice(r, new[] { "External", "Time Machine", "data" }) + "/" + Segments(r, "/", sp) + "/" + Leaf(r, ext)),
    };
    private const int PropertySeed = 20260908;
    private const int PropertyCases = 600;

    /// <summary>(family, text) for <paramref name="cases"/> records that open with a path and continue with
    /// words: first one of every family x spaces x extension combination, so every family is present whatever
    /// the seed draws, then the seeded random fill.</summary>
    internal static List<(string Family, string Text)> GeneratedPathRecords(int seed = PropertySeed, int cases = PropertyCases)
    {
        var r = new Random(seed);
        var drawn = new List<(string, string)>();
        foreach (var (family, make) in PathFamilies)
            foreach (var spaces in new[] { false, true })
                foreach (var extension in new[] { false, true })
                    drawn.Add((family, make(r, spaces, extension)));
        while (drawn.Count < cases)
        {
            var (family, make) = PathFamilies[r.Next(PathFamilies.Length)];
            drawn.Add((family, make(r, r.NextDouble() < 0.5, r.NextDouble() < 0.6)));
        }
        var records = new List<(string, string)>();
        foreach (var (family, pathText) in drawn)
        {
            var tail = Choice(r, PathGaps) + string.Join(" ", Enumerable.Range(0, r.Next(1, 6)).Select(_ => Choice(r, PathWords)));
            records.Add((family, pathText + (r.NextDouble() < 0.9 ? tail : "")));
        }
        return records;
    }

    /// <summary>The fifth inspector's own method: classify one record dict directly, no world on disk.</summary>
    private static MentorRecord ClassifiedDirectly(string text, string modality = "voice")
    {
        var p = new MentorRecord
        {
            Ts = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Session = S, Role = "user", Modality = modality,
            Surface = "desktop", Words = Origin.CountWords(text), Text = text, Pid = "p",
        };
        Origin.Classify(new[] { p }, Array.Empty<MentorEvent>());
        return p;
    }

    /// <summary>The property's body, so the widening controls below can run it and catch its failure.</summary>
    internal static void AssertNoPathIsEverStripped()
    {
        var records = GeneratedPathRecords();
        var familiesSeen = records.Select(r => r.Family).ToHashSet();
        Assert.True(familiesSeen.SetEquals(PathFamilies.Select(f => f.Family)), "a family produced no case");
        Assert.Equal(PropertyCases, records.Count);
        foreach (var (family, text) in records)
        {
            var where = "(" + family + ", " + PropertySeed + ", " + PyTextRepr(text) + ")";
            Assert.True(Origin.StripOf(text) == (null, 0), "strip_of answered " + Origin.StripOf(text) + " for " + where);
            foreach (var strip in Origin.Strips)
                Assert.True(strip.Matcher(text) is null, strip.Name + " matched " + where);
            foreach (var modality in STAMPS)
            {
                var p = ClassifiedDirectly(text, modality);
                Assert.True(p.OriginStrip is null && p.OriginStripChars == 0 && p.OriginStripWords == 0, "a strip field is set for " + where);
                Assert.True(p.Text == text && p.Words == Origin.CountWords(text), "the text or the words changed for " + where);
                Assert.True((p.Origin, p.OriginRule) == ("human", "stamped"), "not human/stamped for " + where);
            }
        }
        Assert.Equal(new[] { "attachment-path", "expanded-command" }, Origin.Strips.Select(s => s.Name));
    }

    private static string PyTextRepr(string text) => "'" + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t") + "'";

    [Fact]
    public void The_strip_table_holds_no_path_shape()
    {
        // The ruling as a PROPERTY of the module, generated across eleven path families: drive letter,
        // drive letter with forward slashes, quoted drive letter, UNC, POSIX under /home, /tmp and the root,
        // ~/, a bare folder, macOS /Users/ and /Volumes/ - with and without spaces in a segment, with and
        // without an extension, followed by a gap and one to five words or by nothing. For every one of them,
        // on both stamps, StripOf answers (null, 0), every matcher answers null, and the classified record is
        // whole. The widening controls below put it red on purpose.
        AssertNoPathIsEverStripped();
    }

    [Fact]
    public void A_marker_run_before_any_generated_path_is_cut_to_its_exact_length_and_no_further()
    {
        var r = new Random(PropertySeed + 1);
        foreach (var (family, text) in GeneratedPathRecords())
        {
            var run = string.Join(" ", Enumerable.Range(0, r.Next(1, 4)).Select(_ => "[Image #" + r.Next(1, 121) + "]"));
            var gap = Choice(r, new[] { "", "", " ", "\n", "\n\n    " });
            var original = run + gap + text;
            Assert.True(Origin.StripOf(original) == ("attachment-path", run.Length), family + ": " + original);
            foreach (var modality in STAMPS)
            {
                var p = ClassifiedDirectly(original, modality);
                Assert.Equal("attachment-path", p.OriginStrip);
                Assert.Equal(run.Length, p.OriginStripChars);
                Assert.True(p.Text == gap + text && original == run + p.Text, family + ": " + original);
                Assert.Equal(Origin.CountWords(run), p.OriginStripWords);
            }
        }
    }

    /// <summary>
    /// THE WIDENING CONTROLS: the sixth inspector widened the reference's attachment-path matcher in memory to
    /// cut /Users/ and the property stayed green, because no family drew that path (INSPECTION-6.md finding
    /// 1). Here the port's table is widened the same way, for /home/user/ (the fifth inspector's widening),
    /// /Users/ and /Volumes/, and the property must go RED naming the family. A property that cannot fail
    /// under a widening proves nothing about the next matcher somebody adds.
    /// </summary>
    [Theory]
    [InlineData("/home/user/", "posix-home")]
    [InlineData("/Users/", "posix-users")]
    [InlineData("/Volumes/", "posix-volumes")]
    public void The_property_goes_red_when_the_attachment_path_matcher_is_widened_to_cut_a_path(string prefix, string family)
    {
        var shipped = Origin.ShippedStrips;
        var widened = new Origin.Strip("attachment-path",
            text => text.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length : Origin.StripAttachmentPath(text),
            "WIDENED IN MEMORY BY A TEST");
        Origin.Strips = new[] { widened, shipped[1] };
        try
        {
            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(AssertNoPathIsEverStripped);
            Assert.Contains("(" + family + ", " + PropertySeed + ", ", failure.Message);
        }
        finally
        {
            Origin.Strips = shipped;
        }
        AssertNoPathIsEverStripped();      // and green again once the table is the shipped one
    }

    [Fact]
    public void The_strip_fields_are_present_on_every_record()
    {
        var prompts = new[] { Prompt(At(T0, 0), S, modality: "typed"), Prompt(At(T0, 5), S, role: "assistant") };
        Origin.Classify(prompts, Array.Empty<MentorEvent>());
        foreach (var p in prompts)
            Assert.True(p.OriginStrip is null && p.OriginStripChars == 0 && p.OriginStripWords == 0);
    }
}
