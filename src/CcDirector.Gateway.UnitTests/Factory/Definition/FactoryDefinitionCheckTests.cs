using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using Xunit;
using static CcDirector.Gateway.Tests.Factory.Definition.DefinitionFixture;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE WHOLE-DEFINITION CHECK (Factory Definition, phase 1; design section 7). A whole valid definition passes with
/// only its expected notices; then one test per error and per notice, each breaking exactly one thing and asserting
/// the file, the line and the message's words.
/// </summary>
public sealed class FactoryDefinitionCheckTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // The valid definition
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_WholeValidDefinition_HasNoErrorAndOnlyTheExpectedNotices()
    {
        var result = Check(Valid());

        Assert.False(result.HasErrors,
            string.Join("\n", result.Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}")));
        var notices = result.Findings.Select(f => $"{f.File}:{f.Line}: {f.Message}").ToList();
        Assert.Equal(new[]
        {
            "seats/outreach.yaml:5: No Director on SOREN_SOUTH is connected now, so it could not be confirmed.",
            "seats/outreach.yaml:8: Whether SOREN_SOUTH has the repository 'devthrottle' registered is not checked yet.",
            "seats/outreach.yaml:14: Whether SOREN_SOUTH holds 'outreach-imap-password' is not checked yet.",
            "seats/outreach.yaml:18: Whether SOREN_SOUTH holds 'outreach-smtp-password' is not checked yet.",
            "skills/mail-voice/SKILL.md:0: The factory skill 'mail-voice' is not named by any seat.",
        }, notices);
        Assert.All(result.Findings, f => Assert.Equal(DefinitionSeverity.Notice, f.Severity));
        Assert.NotNull(result.Definition);
    }

    [Fact]
    public void Check_ValidDefinition_BuildsTheModelInAFixedOrder()
    {
        var d = Check(Valid()).Definition!;

        Assert.Equal("devthrottle", d.Factory);
        Assert.Equal("DevThrottle", d.Title);
        Assert.Equal("Builds DevThrottle", d.Purpose);
        Assert.Equal("Ship the product.\n", d.GoalText);
        Assert.Equal("2026-10-10", d.GoalApprovedOn);
        Assert.Equal("boss", d.BossSeat);
        Assert.Equal(new FactoryDefinitionDefaults("SOREN_NORTH", "ClaudeCode", "claude-opus-5-5"), d.Defaults);
        Assert.Equal(new[] { "devthrottle" }, d.Repositories);
        Assert.Equal(new[] { "boss", "outreach", "writer" }, d.Seats.Select(s => s.Seat));
        Assert.Equal(new[] { "base", "outreach" }, d.RuleSets.Select(r => r.Name));
        Assert.Equal(new[] { "Bash(git status)" }, d.RuleSets[0].Allow);
        Assert.Equal(new[] { "Bash(rm:*)" }, d.RuleSets[1].Deny);
        Assert.Equal(new[] { "files/outreach_send.py" }, d.Files);
        Assert.Equal(new[] { "checks/new_mail.py" }, d.Checks);
        var skill = Assert.Single(d.Skills);
        Assert.Equal("mail-voice", skill.Name);
        Assert.Equal(new[] { "skills/mail-voice/examples.md" }, skill.Files);
        var grant = Assert.Single(d.Grants);
        Assert.Equal(new[] { "outreach" }, grant.Seats);

        var outreach = d.Seats[1];
        Assert.Equal("SOREN_SOUTH", outreach.MachineOrDefault(d.Defaults));
        Assert.Equal("ClaudeCode", outreach.AgentOrDefault(d.Defaults));
        Assert.Equal(new[] { "base", "outreach" }, outreach.Rules);
        Assert.Equal(new[] { "browsers" }, outreach.FleetSkills);
        var start = Assert.Single(outreach.Starts);
        Assert.Equal(600, start.EverySeconds);
        Assert.Equal("checks/new_mail.py", start.Check);
        Assert.True(start.PreventOverlap);

        var writer = d.Seats[2];
        Assert.Null(writer.Machine);
        Assert.Equal("SOREN_NORTH", writer.MachineOrDefault(d.Defaults));
        Assert.Equal("claude-opus-5-5", writer.ModelOrDefault(d.Defaults));

        var boss = d.Seats[0];
        Assert.Equal("0 8 * * 1-5", boss.Starts[0].Cron);
        Assert.Equal("Europe/Copenhagen", boss.Starts[0].TimeZone);
    }

    [Fact]
    public void Check_SameContentInAnyOrder_GivesAnEqualModelAndFindings()
    {
        var forward = Check(Valid());
        var reversed = Check(Valid().Reverse().ToDictionary(kv => kv.Key, kv => kv.Value));

        Assert.Equal(forward.Findings, reversed.Findings);
        Assert.Equal(forward.Definition!.Seats.Select(s => s.Seat), reversed.Definition!.Seats.Select(s => s.Seat));
        Assert.Equal(forward.Definition.Files, reversed.Definition.Files);
    }

    [Fact]
    public void Read_NeedsNoAccountData_AndParsesAValidDefinition()
    {
        var result = FactoryDefinitionCheck.Read(Valid());

        Assert.Empty(result.Findings);
        Assert.Equal("devthrottle", result.Definition!.Factory);
    }

    [Fact]
    public void Read_FormatError_ReturnsNoDefinition()
    {
        var result = FactoryDefinitionCheck.Read(Edit("seats/writer.yaml", "role: Writer", "role: Writer\ncolour: red"));

        AssertOneError(result, "seats/writer.yaml", 4, "'colour' is not a key of a seat file");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Files that do not parse
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_YamlThatDoesNotParse_IsAnErrorOnTheParsersLine()
    {
        var result = Check(Edit("seats/writer.yaml", "role: Writer", "role: Writer: Scribe"));

        var error = AssertOneError(result, "seats/writer.yaml", 3, "seats/writer.yaml is not valid YAML:");
        Assert.DoesNotContain("(Line:", error.Message);
    }

    [Fact]
    public void Check_YamlEndingInsideAnUnclosedList_IsAnErrorOnTheLineThatOpenedIt()
    {
        // YamlDotNet's scanner fails this case with no position of its own; it is still a syntax error in the file.
        var result = Check(Edit("seats/writer.yaml", "role: Writer", "role: [Writer"));

        AssertOneError(result, "seats/writer.yaml", 3, "seats/writer.yaml is not valid YAML:", "unclosed [");
    }

    [Fact]
    public void Check_JsonThatDoesNotParse_IsAnErrorOnTheParsersLine()
    {
        var result = Check(Edit("rules/base.json", "\"deny\": [\"Bash(git push:*)\"],", "\"deny\": [\"Bash(git push:*)\" ,,"));

        var error = AssertOneError(result, "rules/base.json", 4, "rules/base.json is not valid JSON:");
        Assert.DoesNotContain("LineNumber", error.Message);
    }

    [Fact]
    public void Check_JsonWithATrailingComma_IsRefused()
    {
        var result = Check(Edit("rules/base.json", "\"ask\": []", "\"ask\": [],"));

        AssertOneError(result, "rules/base.json", 6, "is not valid JSON");
    }

    [Fact]
    public void Check_JsonWithTabIndentation_Parses()
    {
        // OutreachRules is indented with tabs: good JSON that a YAML parser would refuse.
        Assert.Contains('\t', OutreachRules);
        Assert.False(Check(Valid()).HasErrors);
    }

    [Fact]
    public void Check_DuplicateYamlKey_IsAnErrorOnTheSecondKeysLine()
    {
        var result = Check(Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts.\nname: Scribe"));

        AssertOneError(result, "seats/writer.yaml", 5, "The key 'name' appears twice", "first on line 2");
    }

    [Fact]
    public void Check_DuplicateJsonKey_IsAnErrorOnTheSecondKeysLine()
    {
        var result = Check(Edit("rules/base.json", "\"ask\": []", "\"ask\": [],\n    \"allow\": []"));

        AssertOneError(result, "rules/base.json", 6, "The key 'allow' appears twice", "first on line 3");
    }

    [Fact]
    public void Check_YamlAnchor_IsRefusedOnItsLine()
    {
        var result = Check(Edit("factory.yaml", "defaults:", "defaults: &d"));

        AssertOneError(result, "factory.yaml", 8, "An anchor (&d) is not allowed", "can hide keys");
    }

    [Fact]
    public void Check_YamlAlias_IsRefusedOnItsLine()
    {
        var result = Check(Edit("seats/writer.yaml", "name: Writer\nrole: Writer", "name: &w Writer\nrole: *w"));

        // The anchor is met first, on line 2, and stops the read: one error, naming the anchor.
        AssertOneError(result, "seats/writer.yaml", 2, "An anchor (&w) is not allowed");
    }

    [Fact]
    public void Check_YamlAliasWithNoAnchorInThisFile_IsRefusedOnItsLine()
    {
        // An alias alone is still refused as an alias, before YAML can complain the anchor is unknown.
        var result = Check(Edit("seats/writer.yaml", "role: Writer", "role: *w"));

        var error = Assert.Single(result.Findings, f => f.IsError);
        Assert.Equal("seats/writer.yaml", error.File);
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public void Check_YamlTag_IsRefusedOnItsLine()
    {
        var result = Check(Edit("seats/writer.yaml", "role: Writer", "role: !!str Writer"));

        AssertOneError(result, "seats/writer.yaml", 3, "A tag", "is not allowed");
    }

    [Fact]
    public void Check_TwoYamlDocumentsInOneFile_IsRefused()
    {
        var result = Check(With("seats/writer.yaml", Writer + "\n---\nseat: writer\n"));

        AssertOneError(result, "seats/writer.yaml", 5, "more than one YAML document");
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(120_000)]
    public void Check_YamlNestedDeeperThanTheLimit_IsAFindingNotACrash(int depth)
    {
        // Review finding 1: the tree reader recursed with no limit, so a few kilobytes of brackets overflowed the
        // stack, which .NET cannot catch, and would end the Gateway process.
        var text = "seat: writer\nname: W\nrole: W\nbrief: B\nextra: " + new string('[', depth) + new string(']', depth) + "\n";

        var result = Check(With("seats/writer.yaml", text));

        AssertOneError(result, "seats/writer.yaml", 5, $"nested more than {DefinitionDocument.MaxDepth} deep");
    }

    [Fact]
    public void Check_YamlNestedToTheLimit_Parses()
    {
        // The root mapping is depth 0 and the outermost list under 'extra' depth 1, so this many lists put the
        // innermost exactly at the limit; one more is refused.
        string Nested(int lists) => "seat: writer\nname: W\nrole: W\nbrief: B\nextra: " + new string('[', lists) + new string(']', lists) + "\n";

        // Parsed, so the finding is the schema's, not the depth limit's.
        AssertOneError(Check(With("seats/writer.yaml", Nested(DefinitionDocument.MaxDepth))), "seats/writer.yaml", 5, "'extra' is not a key of a seat file");
        AssertOneError(Check(With("seats/writer.yaml", Nested(DefinitionDocument.MaxDepth + 1))), "seats/writer.yaml", 5, "nested more than");
    }

    [Fact]
    public void Check_YamlBeginningWithAByteOrderMark_IsRefusedByName()
    {
        var result = Check(With("factory.yaml", DefinitionDocument.ByteOrderMark + DefinitionFixture.Factory));

        AssertOneError(result, "factory.yaml", 1, "factory.yaml begins with a byte-order mark. Save it as UTF-8 without a byte-order mark.");
    }

    [Fact]
    public void Check_JsonBeginningWithAByteOrderMark_IsRefusedByName()
    {
        var result = Check(With("rules/base.json", DefinitionDocument.ByteOrderMark + BaseRules));

        AssertOneError(result, "rules/base.json", 1, "rules/base.json begins with a byte-order mark.");
    }

    [Fact]
    public void Check_SkillMdBeginningWithAByteOrderMark_IsRefusedByName()
    {
        var result = Check(With("skills/mail-voice/SKILL.md", DefinitionDocument.ByteOrderMark + MailVoice));

        AssertOneError(result, "skills/mail-voice/SKILL.md", 1, "skills/mail-voice/SKILL.md begins with a byte-order mark.");
    }

    [Fact]
    public void Check_NullText_IsTheCallersDefectAndThrows()
    {
        var content = Valid();
        content["files/null.txt"] = null!;

        var ex = Assert.Throws<ArgumentException>(() => Check(content));
        Assert.Contains("holds no text for 'files/null.txt'", ex.Message);
    }

    [Fact]
    public void Check_EmptySeatFile_IsRefused()
    {
        var result = Check(With("seats/writer.yaml", ""));

        AssertOneError(result, "seats/writer.yaml", 0, "seats/writer.yaml is empty");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Schema
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_UnknownKey_IsNamedWithItsFileLineAndTheKeysTheFileTakes()
    {
        var result = Check(Edit("factory.yaml", "title: DevThrottle", "title: DevThrottle\nowner: Soren"));

        AssertOneError(result, "factory.yaml", 3,
            "'owner' is not a key of factory.yaml. The keys are: factory, title, purpose, goalText, goalApprovedOn, bossSeat, defaults, repositories.");
    }

    [Fact]
    public void Check_UnknownKeyInAStart_NamesAStart()
    {
        var result = Check(Edit("seats/boss.yaml", "prompt: Morning round.", "prompt: Morning round.\n    retries: 3"));

        AssertOneError(result, "seats/boss.yaml", 10, "'retries' is not a key of a start.", "cron, timeZone, every, check");
    }

    [Fact]
    public void Check_RuleSetWithHooks_IsAnUnknownKey()
    {
        var result = Check(Edit("rules/base.json", "\"permissions\": {", "\"hooks\": {},\n  \"permissions\": {"));

        AssertOneError(result, "rules/base.json", 2, "'hooks' is not a key of a rule set. The keys are: permissions.");
    }

    [Fact]
    public void Check_MissingRequiredKey_IsAnErrorOnTheObjectsLine()
    {
        var result = Check(Edit("seats/writer.yaml", "\nbrief: Write the posts.", ""));

        AssertOneError(result, "seats/writer.yaml", 1, "The key 'brief' is required in a seat file.");
    }

    [Fact]
    public void Check_MissingRequiredKeyInAStart_IsOnTheStartsLine()
    {
        var result = Check(Edit("seats/boss.yaml", "\n    prompt: Morning round.", ""));

        AssertOneError(result, "seats/boss.yaml", 7, "The key 'prompt' is required in a start.");
    }

    [Fact]
    public void Check_WrongType_SaysWhatWasExpectedAndWhatWasWritten()
    {
        var result = Check(Edit("seats/writer.yaml", "name: Writer", "name: 12"));

        AssertOneError(result, "seats/writer.yaml", 2, "'name' must be text, not a number (12).");
    }

    [Fact]
    public void Check_TextWhereTrueOrFalseIsExpected_IsAWrongType()
    {
        var result = Check(Edit("seats/boss.yaml", "prompt: Morning round.", "prompt: Morning round.\n    preventOverlap: \"yes\""));

        AssertOneError(result, "seats/boss.yaml", 10, "'preventOverlap' must be true or false, not text ('yes').");
    }

    [Fact]
    public void Check_AListWhereAMappingIsExpected_IsAWrongType()
    {
        var result = Check(Edit("seats/outreach.yaml", "skills:\n  fleet: [browsers]", "skills: [browsers]"));

        AssertOneError(result, "seats/outreach.yaml", 19, "'skills' must be a mapping of keys, not a list.");
    }

    [Fact]
    public void Check_EmptyValue_IsAWrongType()
    {
        var result = Check(Edit("seats/writer.yaml", "role: Writer", "role:"));

        AssertOneError(result, "seats/writer.yaml", 3, "'role' must be text, not empty.");
    }

    [Fact]
    public void Check_AnAgentWeDoNotSupport_IsRefusedWithTheAgentsWeDo()
    {
        var result = Check(Edit("seats/outreach.yaml", "agent: ClaudeCode", "agent: RawCli"));

        AssertOneError(result, "seats/outreach.yaml", 6,
            "'agent' is 'RawCli', which is not one of: ClaudeCode, Pi, Codex, Gemini, OpenCode, Cursor, Grok, Copilot.");
    }

    [Fact]
    public void Check_BadPattern_QuotesTheValueAndWhatTheKeyTakes()
    {
        var result = Check(Edit("factory.yaml", "bossSeat: boss", "bossSeat: The_Boss"));

        AssertOneError(result, "factory.yaml", 7, "'bossSeat' is 'The_Boss', which is not right.", "lower-case letters and digits joined by single hyphens");
    }

    [Fact]
    public void Check_BlankBrief_IsABadValue()
    {
        var result = Check(Edit("seats/writer.yaml", "brief: Write the posts.", "brief: \"   \""));

        AssertOneError(result, "seats/writer.yaml", 4, "'brief' is '   ', which is not right.", "Must not be blank.");
    }

    [Fact]
    public void Check_PurposeTooLong_SaysTheLengthAndTheLimit()
    {
        var result = Check(Edit("factory.yaml", "purpose: Builds DevThrottle", "purpose: " + new string('x', 121)));

        AssertOneError(result, "factory.yaml", 3, "'purpose' is 121 characters; it takes at most 120.");
    }

    [Fact]
    public void Check_EmptyRule_IsTooShort()
    {
        var result = Check(Edit("rules/base.json", "\"ask\": []", "\"ask\": [\"\"]"));

        AssertOneError(result, "rules/base.json", 5, "Item 1 of 'ask' must not be empty.");
    }

    [Fact]
    public void Check_AListedTwice_IsRefusedOnTheSecondItem()
    {
        var result = Check(Edit("seats/outreach.yaml", "rules: [base, outreach]", "rules: [base, outreach, base]"));

        AssertOneError(result, "seats/outreach.yaml", 17, "'rules' lists 'base' twice (first on line 17); list it once.");
    }

    [Fact]
    public void Check_GrantWithNoSeats_NeedsAtLeastOne()
    {
        var result = Check(Edit("grants.yaml", "seats: [outreach]", "seats: []"));

        // The seat that names the grant now disagrees with it too; the schema error comes first and stops the
        // reference stage, so it is the only finding.
        AssertOneError(result, "grants.yaml", 5, "'seats' needs at least 1 item.");
    }

    [Fact]
    public void Check_IntervalWrittenWrongly_IsABadValue()
    {
        var result = Check(Edit("seats/outreach.yaml", "every: 10m", "every: 10 minutes"));

        AssertOneError(result, "seats/outreach.yaml", 12, "'every' is '10 minutes', which is not right.", "s, m or h");
    }

    [Fact]
    public void Check_CheckOutsideChecksFolder_IsABadValue()
    {
        var result = Check(Edit("seats/outreach.yaml", "check: checks/new_mail.py", "check: files/outreach_send.py"));

        AssertOneError(result, "seats/outreach.yaml", 13, "'check' is 'files/outreach_send.py', which is not right.");
    }

    [Fact]
    public void Check_SeatFileOutsideFilesFolder_IsABadValue()
    {
        var result = Check(Edit("seats/outreach.yaml", "files: [files/outreach_send.py]", "files: [checks/new_mail.py]"));

        AssertOneError(result, "seats/outreach.yaml", 21, "Item 1 of 'files' is 'checks/new_mail.py', which is not right.");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Paths and caps
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_PathNotAPlace_ListsThePlaces()
    {
        var content = Valid();
        content["notes/todo.md"] = "x";

        AssertOneError(Check(content), "notes/todo.md", 0,
            "'notes/todo.md' is not a place a definition holds. The places are: factory.yaml, seats/<seat>.yaml, grants.yaml, rules/<name>.json, files/<any path>, checks/<name>.py, skills/<name>/SKILL.md and skills/<name>/<any path>.");
    }

    [Theory]
    [InlineData("seats/writer.yml")]
    [InlineData("rules/base.yaml")]
    [InlineData("checks/new_mail.sh")]
    [InlineData("checks/sub/new_mail.py")]
    [InlineData("skills/SKILL.md")]
    [InlineData("files")]
    public void Check_PathNearlyAPlace_IsNotAPlace(string path)
    {
        var content = Valid();
        content[path] = "x";

        AssertOneError(Check(content), path, 0, "is not a place a definition holds");
    }

    [Theory]
    [InlineData("files\\tool.py", "it contains a backslash")]
    [InlineData("/files/tool.py", "it begins with a slash")]
    [InlineData("files//tool.py", "it has an empty part")]
    [InlineData("files/./tool.py", "it has a '.' part")]
    [InlineData("files/../factory.yaml", "it has a '..' part")]
    [InlineData("files/tool.py/", "it has an empty part")]
    // A path every machine can write (review of the definition store, finding 1).
    [InlineData("files/nul", "its part 'nul' is a name Windows keeps for a device")]
    [InlineData("files/CON.txt", "its part 'CON.txt' is a name Windows keeps for a device")]
    [InlineData("files/aux/tool.py", "its part 'aux' is a name Windows keeps for a device")]
    [InlineData("checks/com1.py", "its part 'com1.py' is a name Windows keeps for a device")]
    [InlineData("files/tool.", "its part 'tool.' ends in a dot or a space")]
    [InlineData("files/tool ", "its part 'tool ' ends in a dot or a space")]
    [InlineData("files/a<b.py", "it holds the character '<'")]
    [InlineData("files/a>b.py", "it holds the character '>'")]
    [InlineData("files/a:b.py", "it holds the character ':'")]
    [InlineData("files/\"a\".py", "it holds the character '\"'")]
    [InlineData("files/a|b.py", "it holds the character '|'")]
    [InlineData("files/a?.py", "it holds the character '?'")]
    [InlineData("files/a*.py", "it holds the character '*'")]
    public void Check_BadPath_SaysWhy(string path, string reason)
    {
        var content = Valid();
        content[path] = "x";

        AssertOneError(Check(content), path, 0, "is not a path a definition can hold: " + reason);
    }

    [Theory]
    [InlineData("files/Tool.py", "files/tool.py", "files/tool.py", "spells 'files/tool.py' where 'files/Tool.py' spells it 'files/Tool.py'")]
    [InlineData("files/Lib/a.py", "files/lib/b.py", "files/lib/b.py", "spells 'files/lib' where 'files/Lib/a.py' spells it 'files/Lib'")]
    [InlineData("files/lib", "files/lib/b.py", "files/lib/b.py", "'files/lib' is a file in 'files/lib' and a folder in 'files/lib/b.py'")]
    public void Check_TwoPathsNoMachineCanHoldSideBySide_IsAnErrorOnTheLaterPath(string one, string two, string named, string words)
    {
        // Review of the definition store, finding 1: Windows and macOS hold names that differ only in case as one, and
        // no machine holds a name as a file and a folder at once, so an export would write fewer files than this holds.
        var content = Valid();
        content[one] = "x";
        content[two] = "y";

        AssertOneError(Check(content), named, 0, words);
    }

    [Fact]
    public void Check_PathHoldingALineBreak_IsRefusedInOneLine()
    {
        var content = Valid();
        content["files/a\nb.txt"] = "x";

        var error = AssertOneError(Check(content), "files/a\nb.txt", 0, "it holds a control character, such as a line break");
        Assert.DoesNotContain('\n', error.Message);
    }

    [Fact]
    public void Check_PathOver200Characters_IsRefused()
    {
        var path = "files/" + new string('a', 195);
        var content = Valid();
        content[path] = "x";

        AssertOneError(Check(content), path, 0, "it is 201 characters long", "at most 200 characters");
    }

    [Fact]
    public void Check_SeatFileWithABadName_IsRefused()
    {
        var content = Without("seats/writer.yaml");
        content["seats/Writer.yaml"] = Writer;

        AssertOneError(Check(content), "seats/Writer.yaml", 0, "names the seat 'Writer'", "lower-case letters and digits joined by single hyphens");
    }

    [Fact]
    public void Check_CheckNameWithAnUnderscore_IsAllowed()
    {
        Assert.True(Valid().ContainsKey("checks/new_mail.py"));
        Assert.False(Check(Valid()).HasErrors);
    }

    [Fact]
    public void Check_FileOverItsCap_IsRefused()
    {
        var content = Valid();
        content["files/big.txt"] = new string('x', DefinitionPaths.MaxFileBytes + 1);

        AssertOneError(Check(content), "files/big.txt", 0, $"is {DefinitionPaths.MaxFileBytes + 1} bytes; a file may hold at most {DefinitionPaths.MaxFileBytes} bytes (256 KB)");
    }

    [Fact]
    public void Check_FileCapCountsUtf8Bytes_NotCharacters()
    {
        var content = Valid();
        // Each e-acute is two UTF-8 bytes: under the cap in characters, over it in bytes.
        content["files/accents.txt"] = new string('\u00e9', DefinitionPaths.MaxFileBytes / 2 + 1);

        AssertOneError(Check(content), "files/accents.txt", 0, "a file may hold at most");
    }

    [Fact]
    public void Check_VersionOverItsCap_IsRefused()
    {
        var content = Valid();
        for (var i = 0; i < 17; i++) content[$"files/part-{i:00}.txt"] = new string('x', DefinitionPaths.MaxFileBytes);

        var result = Check(content);

        AssertOneError(result, "", 0, $"a version may hold at most {DefinitionPaths.MaxVersionBytes} bytes (4 MB)");
    }

    [Fact]
    public void Check_MoreFilesThanAVersionHolds_IsRefused()
    {
        var content = Valid();
        var extra = DefinitionPaths.MaxFiles - content.Count + 1;
        for (var i = 0; i < extra; i++) content[$"files/many/{i:0000}.txt"] = "x";
        Assert.Equal(DefinitionPaths.MaxFiles + 1, content.Count);

        AssertOneError(Check(content), "", 0, $"The definition holds {DefinitionPaths.MaxFiles + 1} files; a version may hold at most {DefinitionPaths.MaxFiles}.");
    }

    [Fact]
    public void Check_ExactlyTheMostFilesAVersionHolds_IsAllowed()
    {
        var content = Valid();
        var extra = DefinitionPaths.MaxFiles - content.Count;
        for (var i = 0; i < extra; i++) content[$"files/many/{i:0000}.txt"] = "x";

        Assert.False(Check(content).HasErrors);
    }

    [Fact]
    public void Check_NoFactoryFile_IsRefused()
    {
        AssertOneError(Check(Without("factory.yaml")), "factory.yaml", 0, "The definition has no factory.yaml.");
    }

    [Fact]
    public void Check_NoSeat_IsRefused()
    {
        var content = Valid();
        foreach (var seat in content.Keys.Where(k => k.StartsWith("seats/", StringComparison.Ordinal)).ToList())
            content.Remove(seat);

        AssertOneError(Check(content), "seats/", 0, "The definition has no seat.");
    }

    [Fact]
    public void Check_SeatKeyNotTheFileName_IsAnErrorOnTheSeatKey()
    {
        var result = Check(Edit("seats/writer.yaml", "seat: writer", "seat: author"));

        AssertOneError(result, "seats/writer.yaml", 1, "This file is seats/writer.yaml but its seat key says 'author'");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Factory skills' own files
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_SkillFileWithNoSkillMd_IsRefused()
    {
        var content = Valid();
        content["skills/tone/notes.md"] = "x";

        AssertOneError(Check(content), "skills/tone/notes.md", 0, "beside the factory skill 'tone', but skills/tone/SKILL.md does not exist");
    }

    [Fact]
    public void Check_SkillMdWithoutFrontmatter_IsRefused()
    {
        var result = Check(With("skills/mail-voice/SKILL.md", "Write plainly.\n"));

        AssertOneError(result, "skills/mail-voice/SKILL.md", 1, "must begin with frontmatter");
    }

    [Fact]
    public void Check_SkillNameNotItsFolder_IsAnErrorOnTheNameLine()
    {
        var result = Check(Edit("skills/mail-voice/SKILL.md", "name: mail-voice", "name: voice"));

        AssertOneError(result, "skills/mail-voice/SKILL.md", 2, "The skill's name is 'voice' but its folder is 'mail-voice'");
    }

    [Fact]
    public void Check_SkillWithBlankDescription_IsAnErrorOnTheDescriptionLine()
    {
        var result = Check(Edit("skills/mail-voice/SKILL.md", "description: How the factory writes mail.", "description: \" \""));

        AssertOneError(result, "skills/mail-voice/SKILL.md", 3, "The skill's description must be text that is not blank.");
    }

    [Fact]
    public void Check_SkillWithNoDescription_IsRefused()
    {
        var result = Check(Edit("skills/mail-voice/SKILL.md", "description: How the factory writes mail.\n", ""));

        AssertOneError(result, "skills/mail-voice/SKILL.md", 1, "needs the key 'description'");
    }

    [Fact]
    public void Check_SkillWithOtherFrontmatterKeys_IsAllowed()
    {
        var result = Check(Edit("skills/mail-voice/SKILL.md", "description: How the factory writes mail.", "description: How the factory writes mail.\nallowed-tools: Read"));

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Check_SkillFrontmatterWithAnAnchor_IsRefusedOnTheFilesLine()
    {
        var result = Check(Edit("skills/mail-voice/SKILL.md", "description: How", "description: &x How"));

        AssertOneError(result, "skills/mail-voice/SKILL.md", 3, "An anchor (&x) is not allowed");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // References and the factory's own rules
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_FactoryIdNotTheOneSaved_IsAnErrorOnTheFactoryLine()
    {
        var result = FactoryDefinitionCheck.Check(Valid(), "tallyhand", FleetSkills, Machines);

        AssertOneError(result, "factory.yaml", 1, "factory.yaml says the factory is 'devthrottle', but this definition is being saved for 'tallyhand'");
    }

    [Fact]
    public void Check_GoalDateWithNoGoal_IsRefused()
    {
        var result = Check(Edit("factory.yaml", "goalText: |\n  Ship the product.\n", ""));

        AssertOneError(result, "factory.yaml", 4, "goalApprovedOn is only allowed with goalText");
    }

    [Fact]
    public void Check_GoalDateThatIsNotADate_IsRefused()
    {
        var result = Check(Edit("factory.yaml", "goalApprovedOn: 2026-10-10", "goalApprovedOn: 2026-02-30"));

        AssertOneError(result, "factory.yaml", 6, "goalApprovedOn is '2026-02-30', which is not a real date");
    }

    [Fact]
    public void Check_BossSeatMissing_IsAnErrorOnTheBossSeatLine()
    {
        var result = Check(Edit("factory.yaml", "bossSeat: boss", "bossSeat: chief"));

        AssertOneError(result, "factory.yaml", 7, "The boss seat 'chief' does not exist: there is no seats/chief.yaml.");
    }

    [Fact]
    public void Check_BossSeatNotRoleBoss_IsAnErrorOnTheBossSeatLine()
    {
        var result = Check(Edit("seats/boss.yaml", "role: Boss", "role: Manager"));

        AssertOneError(result, "factory.yaml", 7, "The boss seat 'boss' has the role 'Manager'; a boss seat's role must be Boss.");
    }

    [Fact]
    public void Check_SeatRepositoryNotTheFactorys_IsAnErrorOnTheRepositoryLine()
    {
        var result = Check(Edit("seats/outreach.yaml", "repository: devthrottle", "repository: website"));

        AssertOneError(result, "seats/outreach.yaml", 8, "The seat names the repository 'website', which factory.yaml does not list under repositories.");
    }

    [Fact]
    public void Check_SeatGrantThatDoesNotExist_IsAnErrorOnTheReferencingLine()
    {
        var result = Check(Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts.\ngrants: [publish-posts]"));

        AssertOneError(result, "seats/writer.yaml", 5, "The seat names the grant 'publish-posts', which grants.yaml does not hold.");
    }

    [Fact]
    public void Check_SeatGrantWithNoGrantsFile_IsADanglingReference()
    {
        var result = Check(Without("grants.yaml"));

        AssertOneError(result, "seats/outreach.yaml", 16, "The seat names the grant 'send-outreach-email', which grants.yaml does not hold.");
    }

    [Fact]
    public void Check_SeatNamesAGrantThatDoesNotListIt_IsAnErrorOnTheSeatsLine()
    {
        var result = Check(Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts.\ngrants: [send-outreach-email]"));

        AssertOneError(result, "seats/writer.yaml", 5,
            "The seat names the grant 'send-outreach-email', but that grant's seats in grants.yaml do not list 'writer'. The two lists must agree.");
    }

    [Fact]
    public void Check_GrantListsASeatThatDoesNotNameIt_IsAnErrorOnTheGrantsLine()
    {
        var result = Check(Edit("grants.yaml", "seats: [outreach]", "seats: [outreach, writer]"));

        AssertOneError(result, "grants.yaml", 5,
            "The grant 'send-outreach-email' lists the seat 'writer', but seats/writer.yaml does not name it under grants. The two lists must agree.");
    }

    [Fact]
    public void Check_GrantListsASeatThatDoesNotExist_IsAnErrorOnTheGrantsLine()
    {
        var result = Check(Edit("grants.yaml", "seats: [outreach]", "seats: [outreach, ghost]"));

        AssertOneError(result, "grants.yaml", 5, "The grant 'send-outreach-email' lists the seat 'ghost', which does not exist: there is no seats/ghost.yaml.");
    }

    [Fact]
    public void Check_TwoGrantsWithOneId_IsAnErrorOnTheSecondId()
    {
        var result = Check(With("grants.yaml", Grants + "\n  - id: send-outreach-email\n    allows: More email\n    seats: [outreach]\n"));

        AssertOneError(result, "grants.yaml", 6, "The grant id 'send-outreach-email' is used twice (first on line 2)");
    }

    [Fact]
    public void Check_SeatRuleSetThatDoesNotExist_IsAnErrorOnTheReferencingLine()
    {
        var result = Check(Edit("seats/boss.yaml", "rules: [base]", "rules: [strict]"));

        // The rule set 'base' is still named by outreach, so it is not unused.
        AssertOneError(result, "seats/boss.yaml", 10, "The seat names the rule set 'strict', but there is no rules/strict.json.");
    }

    [Fact]
    public void Check_SeatFileThatDoesNotExist_IsAnErrorOnTheReferencingLine()
    {
        var result = Check(Edit("seats/outreach.yaml", "files: [files/outreach_send.py]", "files: [files/outreach_send.py, files/template.md]"));

        AssertOneError(result, "seats/outreach.yaml", 21, "The seat names the file 'files/template.md', which is not in this definition.");
    }

    [Fact]
    public void Check_StartCheckThatDoesNotExist_IsAnErrorOnTheReferencingLine()
    {
        var result = Check(Edit("seats/outreach.yaml", "check: checks/new_mail.py", "check: checks/old_mail.py"));

        AssertOneError(result, "seats/outreach.yaml", 13, "The start names the check 'checks/old_mail.py', which is not in this definition.");
    }

    [Fact]
    public void Check_FleetSkillNotInTheStore_IsAnErrorOnTheReferencingLine()
    {
        var result = Check(Edit("seats/outreach.yaml", "fleet: [browsers]", "fleet: [browsers, sales-voice]"));

        AssertOneError(result, "seats/outreach.yaml", 20, "The seat names the fleet skill 'sales-voice', which is not in this account's skill store.");
    }

    [Fact]
    public void Check_FactorySkillAndFleetSkillWithOneName_IsRefusedOnTheFleetLine()
    {
        var content = Edit("seats/outreach.yaml", "fleet: [browsers]", "fleet: [browsers, mail-voice]");

        var result = FactoryDefinitionCheck.Check(content, FactoryId, new[] { "browsers", "mail-voice" }, Machines);

        AssertOneError(result, "seats/outreach.yaml", 20,
            "'mail-voice' is both a factory skill (skills/mail-voice/SKILL.md) and a fleet skill, so a seat would not know which one it got.");
    }

    // The two phase 0 tables (design section 9): factory skills and rules at launch, agent by agent.

    private const string CodexSkillReason =
        "Codex reads skills only from the start folder and its parents, the user's home folder and an administrator folder; " +
        "none of them is for one session alone, so a Codex seat cannot be given a factory skill.";

    private const string CodexRulesReason =
        "Codex enforces a rule given at launch only while its hook runs; when the hook is missing, fails or times out the " +
        "command runs anyway, so a Codex seat cannot carry rules.";

    // A writer seat on <agent> naming whatever extra lines follow its brief (line 5).
    private static Dictionary<string, string> WriterOn(string agent, string extra)
        => With("seats/writer.yaml", $"seat: writer\nname: Writer\nrole: Writer\nagent: {agent}\nbrief: Write the posts.\n{extra}");

    [Fact]
    public void Check_ClaudeCodeSeatWithAFactorySkillAndARuleSet_Passes()
    {
        // Outreach runs ClaudeCode and already takes two rule sets; give it the factory skill too.
        var result = Check(Edit("seats/outreach.yaml", "fleet: [browsers]", "fleet: [browsers]\n  factory: [mail-voice]"));

        Assert.False(result.HasErrors,
            string.Join("\n", result.Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}")));
        Assert.NotNull(result.Definition);
    }

    [Fact]
    public void Check_CodexSeatWithAFactorySkill_IsRefusedOnItsFactoryLine()
    {
        // Written as a block list, so the key's line and the item's line differ:
        // 6 skills:  7   factory:  8     - mail-voice
        var result = Check(WriterOn("Codex", "skills:\n  factory:\n    - mail-voice\n"));

        AssertOneError(result, "seats/writer.yaml", 7, CodexSkillReason);
    }

    [Fact]
    public void Check_CodexSeatWithARuleSet_IsRefusedOnItsRulesLine()
    {
        // Written as a block list, so the key's line and the items' lines differ: 6 rules:  7   - base  8   - outreach
        var result = Check(WriterOn("Codex", "rules:\n  - base\n  - outreach\n"));

        AssertOneError(result, "seats/writer.yaml", 6, CodexRulesReason);
    }

    [Fact]
    public void Check_SeatOnACodexFactoryDefault_IsRefusedTheSameForRulesAndSkills()
    {
        // Boss names no agent, so it runs the factory's default; its line 10 is "rules: [base]", and line 12 the
        // factory skill added here. Outreach names its own agent (ClaudeCode) and is not refused.
        var content = Edit("factory.yaml", "  agent: ClaudeCode", "  agent: Codex");
        content["seats/boss.yaml"] += "\nskills:\n  factory: [mail-voice]";

        var errors = Check(content).Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}").ToList();

        Assert.Equal(new[]
        {
            $"seats/boss.yaml:10 {CodexRulesReason}",
            $"seats/boss.yaml:12 {CodexSkillReason}",
        }, errors);
    }

    [Fact]
    public void Check_GeminiSeat_StillReadsNotShownYetForBoth()
    {
        // 6 rules: [base]  7 skills:  8   factory: [mail-voice]
        var result = Check(WriterOn("Gemini", "rules: [base]\nskills:\n  factory: [mail-voice]\n"));

        var errors = result.Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}").ToList();
        Assert.Equal(new[]
        {
            "seats/writer.yaml:6 Phase 0 has not shown that Gemini can take rules at launch, so a seat on Gemini cannot carry rules yet.",
            "seats/writer.yaml:8 Phase 0 has not shown that Gemini can load a factory skill, so a seat on Gemini cannot name one yet.",
        }, errors);
    }

    // The leading-slash path rule (phase 0 finding 4, design section 9).

    [Fact]
    public void Check_RuleWithAOneSlashPath_IsRefusedOnTheEntrysLine()
    {
        // rules/base.json line 3 is the allow list.
        var result = Check(Edit("rules/base.json", "\"allow\": [\"Bash(git status)\"]", "\"allow\": [\"Bash(git status)\", \"Read(/secrets/**)\"]"));

        AssertOneError(result, "rules/base.json", 3,
            "'Read(/secrets/**)' starts with one slash, which Claude Code reads from the folder the rules are launched from, " +
            "not the seat's repository, so it would match nothing there. Write ./secrets/** for the start folder, or " +
            "//secrets/** for an absolute path.");
    }

    [Fact]
    public void Check_RuleWithAOneSlashPath_IsRefusedOnItsOwnLineInABlockList()
    {
        var result = Check(With("rules/base.json", "{\n  \"permissions\": {\n    \"deny\": [\n      \"Bash(git push:*)\",\n      \"Edit(/etc/hosts)\"\n    ]\n  }\n}\n"));

        AssertOneError(result, "rules/base.json", 5, "'Edit(/etc/hosts)' starts with one slash", "Write ./etc/hosts for the start folder, or //etc/hosts");
    }

    [Theory]
    [InlineData("Read(//secrets/**)")]
    [InlineData("Read(./secrets/**)")]
    [InlineData("Read(~/secrets/**)")]
    [InlineData("Bash(/usr/bin/x)")]
    public void Check_RulePathsThatAreAlreadyExact_AreNotTouched(string entry)
    {
        var result = Check(Edit("rules/base.json", "\"deny\": [\"Bash(git push:*)\"]", $"\"deny\": [\"Bash(git push:*)\", \"{entry}\"]"));

        Assert.False(result.HasErrors,
            string.Join("\n", result.Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}")));
    }

    [Fact]
    public void Check_FactorySkillThatDoesNotExist_IsAnErrorOnTheReferencingLine()
    {
        var result = Check(Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts.\nskills:\n  factory: [tone]"));

        AssertOneError(result, "seats/writer.yaml", 6, "The seat names the factory skill 'tone', but there is no skills/tone/SKILL.md.");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Start rules
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_StartWithBothCronAndEvery_IsRefusedOnTheStartsLine()
    {
        var result = Check(Edit("seats/outreach.yaml", "  - every: 10m", "  - every: 10m\n    cron: \"0 8 * * *\"\n    timeZone: UTC"));

        AssertOneError(result, "seats/outreach.yaml", 12, "A start has both cron and every; give exactly one.");
    }

    [Fact]
    public void Check_StartWithNeitherCronNorEvery_IsRefusedOnTheStartsLine()
    {
        var result = Check(Edit("seats/boss.yaml", "  - cron: \"0 8 * * 1-5\"\n    timeZone: Europe/Copenhagen\n    prompt", "  - prompt"));

        AssertOneError(result, "seats/boss.yaml", 7, "A start has neither cron nor every; give exactly one.");
    }

    [Fact]
    public void Check_IntervalStartWithNoCheck_IsRefusedOnTheEveryLine()
    {
        var result = Check(Edit("seats/outreach.yaml", "    check: checks/new_mail.py\n    secrets: [outreach-imap-password]\n", ""));

        // The check is now unused too, which is a notice, not an error.
        AssertOneError(result, "seats/outreach.yaml", 12, "A start with every must have a check");
    }

    [Fact]
    public void Check_StartWithACheckAndPreventOverlapFalse_IsRefusedOnThatLine()
    {
        var result = Check(Edit("seats/outreach.yaml", "    prompt: \"New mail", "    preventOverlap: false\n    prompt: \"New mail"));

        AssertOneError(result, "seats/outreach.yaml", 15, "A start with a check always prevents overlap, so preventOverlap cannot be false here.");
    }

    [Theory]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void Check_StartWithACheckAndPreventOverlapFalseInCapitals_IsRefused(string written)
    {
        // Review finding 2: YAML reads False and FALSE as false, so the check must too.
        var result = Check(Edit("seats/outreach.yaml", "    prompt: \"New mail", $"    preventOverlap: {written}\n    prompt: \"New mail"));

        AssertOneError(result, "seats/outreach.yaml", 15, "preventOverlap cannot be false here");
    }

    [Fact]
    public void Check_PreventOverlapFalseInCapitals_IsStoredAsFalse()
    {
        var result = Check(Edit("seats/boss.yaml", "prompt: Morning round.", "prompt: Morning round.\n    preventOverlap: False"));

        Assert.False(result.HasErrors);
        Assert.False(result.Definition!.Seats[0].Starts[0].PreventOverlap);
    }

    [Fact]
    public void Check_HugeInterval_SaysItsTrueLengthInSeconds()
    {
        // Review finding 4: 600000h overflowed an int and was reported as a negative number of seconds.
        var result = Check(Edit("seats/outreach.yaml", "every: 10m", "every: 600000h"));

        AssertOneError(result, "seats/outreach.yaml", 12, "An interval of 600000h is 2160000000 seconds;");
    }

    [Fact]
    public void Check_StartWithNoCheckAndPreventOverlapFalse_IsAllowed()
    {
        var result = Check(Edit("seats/boss.yaml", "prompt: Morning round.", "prompt: Morning round.\n    preventOverlap: false"));

        Assert.False(result.HasErrors);
        Assert.False(result.Definition!.Seats[0].Starts[0].PreventOverlap);
    }

    [Fact]
    public void Check_StartSecretsWithNoCheck_IsRefusedOnTheSecretsLine()
    {
        var result = Check(Edit("seats/boss.yaml", "prompt: Morning round.", "prompt: Morning round.\n    secrets: [boss-token]"));

        AssertOneError(result, "seats/boss.yaml", 10, "secrets on a start go to its check, and this start has none.");
    }

    [Fact]
    public void Check_CronThatSchedulesWouldRefuse_IsRefusedOnTheCronLine()
    {
        var result = Check(Edit("seats/boss.yaml", "cron: \"0 8 * * 1-5\"", "cron: \"every morning\""));

        AssertOneError(result, "seats/boss.yaml", 7, "'every morning' is not a cron expression a schedule takes");
    }

    [Fact]
    public void Check_CronWithSecondsField_IsRefusedAsSchedulesDo()
    {
        // Schedules take the standard five-field form; a sixth (seconds) field is refused there, so here too.
        Assert.False(CcDirector.Gateway.CronSchedule.IsCronExpression("0 0 8 * * 1-5"));
        var result = Check(Edit("seats/boss.yaml", "cron: \"0 8 * * 1-5\"", "cron: \"0 0 8 * * 1-5\""));

        AssertOneError(result, "seats/boss.yaml", 7, "is not a cron expression a schedule takes");
    }

    [Fact]
    public void Check_CronWithNoTimeZone_IsRefusedOnTheCronLine()
    {
        var result = Check(Edit("seats/boss.yaml", "\n    timeZone: Europe/Copenhagen", ""));

        AssertOneError(result, "seats/boss.yaml", 7, "A start with cron needs timeZone");
    }

    [Fact]
    public void Check_TimeZoneWithEvery_IsRefusedOnTheTimeZoneLine()
    {
        var result = Check(Edit("seats/outreach.yaml", "  - every: 10m", "  - every: 10m\n    timeZone: UTC"));

        AssertOneError(result, "seats/outreach.yaml", 13, "timeZone is only allowed with cron.");
    }

    [Fact]
    public void Check_UnknownTimeZone_IsRefusedOnTheTimeZoneLine()
    {
        Assert.False(CcDirector.Gateway.CronSchedule.IsKnownTimeZone("Mars/Olympus"));
        var result = Check(Edit("seats/boss.yaml", "timeZone: Europe/Copenhagen", "timeZone: Mars/Olympus"));

        AssertOneError(result, "seats/boss.yaml", 8, "'Mars/Olympus' is not a time zone a schedule takes.");
    }

    [Fact]
    public void Check_IntervalShorterThanTheTriggersMinimum_IsRefused()
    {
        var tooShort = $"{TriggerDefinition.MinimumIntervalSeconds - 1}s";
        var result = Check(Edit("seats/outreach.yaml", "every: 10m", "every: " + tooShort));

        AssertOneError(result, "seats/outreach.yaml", 12,
            $"An interval of {tooShort} is {TriggerDefinition.MinimumIntervalSeconds - 1} seconds; it must be between {TriggerDefinition.MinimumIntervalSeconds} and {TriggerDefinition.MaximumIntervalSeconds} seconds.");
    }

    [Fact]
    public void Check_IntervalLongerThanTheTriggersMaximum_IsRefused()
    {
        var tooLong = $"{TriggerDefinition.MaximumIntervalSeconds / 3600 + 1}h";
        var result = Check(Edit("seats/outreach.yaml", "every: 10m", "every: " + tooLong));

        AssertOneError(result, "seats/outreach.yaml", 12, $"An interval of {tooLong} is", $"between {TriggerDefinition.MinimumIntervalSeconds} and {TriggerDefinition.MaximumIntervalSeconds} seconds");
    }

    [Fact]
    public void Check_IntervalAtTheTriggersBounds_IsAllowed()
    {
        Assert.False(Check(Edit("seats/outreach.yaml", "every: 10m", $"every: {TriggerDefinition.MinimumIntervalSeconds}s")).HasErrors);
        Assert.False(Check(Edit("seats/outreach.yaml", "every: 10m", $"every: {TriggerDefinition.MaximumIntervalSeconds}s")).HasErrors);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Notices
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_SeatSecret_IsANoticeNamingTheSeatsMachine()
    {
        AssertNotice(Check(Valid()), "seats/outreach.yaml", 18, "Whether SOREN_SOUTH holds 'outreach-smtp-password' is not checked yet.");
    }

    [Fact]
    public void Check_StartSecret_IsANoticeNamingTheSeatsMachine()
    {
        AssertNotice(Check(Valid()), "seats/outreach.yaml", 14, "Whether SOREN_SOUTH holds 'outreach-imap-password' is not checked yet.");
    }

    [Fact]
    public void Check_SecretOnASeatWithNoMachine_NamesTheDefaultMachine()
    {
        var result = Check(Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts.\nsecrets: [blog-token]"));

        Assert.False(result.HasErrors);
        AssertNotice(result, "seats/writer.yaml", 5, "Whether SOREN_NORTH holds 'blog-token' is not checked yet.");
    }

    [Fact]
    public void Check_SeatMachineNotConnected_IsANotice()
    {
        AssertNotice(Check(Valid()), "seats/outreach.yaml", 5, "No Director on SOREN_SOUTH is connected now, so it could not be confirmed.");
    }

    [Fact]
    public void Check_DefaultMachineNotConnected_IsANoticeOnTheDefaultsLine()
    {
        var result = FactoryDefinitionCheck.Check(Valid(), FactoryId, FleetSkills, Array.Empty<string>());

        Assert.False(result.HasErrors);
        AssertNotice(result, "factory.yaml", 9, "No Director on SOREN_NORTH is connected now");
    }

    [Fact]
    public void Check_ConnectedMachine_IsMatchedWithoutRegardToCase()
    {
        var result = FactoryDefinitionCheck.Check(Valid(), FactoryId, FleetSkills, new[] { "soren_north", "Soren_South" });

        Assert.DoesNotContain(result.Findings, f => f.Message.StartsWith("No Director on", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_SeatRepository_IsANotice()
    {
        AssertNotice(Check(Valid()), "seats/outreach.yaml", 8, "Whether SOREN_SOUTH has the repository 'devthrottle' registered is not checked yet.");
    }

    [Fact]
    public void Check_UnusedRuleSet_IsANotice()
    {
        var result = Check(Edit("seats/outreach.yaml", "rules: [base, outreach]", "rules: [base]"));

        Assert.False(result.HasErrors);
        AssertNotice(result, "rules/outreach.json", 0, "The rule set 'outreach' is not named by any seat.");
    }

    [Fact]
    public void Check_UnusedFile_IsANotice()
    {
        var content = Valid();
        content["files/template.md"] = "Hello";

        var result = Check(content);

        Assert.False(result.HasErrors);
        AssertNotice(result, "files/template.md", 0, "'files/template.md' is not named by any seat.");
    }

    [Fact]
    public void Check_UnusedCheck_IsANotice()
    {
        var content = Valid();
        content["checks/spare.py"] = "print('{\"count\": 0}')";

        var result = Check(content);

        Assert.False(result.HasErrors);
        AssertNotice(result, "checks/spare.py", 0, "The check 'checks/spare.py' is not named by any start.");
    }

    [Fact]
    public void Check_UnusedFactorySkill_IsANotice()
    {
        AssertNotice(Check(Valid()), "skills/mail-voice/SKILL.md", 0, "The factory skill 'mail-voice' is not named by any seat.");
    }

    [Fact]
    public void Check_Notices_NeverRefuse()
    {
        var result = FactoryDefinitionCheck.Check(Valid(), FactoryId, FleetSkills, Array.Empty<string>());

        Assert.NotEmpty(result.Findings);
        Assert.False(result.HasErrors);
        Assert.NotNull(result.Definition);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The cap on findings
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_MoreThan200Findings_Returns200AndSaysHowManyWereNotShown()
    {
        var content = Valid();
        for (var i = 0; i < 250; i++) content[$"notes/{i:000}.md"] = "x";

        var result = Check(content);

        Assert.Equal(FactoryDefinitionCheck.MaxFindings + 1, result.Findings.Count);
        var last = result.Findings[^1];
        Assert.Equal(DefinitionSeverity.Error, last.Severity);
        Assert.Equal("", last.File);
        Assert.Equal(0, last.Line);
        Assert.StartsWith("50 more findings were not shown; the check returns at most 200 at a time.", last.Message);
    }

    [Fact]
    public void Check_ErrorsComeBeforeNotices()
    {
        var content = Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts.\ngrants: [publish-posts]");

        var result = Check(content);

        var firstNotice = result.Findings.ToList().FindIndex(f => !f.IsError);
        var lastError = result.Findings.ToList().FindLastIndex(f => f.IsError);
        Assert.True(lastError < firstNotice);
    }
}
