using System.Reflection;
using System.Text.RegularExpressions;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Speech;

/// <summary>
/// EVERY FIXED SENTENCE THE PRODUCT SPEAKS EXISTS IN EVERY LANGUAGE IT SPEAKS (issue #1009).
///
/// The owner's decision was explicit and left no room for a partial job: everything speaks the language,
/// no exceptions, and no English fragment reachable in a fully French or fully Spanish session. So these
/// tests are about coverage that cannot rot rather than about any one sentence being right:
///   - every phrase has all three languages, and adding a fourth language turns them all red;
///   - the slots in a translation match the slots in the English, so a missing one cannot silently speak
///     a sentence with a hole in it;
///   - no English original is still reachable anywhere in the Gateway;
///   - the accented characters SURVIVE THE COMPILER, checked against escape sequences rather than
///     accented literals, because a test file decoded the same wrong way would otherwise agree with the
///     bug;
///   - and no spoken text is ever written to a log, which is what keeps the repository's ASCII rule
///     intact rather than argued around.
///
/// The last two are the guards the Architect's accents ruling requires
/// (docs/MISSION-multilingual-RULINGS.md). Neither is optional.
/// </summary>
public sealed class SpokenPhraseTests
{
    // ----------------------------------------------------------------------------------------------
    // Coverage: no language can be left out, and none can be left out later either.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE ACCEPTANCE ROW ON ISSUE #1009: a test that fails if a spoken string has no translation for a
    /// supported language.
    ///
    /// Revert-proof in the direction that matters: add a language to <see cref="SpokenLanguages.All"/>
    /// and every phrase goes red until it is translated. That is the failure mode this guards - not
    /// somebody deleting a translation, but somebody adding a language and shipping it half-covered,
    /// which is what a dropdown offering twelve languages did last time.
    /// </summary>
    [Fact]
    public void Every_phrase_exists_in_every_language_the_product_speaks()
    {
        var missing = new List<string>();

        foreach (var phrase in SpokenPhrases.All)
        foreach (var language in SpokenLanguages.All)
        {
            if (!phrase.Translations.TryGetValue(language.Code, out var text) || string.IsNullOrWhiteSpace(text))
                missing.Add($"{phrase.Key} / {language.Code}");
        }

        Assert.True(missing.Count == 0,
            "Every fixed spoken sentence must exist in every language the product speaks. The owner's "
            + "decision on issue #1009 was that everything speaks the language, with no exceptions. "
            + "Missing: " + string.Join(", ", missing));
    }

    /// <summary>Every phrase declared on <see cref="SpokenPhrases"/> is in its <c>All</c> list. A phrase
    ///  that is declared and not listed is invisible to every test above it - covered by nothing, and
    ///  looking covered.</summary>
    [Fact]
    public void Every_declared_phrase_is_in_the_All_list()
    {
        var declared = typeof(SpokenPhrases)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(SpokenPhrase))
            .Select(f => (SpokenPhrase)f.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(declared);
        var listed = SpokenPhrases.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var orphans = declared.Where(p => !listed.Contains(p.Key)).Select(p => p.Key).ToList();

        Assert.True(orphans.Count == 0,
            "A phrase is declared on SpokenPhrases but missing from SpokenPhrases.All, so no coverage test "
            + "can see it. Add it to the list: " + string.Join(", ", orphans));
    }

    /// <summary>
    /// The slots line up across languages. A French sentence that dropped its <c>{0}</c> would speak a
    /// grammatical sentence with the session name missing - "C'est fait, j'ai supprime." - and nothing
    /// would fail. A French sentence that gained a <c>{1}</c> would throw at run time, in a car.
    /// </summary>
    [Fact]
    public void Every_translation_uses_the_same_slots_as_the_English()
    {
        var mismatches = new List<string>();

        foreach (var phrase in SpokenPhrases.All)
        {
            var english = Slots(phrase.In(SpokenLanguages.English));
            foreach (var language in SpokenLanguages.All)
            {
                var slots = Slots(phrase.In(language));
                if (!english.SetEquals(slots))
                    mismatches.Add($"{phrase.Key} / {language.Code}: expected [{string.Join(",", english.Order())}], found [{string.Join(",", slots.Order())}]");
            }
        }

        Assert.True(mismatches.Count == 0,
            "A translation must use exactly the same slots as the English. A dropped slot speaks a sentence "
            + "with a fact missing and fails nothing; an extra one throws mid-drive. " + string.Join("; ", mismatches));
    }

    /// <summary>Every phrase actually DIFFERS between languages. Three identical strings would satisfy
    ///  every coverage check above while the product spoke English to everyone - which is precisely what
    ///  "the setting does nothing" looked like on the last attempt.</summary>
    [Fact]
    public void Every_phrase_really_is_translated_and_not_three_copies_of_the_English()
    {
        var untranslated = SpokenPhrases.All
            .Where(p => SpokenLanguages.All.Select(l => p.In(l)).Distinct(StringComparer.Ordinal).Count() != SpokenLanguages.All.Count)
            .Select(p => p.Key)
            .ToList();

        Assert.True(untranslated.Count == 0,
            "A phrase reads identically in two languages, so at least one of them was never translated: "
            + string.Join(", ", untranslated));
    }

    /// <summary>
    /// AN UNKNOWN LANGUAGE CANNOT REACH A PHRASE AT ALL, because it cannot be constructed.
    ///
    /// This test used to build <c>new SpokenLanguage("xx", "Klingon", "Klingon")</c> and check that asking a
    /// phrase for it threw naming the phrase KEY and never the words. That was the right guarantee for a type
    /// which accepted any code. The type now refuses a code that is not one we speak (audit 4 / the re-audit's
    /// root cause: nonblank was never the same as known), so the line that made the unknown language throws
    /// FIRST - which left the old assertion unreachable and the suite red.
    ///
    /// The property is stronger now and this asserts the stronger one: the missing-translation path cannot be
    /// entered through a real language object. What keeps it that way is the completeness test above - every
    /// phrase has every known language - so the pair is "no unknown language exists" and "no known language is
    /// missing a translation".
    ///
    /// The refusal names the CODE, which is safe: a code is ASCII. It must never carry spoken words, and it
    /// cannot, because it never sees a phrase.
    /// </summary>
    [Fact]
    public void An_unknown_language_cannot_be_constructed_so_it_can_never_reach_a_phrase()
    {
        var error = Assert.Throws<ArgumentException>(() => new SpokenLanguage("xx", "Klingon", "Klingon"));

        Assert.Contains("xx", error.Message);
        Assert.DoesNotContain(SpokenPhrases.VoiceTurnBlockedUnreadable.In(SpokenLanguages.English), error.Message);

        // And every language that CAN exist has this phrase, so the throw inside In() has no way to fire.
        foreach (var language in SpokenLanguages.All)
            Assert.False(string.IsNullOrWhiteSpace(SpokenPhrases.VoiceTurnBlockedUnreadable.In(language)));
    }

    // ----------------------------------------------------------------------------------------------
    // GUARD FROM THE ACCENTS RULING (1 of 2): the encoding survives the real loading path.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE ACCENTED CHARACTERS SURVIVE THE COMPILER, BYTE FOR BYTE.
    ///
    /// This is the guard the Architect's ruling requires, and it is not decoration. The real loading path
    /// for these strings is the C# compiler reading SpokenPhrases.cs. If that file is ever decoded as
    /// this machine's default code page instead of UTF-8 - and this machine defaults to cp1252 - then
    /// e-acute becomes two mojibake characters, NOTHING fails, and the only symptom is a voice
    /// mispronouncing French to a customer.
    ///
    /// The expectations below are \u ESCAPE SEQUENCES, deliberately, and this test file contains no
    /// accented literal of its own. Written as accented literals they would be decoded by the same
    /// compiler under the same wrong assumption and would agree with the bug - a test that cannot fail.
    /// An escape is decided by the compiler's own lexer and cannot be mis-decoded.
    /// </summary>
    [Fact]
    public void Accented_characters_survive_the_compiler_byte_for_byte()
    {
        // "... je n'ai pas re<e-acute>ussi a<a-grave> le lire ..." - the accents are the whole point of the assertion.
        Assert.Contains("r\u00E9ussi \u00E0", SpokenPhrases.VoiceTurnBlockedMenu.In(SpokenLanguages.French));
        // "... je ne lirai pas la suite." after "Ce re<e-acute>sume<e-acute> est trop long".
        Assert.Contains("r\u00E9sum\u00E9", SpokenPhrases.NarrationCutNotice.In(SpokenLanguages.French));
        // Spanish: "menu<u-acute>" and the inverted question mark family via "opcio<o-acute>n".
        Assert.Contains("men\u00FA", SpokenPhrases.VoiceTurnBlockedMenu.In(SpokenLanguages.Spanish));
        Assert.Contains("Opci\u00F3n", SpokenPhrases.MenuOption.In(SpokenLanguages.Spanish, 1, "x"));

        // And the mojibake shape itself is absent. A cp1252 misread of UTF-8 e-acute produces exactly
        // this pair, so naming it turns a silent corruption into a named failure.
        foreach (var phrase in SpokenPhrases.All)
        foreach (var language in SpokenLanguages.All)
        {
            Assert.DoesNotContain("\u00C3\u00A9", phrase.In(language, 1, "x"));   // e-acute read as cp1252
            Assert.DoesNotContain("\uFFFD", phrase.In(language, 1, "x"));          // the replacement character
        }
    }

    /// <summary>
    /// The file on disk is UTF-8 WITH a byte order mark. The test above proves the bytes survived THIS
    /// build; the mark is what makes that true for every build, on every machine, regardless of what the
    /// operating system's default code page happens to be. Without it the compiler is guessing, and it
    /// guesses right until the day it does not.
    /// </summary>
    [Fact]
    public void The_phrase_file_is_utf8_with_a_byte_order_mark()
    {
        var path = Path.Combine(RepoRoot(), "src", "CcDirector.Gateway", "Speech", "SpokenPhrases.cs");
        Assert.True(File.Exists(path), $"Expected the phrase file at {path}.");

        var head = new byte[3];
        using (var stream = File.OpenRead(path)) Assert.Equal(3, stream.Read(head, 0, 3));

        Assert.True(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
            "SpokenPhrases.cs must be saved as UTF-8 WITH a byte order mark. Without it the C# compiler "
            + "guesses the encoding, and on a machine defaulting to cp1252 an accented letter silently "
            + "becomes mojibake - the build stays green and the voice pronounces French wrong.");
    }

    // ----------------------------------------------------------------------------------------------
    // GUARD FROM THE ACCENTS RULING (2 of 2): spoken text never reaches a log.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// NO SPOKEN TEXT IS EVER WRITTEN TO A LOG OR A CONSOLE.
    ///
    /// This is what keeps the repository's ASCII-output rule intact rather than merely argued around.
    /// Accents are allowed in the payload precisely BECAUSE the payload never reaches an output channel;
    /// the moment a log line interpolates a spoken string, the exemption stops being true and a Windows
    /// terminal gets the encoding error the rule exists to prevent.
    ///
    /// So every logging call in the Gateway is scanned for an interpolation of a spoken value. Lengths,
    /// keys and counts are fine and are what the existing log lines already use.
    ///
    /// Revert-proof: change any log line to interpolate the words instead of the length and this goes red
    /// naming the file and the line.
    /// </summary>
    [Fact]
    public void No_log_line_writes_spoken_text()
    {
        var offenders = new List<string>();

        foreach (var (relativePath, content) in GatewaySourceFiles())
        {
            var lineNumber = 0;
            foreach (var line in content.Split('\n'))
            {
                lineNumber++;
                if (!IsLoggingCall(line)) continue;
                foreach (var expression in InterpolatedExpressions(line))
                {
                    if (IsSpokenValue(expression))
                        offenders.Add($"{relativePath}:{lineNumber} logs {{{expression}}}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A log line writes spoken text. Spoken strings carry accents, and the repository's ASCII rule "
            + "protects output channels - so a log must carry the phrase KEY, its LENGTH, or a count, never "
            + "the words. See docs/MISSION-multilingual-RULINGS.md. Found:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Sanity on the log scan: it can see the logging calls it claims to check. A scanner that
    ///  matches nothing passes forever.</summary>
    [Fact]
    public void The_log_scan_can_see_logging_calls()
    {
        var seen = GatewaySourceFiles()
            .SelectMany(f => f.Content.Split('\n'))
            .Count(IsLoggingCall);

        Assert.True(seen > 100, $"The log scan found only {seen} logging calls in the Gateway - it is not looking where it thinks it is.");
    }

    // ----------------------------------------------------------------------------------------------
    // No English original is still reachable.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE ENGLISH ORIGINALS ARE GONE FROM EVERYWHERE BUT THE PHRASE FILE.
    ///
    /// Each of these strings used to be a constant in the Gateway, spoken to everybody in English. If one
    /// is still there, it is still reachable, and an account set to French still hears it - which is the
    /// literal wording of the owner's decision on issue #1009: "no English fragment reachable in a fully
    /// French or Spanish session".
    ///
    /// Revert-proof: paste any of these back into the file it came from and this goes red naming it.
    /// </summary>
    [Fact]
    public void No_English_original_of_a_spoken_phrase_survives_outside_the_phrase_file()
    {
        var fragments = new[]
        {
            "Okay, I left",
            "Done. I deleted",
            "I'm having trouble answering that right now",
            "I'm your fleet manager, and you talk to me two ways",
            "so I won't answer it blindly",
            "so I won't type your answer in blindly",
            "and I can't pick an option for you yet",
            "Heads up - this session is now waiting on a menu",
            "That is as much as I can read out",
            "Say the number, or the option.",
            "Say which ones apply, then say done.",
        };
        var offenders = new List<string>();

        foreach (var (relativePath, content) in GatewaySourceFiles())
        {
            if (relativePath.EndsWith("/Speech/SpokenPhrases.cs", StringComparison.Ordinal)) continue;
            foreach (var fragment in fragments)
            {
                if (content.Contains(fragment, StringComparison.Ordinal))
                    offenders.Add($"{relativePath}: \"{fragment}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "An English spoken string still lives outside SpokenPhrases, so an account set to French or "
            + "Spanish can still hear it. Move it into SpokenPhrases and translate it. Found:\n  "
            + string.Join("\n  ", offenders));
    }

    // ----------------------------------------------------------------------------------------------
    // The phrases actually reach the places that speak them.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A MENU IS READ OUT AS WHOLE TRANSLATED SENTENCES, not an English frame with the labels swapped.
    ///
    /// Issue #1009 names this method as the one thing that "cannot be translated as written", because it
    /// glued the word "Option", a number, a colon and a "(recommended)" tag together in code - decisions
    /// that differ per language and had already been made for English.
    /// </summary>
    [Fact]
    public void A_menu_is_read_in_whole_sentences_in_the_account_language()
    {
        var menu = new WingmanMenu
        {
            IsMenu = true,
            Question = "Voulez-vous continuer ?",
            SelectionMode = "single",
            Options = new List<WingmanMenuOption>
            {
                new() { Key = "1. Oui", Send = "1\r", Recommended = true },
                new() { Key = "2. Non", Send = "2\r" },
            },
        };

        var french = WingmanTranslator.BuildMenuSpoken(SpokenLanguages.French, menu);

        Assert.Contains("Voulez-vous continuer ?", french);
        Assert.Contains(SpokenPhrases.MenuOptionRecommended.In(SpokenLanguages.French, 1, "Oui"), french);
        Assert.Contains(SpokenPhrases.MenuOption.In(SpokenLanguages.French, 2, "Non"), french);
        Assert.Contains(SpokenPhrases.MenuAnswerSingle.In(SpokenLanguages.French), french);
        // The English frame is gone entirely - not merely outnumbered.
        Assert.DoesNotContain("Option 1:", french);
        Assert.DoesNotContain("recommended", french);
        Assert.DoesNotContain("Say the number", french);
    }

    /// <summary>
    /// The menu-DETECTION prompt asks for its spoken fields in the account's language, without applying
    /// the plain-prose rule to the JSON envelope it must return. Both halves matter: the first stops a
    /// French reading from wrapping an English question, and the second stops the guard from breaking
    /// menu handling, which decides whether a keypress lands in somebody's terminal.
    /// </summary>
    [Fact]
    public void The_menu_detect_prompt_asks_for_spoken_fields_in_the_language_but_still_returns_json()
    {
        var prompt = WingmanTranslator.BuildMenuDetectPrompt(SpokenLanguages.Spanish, "1. Yes  2. No");

        Assert.Contains(SpeechContract.SpeakInLanguageRule(SpokenLanguages.Spanish), prompt);
        Assert.Contains("READ ALOUD", prompt);
        Assert.DoesNotContain(SpeechContract.PlainSpokenProseRule, prompt);
        Assert.Contains("Output ONLY this JSON", prompt);
    }

    // ----------------------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------------------

    /// <summary>The <c>{0}</c>-style slots in a composite format string.</summary>
    private static HashSet<string> Slots(string text)
        => Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>The expressions inside <c>{...}</c> holes of an interpolated string on one line, with any
    ///  <c>:format</c> suffix dropped. Only simple names and member chains are matched, which is every
    ///  shape the Gateway's log lines actually use.</summary>
    private static IEnumerable<string> InterpolatedExpressions(string line)
    {
        foreach (Match m in Regex.Matches(line, @"\{([A-Za-z_][A-Za-z_0-9]*(?:\.[A-Za-z_][A-Za-z_0-9]*)*)(?::[^}]*)?\}"))
            yield return m.Groups[1].Value;
    }

    /// <summary>
    /// True when an interpolated expression is the spoken WORDS rather than a fact about them.
    ///
    /// The distinction is the whole point of the guard, and getting it wrong in either direction would
    /// make the guard useless: flagging <c>spoken.Length</c> would condemn the correct log lines the
    /// Gateway already writes, and flagging nothing would let the words through. So a measurement of a
    /// spoken value is fine and the value itself is not.
    /// </summary>
    private static bool IsSpokenValue(string expression)
    {
        var measurements = new[] { "Length", "Count" };
        var lastDot = expression.LastIndexOf('.');
        if (lastDot > 0 && measurements.Contains(expression[(lastDot + 1)..], StringComparer.Ordinal))
            return false;

        var spokenNames = new[]
        {
            "spoken", "finalSpoken", "spokenCancel", "spokenDone", "giveUp", "words", "narration",
            "text", "input", "userText", "cutNotice", "script",
        };
        if (spokenNames.Contains(expression, StringComparer.Ordinal)) return true;

        // A member chain ending in the spoken value itself: t.Spoken, result.Spoken, explain.Spoken.
        return lastDot > 0
               && (expression[(lastDot + 1)..] is "Spoken" or "Text"
                   || spokenNames.Contains(expression[(lastDot + 1)..], StringComparer.Ordinal));
    }

    /// <summary>True when the line is a call to a logging sink. Covers the Gateway's two shapes: the
    ///  static <c>FileLog.Write</c> and the injected <c>_log(...)</c> delegate.</summary>
    private static bool IsLoggingCall(string line)
        => line.Contains("FileLog.Write(", StringComparison.Ordinal)
           || line.Contains("_log($", StringComparison.Ordinal)
           || line.Contains("_log(\"", StringComparison.Ordinal)
           || line.Contains("log(\"", StringComparison.Ordinal);

    /// <summary>Every production source file in the Gateway project.</summary>
    private static IReadOnlyList<(string Path, string Content)> GatewaySourceFiles()
    {
        var root = RepoRoot();
        var gateway = Path.Combine(root, "src", "CcDirector.Gateway");
        var files = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(gateway, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
            files.Add((rel, File.ReadAllText(file)));
        }
        Assert.NotEmpty(files);
        return files;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

/// <summary>Ordering helper so a failure message lists slots deterministically.</summary>
internal static class SlotOrdering
{
    public static IEnumerable<string> Order(this HashSet<string> slots) => slots.OrderBy(s => s, StringComparer.Ordinal);
}
