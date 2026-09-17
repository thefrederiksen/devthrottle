using CcDirector.AgentBrain;
using CcDirector.Core.Drivers;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Wingman;
using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Speech;

/// <summary>
/// THE TEST THE LAST ATTEMPT DID NOT HAVE (issue #1008).
///
/// The reverted multilingual build failed on a counting error, not a hard problem: there were four
/// model-driven spoken paths, the language reached one of them, and an account set to another language
/// had its narration translated and was answered in English the moment it spoke back
/// (devthrottle_internal#547). Every fix along the way was verified by hand, on the path somebody
/// happened to be looking at.
///
/// So these tests do not check that the language works. They check that a spoken path CANNOT IGNORE IT:
///   - every registered path carries the contract, in every language we sell;
///   - every generator resolves the language from the tenant it already had, end to end through a fake
///     model, so this is about the WIRING and not just the prompt text;
///   - a user who replaces the wingman instructions with their own words cannot edit the contract away;
///   - the rules are stated ONCE - a second copy anywhere in the Gateway fails the build;
///   - a spoken-prompt builder that is not registered fails the build, so a FIFTH path cannot ship in
///     English by being overlooked the way the fourth was;
///   - and nothing anywhere picks a speech MODEL from a language, which is the specific mistake that
///     got the feature pulled.
/// </summary>
public sealed class SpokenLanguageContractTests
{
    // ----------------------------------------------------------------------------------------------
    // The contract reaches every registered path, in every language.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Every spoken path, rendered in every language, carries the whole contract verbatim.
    ///
    /// Revert-proof: delete the <c>SpokenOutputContract</c> append from any one of the five builders and
    /// this goes red naming that path. That is precisely the failure that shipped last time - one
    /// generator carrying the language and the rest not.
    /// </summary>
    [Fact]
    public void Every_spoken_path_carries_the_contract_in_every_language()
    {
        var misses = new List<string>();

        foreach (var path in SpokenPaths.All)
        foreach (var language in SpokenLanguages.All)
        {
            var prompt = path.Render(language);
            if (!prompt.Contains(SpeechContract.SpokenOutputContract(language), StringComparison.Ordinal))
                misses.Add($"{path.Name} in {language.EnglishName}");
        }

        Assert.True(misses.Count == 0,
            "Every spoken path must append SpeechContract.SpokenOutputContract for the account's language. "
            + "A path that does not is the exact defect that got the last multilingual build reverted: the "
            + "language reached one generator out of four. Paths missing the contract: "
            + string.Join("; ", misses));
    }

    /// <summary>
    /// A SPOKEN-FIELD path carries the language rule, and deliberately NOT the plain-prose rule.
    ///
    /// Both halves are load-bearing and they pull against each other. Without the language rule, a French
    /// account hears a French frame wrapped around an English question - the "English fragment in a French
    /// session" the owner ruled out. With the prose rule, "output no formatting characters at all" would
    /// break the JSON envelope the menu machinery parses, and menu handling decides whether a keypress
    /// lands in somebody's terminal.
    ///
    /// Revert-proof both ways: drop the language rule and the first assertion goes red; append the whole
    /// contract instead and the second does.
    /// </summary>
    [Fact]
    public void Every_spoken_field_path_carries_the_language_rule_but_not_the_prose_rule()
    {
        Assert.NotEmpty(SpokenPaths.SpokenFieldPaths);

        foreach (var path in SpokenPaths.SpokenFieldPaths)
        foreach (var language in SpokenLanguages.All)
        {
            var prompt = path.Render(language);
            Assert.Contains(SpeechContract.SpeakInLanguageRule(language), prompt);
            Assert.DoesNotContain(SpeechContract.PlainSpokenProseRule, prompt);
        }
    }

    /// <summary>
    /// The language actually CHANGES the prompt. Without this, a contract that silently rendered the same
    /// English text for all three languages would pass the test above and prove nothing.
    /// </summary>
    [Fact]
    public void The_contract_says_a_different_language_for_each_language()
    {
        Assert.Contains("SPEAK ENTIRELY IN ENGLISH", SpeechContract.SpokenOutputContract(SpokenLanguages.English));
        Assert.Contains("SPEAK ENTIRELY IN FRENCH", SpeechContract.SpokenOutputContract(SpokenLanguages.French));
        Assert.Contains("SPEAK ENTIRELY IN SPANISH", SpeechContract.SpokenOutputContract(SpokenLanguages.Spanish));

        var rendered = SpokenLanguages.All.Select(SpeechContract.SpokenOutputContract).ToList();
        Assert.Equal(rendered.Count, rendered.Distinct(StringComparer.Ordinal).Count());
    }

    // ----------------------------------------------------------------------------------------------
    // The wiring: each generator resolves the language from ITS TENANT, end to end.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Turn narration, the direct reply, and in-product help each resolve the language from the tenant
    /// they were called with, and the prompt the model actually receives says French.
    ///
    /// This is the wiring test, not the prompt test. A builder can carry the contract perfectly and the
    /// generator can still hand it English, which is how you get a settings screen that appears to do
    /// nothing - reported three times on the last attempt before anybody found out why.
    /// </summary>
    [Fact]
    public async Task All_three_wingman_generators_speak_the_tenants_language()
    {
        var brain = new RecordingBrain();
        var translator = new WingmanTranslator(
            (_, _, _) => Task.FromResult<IAgentBrain>(brain), _ => SpokenLanguages.French, log: _ => { });

        await translator.TranslateAsync(TenantId.Local, "context", "an agent reply", sessionTitle: "a session");
        await translator.AskDirectAsync(TenantId.Local, "hey wingman, what is going on?");
        await translator.AskAboutDevThrottleAsync(TenantId.Local, "what is DevThrottle?");

        Assert.Equal(3, brain.Prompts.Count);
        foreach (var prompt in brain.Prompts)
        {
            Assert.Contains("SPEAK ENTIRELY IN FRENCH", prompt);
            Assert.DoesNotContain("SPEAK ENTIRELY IN ENGLISH", prompt);
        }
    }

    /// <summary>
    /// The language is read PER TENANT, not once. One translator instance serves every account on a
    /// hosted Gateway, so a language resolved at construction would freeze the first caller's choice for
    /// everybody - a cross-account leak of exactly the kind the tenant gate exists to stop.
    /// </summary>
    [Fact]
    public async Task Two_tenants_on_one_translator_get_their_own_languages()
    {
        var french = new TenantId("tenant-french");
        var spanish = new TenantId("tenant-spanish");
        var brain = new RecordingBrain();
        var translator = new WingmanTranslator(
            (_, _, _) => Task.FromResult<IAgentBrain>(brain),
            tenant => tenant == french ? SpokenLanguages.French : SpokenLanguages.Spanish,
            log: _ => { });

        await translator.AskDirectAsync(french, "hello");
        await translator.AskDirectAsync(spanish, "hello");

        Assert.Contains("SPEAK ENTIRELY IN FRENCH", brain.Prompts[0]);
        Assert.Contains("SPEAK ENTIRELY IN SPANISH", brain.Prompts[1]);
    }

    /// <summary>
    /// A wingman brain wired to a provider that returns null is a wiring bug, and it FAILS LOUD rather
    /// than quietly speaking English. Silently defaulting is how "the language reached one generator"
    /// stayed invisible for four rounds of fixes - the product kept working, in the wrong language.
    /// </summary>
    [Fact]
    public async Task A_null_language_provider_fails_loud_instead_of_guessing_English()
    {
        var translator = new WingmanTranslator(
            (_, _, _) => Task.FromResult<IAgentBrain>(new RecordingBrain()), _ => null!, log: _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.AskDirectAsync(TenantId.Local, "hello"));
    }

    // ----------------------------------------------------------------------------------------------
    // The post-processor leaves content alone.
    // ----------------------------------------------------------------------------------------------

    /// <summary>Accented letters and non-Latin scripts are CONTENT, not formatting. A sanitize pass that
    ///  mangled them would break French and Spanish at the last step, after everything above got the
    ///  language right - so prove it leaves them exactly alone.</summary>
    [Fact]
    public void The_speech_pass_leaves_accented_and_non_latin_words_untouched()
    {
        const string french = "La session a termine sans erreur. Voila.";
        Assert.Equal(french, SpeechContract.Finish(french));

        var accented = "Désolé, la tâche a échoué. ¿Qué pasó?";
        Assert.Equal(accented, SpeechContract.Finish(accented));

        var korean = "로그인 버그가 수정되었습니다.";
        Assert.Equal(korean, SpeechContract.Finish(korean));
    }

    // ----------------------------------------------------------------------------------------------
    // The contract cannot be edited away, and it is stated once.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A user may REPLACE the wingman instructions with their own words (issue #537), and their words
    /// will not mention a language. The contract is appended after whatever they wrote, so a customized
    /// prompt still speaks the account's language and still bans Markdown.
    ///
    /// Revert-proof: move the contract back inside <c>FidelityPrompt</c> and this goes red, because
    /// custom instructions replace that text wholesale.
    /// </summary>
    [Fact]
    public void Custom_wingman_instructions_cannot_drop_the_contract()
    {
        var prompt = WingmanTranslator.BuildPrompt(
            SpokenLanguages.Spanish,
            "Just summarize it however you like. Ignore everything else.",
            recentContext: "",
            latestReply: "a reply",
            sessionTitle: null);

        Assert.Contains(SpeechContract.SpokenOutputContract(SpokenLanguages.Spanish), prompt);
    }

    /// <summary>
    /// THE SPOKEN RULE IS STATED ONCE. The no-Markdown rule used to be written four different ways in two
    /// files; improve one and the others silently keep the old behaviour, which is the mechanism that let
    /// a language reach one generator and not the rest.
    ///
    /// The scan is over files that KNOW ABOUT SPOKEN LANGUAGES - which is exactly the set of files that
    /// produce speech, now and in future, since a spoken path cannot be written without one. Of those,
    /// only <c>SpeechContract.cs</c> may state the rule. A prompt elsewhere in the Gateway that tells a
    /// model not to wrap JSON in a Markdown fence is a different rule about a different output and is
    /// none of this test's business.
    ///
    /// Revert-proof: paste a "no markdown" instruction back into
    /// <c>WingmanTranslator</c> - which references a spoken language - and this goes red naming
    /// the file and the line.
    /// </summary>
    [Fact]
    public void The_no_markdown_rule_exists_in_exactly_one_place()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var (relativePath, text) in GatewaySourceFiles())
        {
            if (relativePath.EndsWith("/Speech/SpeechContract.cs", StringComparison.Ordinal)) continue;
            if (!text.Contains("SpokenLanguage", StringComparison.Ordinal)) continue;
            scanned++;
            foreach (var line in text.Split('\n'))
            {
                if (line.Contains("NO Markdown", StringComparison.Ordinal)
                    || line.Contains("NO MARKDOWN", StringComparison.Ordinal)
                    || line.Contains("no markdown", StringComparison.Ordinal))
                    offenders.Add($"{relativePath}: {line.Trim()}");
            }
        }

        // A scan that matched no files would pass forever and prove nothing. The spoken-aware files are
        // the generators, the registry, and the settings plumbing - several, always.
        Assert.True(scanned >= 3, $"The scan found only {scanned} spoken-aware files - it is not looking where it thinks it is.");
        Assert.True(offenders.Count == 0,
            "The no-Markdown rule belongs in SpeechContract.PlainSpokenProseRule and nowhere else. A second "
            + "copy is a second rule, and the two drift the moment one is improved. Found: "
            + string.Join("\n  ", offenders));
    }

    // ----------------------------------------------------------------------------------------------
    // Completeness: a FIFTH spoken path cannot be added and forgotten.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Every spoken-prompt builder in the Gateway is either registered in <see cref="SpokenPaths.All"/>
    /// or named on <see cref="SpokenPaths.NotSpokenOutput"/> with a reason. There is no silent third
    /// category.
    ///
    /// This is the test that makes the issue's bar real: "adding a fifth spoken path in future must pick
    /// up the language automatically". A new <c>BuildSomethingPrompt</c> that nobody registers fails the
    /// build; registering it forces it through the contract test above; and the contract is built from
    /// the language. There is no order of operations in which it ships in English by accident.
    ///
    /// Revert-proof: add a <c>public static string BuildFifthPrompt(...)</c> anywhere in the Gateway and
    /// this goes red until it is registered or explicitly exempted.
    /// </summary>
    [Fact]
    public void Every_prompt_builder_in_the_gateway_is_registered_or_named_as_not_spoken()
    {
        var registered = SpokenPaths.All.Concat(SpokenPaths.SpokenFieldPaths)
            .Select(p => p.Builder).ToHashSet(StringComparer.Ordinal);
        var unaccounted = new List<string>();

        foreach (var builder in AllPromptBuilders())
        {
            if (registered.Contains(builder)) continue;
            if (SpokenPaths.NotSpokenOutput.ContainsKey(builder)) continue;
            unaccounted.Add(builder);
        }

        Assert.True(unaccounted.Count == 0,
            "A prompt builder exists in the Gateway that is neither a registered spoken path nor named on "
            + "SpokenPaths.NotSpokenOutput with a reason. If it produces words a person hears, register it "
            + "so the spoken output contract binds it; if it does not, say so on the exemption list. "
            + "Unaccounted: " + string.Join("; ", unaccounted));
    }

    /// <summary>Every builder a registered spoken path names actually EXISTS in the source. Without this,
    ///  a typo in a registration would silently satisfy the completeness guard from both sides - the real
    ///  builder would look registered, and the registered name would match nothing.</summary>
    [Fact]
    public void Every_registered_and_exempted_builder_exists_in_the_source()
    {
        var declared = AllPromptBuilders();
        var claimed = SpokenPaths.All.Select(p => p.Builder)
            .Concat(SpokenPaths.SpokenFieldPaths.Select(p => p.Builder))
            .Concat(SpokenPaths.NotSpokenOutput.Keys)
            .Distinct(StringComparer.Ordinal);

        var missing = claimed.Where(c => !declared.Contains(c)).ToList();

        Assert.True(missing.Count == 0,
            "SpokenPaths names a prompt builder that does not exist in the Gateway source. A stale or "
            + "misspelled name makes the completeness guard blind. Missing: " + string.Join("; ", missing));
    }

    /// <summary>Sanity on the guard itself: it can actually SEE prompt builders. A source scan that
    ///  silently matches nothing is a test that passes forever and proves nothing.</summary>
    [Fact]
    public void The_prompt_builder_scan_finds_the_builders_we_know_exist()
    {
        var found = AllPromptBuilders();

        Assert.Contains("WingmanTranslator.BuildPrompt", found);
        Assert.Contains("WingmanTranslator.BuildDirectPrompt", found);
        Assert.Contains("WingmanTranslator.BuildDevThrottlePrompt", found);
        Assert.Contains("WingmanTranslator.BuildMenuDetectPrompt", found);
    }

    // ----------------------------------------------------------------------------------------------
    // The reverted failure cannot return: a language never picks an engine.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// NO SPOKEN LANGUAGE EVER SELECTS A SPEECH MODEL. This is the whole reason the last attempt was
    /// pulled: choosing a language switched the engine to a multilingual one that could not say the
    /// lengths this product writes - French returned silence at 155 characters and Spanish blew a
    /// sixty-second deadline at 208, against a wingman tuned to write about 500. Every wiring bug in
    /// that build could have been fixed and it would still have failed in normal use.
    ///
    /// French and Spanish are VOICES inside the engine that already serves English. So no file may both
    /// mention a spoken language and touch the text-to-speech MODEL: the two concepts never meet.
    ///
    /// Revert-proof: write <c>if (language.Code == "fr") SetTtsModel(...)</c> anywhere and this goes red.
    /// </summary>
    [Fact]
    public void No_method_derives_a_speech_model_from_a_language()
    {
        var languageTypes = new[] { typeof(SpokenLanguage).FullName!, typeof(SpokenLanguages).FullName! };
        var offenders = new List<string>();
        var sawLanguage = 0;

        using var module = ModuleDefinition.ReadModule(typeof(SpeechContract).Assembly.Location);
        foreach (var type in AllTypes(module))
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;

            var touchesLanguage = false;
            var touchesTtsModel = false;
            foreach (var instruction in method.Body.Instructions)
            {
                // TypeReference derives from MemberReference in Cecil, so it has to be matched first or
                // every type operand would be read as a member of its own declaring type.
                var operandName = instruction.Operand switch
                {
                    TypeReference declaredType => declaredType.FullName,
                    MemberReference member => member.DeclaringType?.FullName + "." + member.Name,
                    _ => null,
                };
                if (operandName is null) continue;
                if (languageTypes.Any(t => operandName.StartsWith(t, StringComparison.Ordinal))) touchesLanguage = true;
                if (operandName.Contains("TtsModel", StringComparison.Ordinal)) touchesTtsModel = true;
            }

            if (touchesLanguage) sawLanguage++;
            if (touchesLanguage && touchesTtsModel)
                offenders.Add($"{type.FullName}.{method.Name}");
        }

        // The scan has to be able to SEE the language type, or an empty result means nothing.
        Assert.True(sawLanguage >= 3,
            $"Only {sawLanguage} compiled methods reference a spoken language - the scan is not looking "
            + "where it thinks it is.");
        Assert.True(offenders.Count == 0,
            "A single method must not both read a spoken language and touch the text-to-speech MODEL. "
            + "Choosing a language switched the speech engine in the build that was reverted "
            + "(devthrottle_internal#547), and that engine could not speak real narration lengths: French "
            + "returned silence at 155 characters and Spanish blew a sixty-second deadline at 208, against "
            + "a wingman tuned to write about 500. A language is a VOICE inside the one engine, never an "
            + "engine. Offending methods: " + string.Join(", ", offenders));
    }

    /// <summary>The language type itself carries no engine. Belt to the method scan's braces: even a
    ///  language record with a <c>TtsModel</c> field would let a caller switch engines without any one
    ///  method looking suspicious.</summary>
    [Fact]
    public void A_language_carries_no_model_or_engine()
    {
        var members = typeof(SpokenLanguage).GetProperties().Select(p => p.Name)
            .Concat(typeof(SpokenLanguage).GetFields().Select(f => f.Name))
            .ToList();

        Assert.DoesNotContain(members, m => m.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Contains("Engine", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Code", "EnglishName", "NativeName" }.OrderBy(x => x), members.OrderBy(x => x));
    }

    /// <summary>
    /// NO LANGUAGE EVER SELECTS A SPEECH MODEL - AND THIS VERSION CANNOT BE WALKED AROUND (issue #1031).
    ///
    /// The method-local scan above is the original guard and it stays, because it catches the blunt form. It has
    /// two documented holes, both found by the independent inspection of Phases 1 and 2:
    ///
    ///   1. IT IS BLIND THROUGH A RESOLVER. It counts a method as touching a language only when the operand is
    ///      declared directly on SpokenLanguage/SpokenLanguages. A call to
    ///      <c>tenantSettings.SpokenLanguage(tenant)</c> is declared on the RESOLVER, so it does not count -
    ///      and that is how the language is actually read everywhere in the product.
    ///   2. IT IS ONE METHOD DEEP. Reading the language in one method and setting the model in a helper it calls
    ///      splits the offence across two methods and neither one looks suspicious.
    ///
    /// So this scan widens both: a language is touched by ANY member named for one (including through the
    /// resolver and through an utterance), and the offence is MODEL SELECTION reached DIRECTLY OR THROUGH A
    /// CALL. Selection, not reading: a synthesis call site legitimately holds both an utterance and the engine
    /// it is posting to, and the Architect confirmed there is no language-dependent model branch in the code
    /// today - what must be impossible is a language DECIDING an engine.
    ///
    /// Sabotage-tested both ways, which is the only reason to believe it: plant
    /// <c>if (language.Code == "fr") SetTtsModel(...)</c> in one method and it goes red; move the
    /// <c>SetTtsModel</c> into a helper and it still goes red, naming the pair.
    /// </summary>
    [Fact]
    public void No_language_selects_a_speech_model_even_through_a_resolver_or_a_helper()
    {
        using var module = ModuleDefinition.ReadModule(typeof(SpeechContract).Assembly.Location);
        var methods = AllTypes(module).SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();

        // Which methods CHOOSE a model: they write one, or they name the stored key a write goes to. Reading the
        // effective model is not selecting it, which is why TtsModel and TtsModelConfig.Resolve are absent here.
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            foreach (var name in OperandNames(method))
            {
                if (name.Contains("SetTtsModel", StringComparison.Ordinal)
                    || name.Contains("TtsModelConfig.Set", StringComparison.Ordinal)
                    || name.Contains("TenantSettingKeys.TtsModel", StringComparison.Ordinal))
                {
                    selectors.Add(Key(method));
                    break;
                }
            }
        }

        // Every method's direct callees, so reachability can be walked to any depth.
        var callGraph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var callees = new List<string>();
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is MethodReference called) callees.Add(called.FullName);
            callGraph[Key(method)] = callees;
        }

        // The scan must SEE model selection at all, or an empty offender list proves nothing.
        Assert.True(selectors.Count > 0,
            "The scan found no method that selects a speech model, so it is not looking where it thinks it is.");

        var offenders = new List<string>();
        var sawLanguage = 0;
        foreach (var method in methods)
        {
            var names = OperandNames(method).ToList();
            if (!names.Any(TouchesLanguage)) continue;
            sawLanguage++;

            if (selectors.Contains(Key(method)))
            {
                offenders.Add($"{Key(method)} (selects a model itself)");
                continue;
            }
            // TRANSITIVE, not one hop (audit finding C3). The first version of this walked exactly one call, and
            // the auditor defeated it by putting a single neutral helper between the language read and the model
            // selector: both guards went green while French could pick an engine. Model selection is rare, so the
            // reachable set stays small and this cannot flood with false positives.
            var reached = ReachesSelector(method, selectors, callGraph);
            if (reached is not null) offenders.Add($"{Key(method)} -> {reached} (selects a model)");
        }

        Assert.True(sawLanguage >= 3,
            $"Only {sawLanguage} compiled methods reference a spoken language through the widened match - the "
            + "scan is not looking where it thinks it is.");
        Assert.True(offenders.Count == 0,
            "A spoken language must never select the speech MODEL - not directly, not through the settings "
            + "resolver, and not through a helper one call away. Choosing a language switched the engine in the "
            + "build that was reverted (devthrottle_internal#547) and that engine could not speak real narration "
            + "lengths. A language selects a VOICE inside the one engine. Offenders: "
            + string.Join(", ", offenders));

        static string Key(MethodDefinition m) => m.FullName;

        // The first model selector reachable from this method by any number of calls, or null when none is.
        // Breadth-first with a visited set, so a cycle - and recursion is ordinary in a call graph - terminates.
        static string? ReachesSelector(MethodDefinition from, HashSet<string> selectors,
            Dictionary<string, List<string>> callGraph)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { from.FullName };
            var queue = new Queue<string>(callGraph.TryGetValue(from.FullName, out var direct) ? direct : new List<string>());
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (!seen.Add(next)) continue;
                if (selectors.Contains(next)) return next;
                if (callGraph.TryGetValue(next, out var deeper))
                    foreach (var callee in deeper) queue.Enqueue(callee);
            }
            return null;
        }

        static IEnumerable<string> OperandNames(MethodDefinition m)
        {
            foreach (var instruction in m.Body.Instructions)
            {
                var name = instruction.Operand switch
                {
                    TypeReference declaredType => declaredType.FullName,
                    MemberReference member => member.DeclaringType?.FullName + "." + member.Name,
                    _ => null,
                };
                if (name is not null) yield return name;
            }
        }

        // A language is touched by any member NAMED for one, wherever it is declared - which is what closes the
        // resolver hole. An utterance counts too: it carries a language by construction, so a method holding one
        // is holding a language whether it says the word or not.
        static bool TouchesLanguage(string operandName)
            => operandName.StartsWith(typeof(SpokenLanguage).FullName!, StringComparison.Ordinal)
               || operandName.StartsWith(typeof(SpokenLanguages).FullName!, StringComparison.Ordinal)
               || operandName.StartsWith(typeof(SpokenUtterance).FullName!, StringComparison.Ordinal)
               || operandName.EndsWith(".SpokenLanguage", StringComparison.Ordinal)
               || operandName.EndsWith(".Utterance", StringComparison.Ordinal);
    }

    /// <summary>Every type reachable in the module, nested types included.</summary>
    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        foreach (var t in WithNested(type))
            yield return t;

        static IEnumerable<TypeDefinition> WithNested(TypeDefinition type)
        {
            yield return type;
            foreach (var nested in type.NestedTypes)
            foreach (var t in WithNested(nested))
                yield return t;
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------------------

    /// <summary>Every production source file in the Gateway project, as (repo-relative path, text).</summary>
    private static IReadOnlyList<(string Path, string Text)> GatewaySourceFiles()
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

    /// <summary>Every prompt-building method declared in the Gateway, as <c>TypeName.MethodName</c>: a
    ///  static method returning a string whose name starts with "Build" and ends with "Prompt". The type
    ///  name comes from the file name, which is one class per file throughout this project.</summary>
    private static IReadOnlySet<string> AllPromptBuilders()
    {
        var builders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relativePath, text) in GatewaySourceFiles())
        {
            var typeName = Path.GetFileNameWithoutExtension(relativePath);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         text, @"static\s+string\s+(Build\w*Prompt)\s*\("))
                builders.Add($"{typeName}.{m.Groups[1].Value}");
        }
        return builders;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>A brain that records every prompt it is asked and answers with a fixed spoken string
    ///  wrapped in the shared answer markers.</summary>
    private sealed class RecordingBrain : IAgentBrain
    {
        public List<string> Prompts { get; } = new();
        public string? SessionId => "recording-brain";

        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new AskResult
            {
                Text = $"{SessionAskRunner.AnswerBeginMarker}\nvoila\n{SessionAskRunner.AnswerEndMarker}",
                ReplySeconds = 0.1,
            });
        }

        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }
}
