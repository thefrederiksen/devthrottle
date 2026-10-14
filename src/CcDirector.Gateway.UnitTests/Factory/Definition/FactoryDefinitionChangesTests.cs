using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using Xunit;
using static CcDirector.Gateway.Tests.Factory.Definition.DefinitionFixture;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// WHAT CHANGES between two versions of a definition (Factory Definition, phase 1; mission goal 3). Each test makes one
/// kind of change to the fixture's valid definition and asserts the exact sentences the command line and the Cockpit
/// will show verbatim.
/// </summary>
public sealed class FactoryDefinitionChangesTests
{
    private static FactoryDefinitionChangesDto Changes(Dictionary<string, string> after)
        => FactoryDefinitionChanges.Compare(Valid(), after);

    private static Dictionary<string, string> Edit(Dictionary<string, string> content, string path, string oldText, string newText)
    {
        Assert.Contains(oldText, content[path]);
        content[path] = content[path].Replace(oldText, newText, StringComparison.Ordinal);
        return content;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Nothing, and everything
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_EqualVersions_SaysNothingChanges()
    {
        var dto = Changes(Valid());

        Assert.True(dto.NothingChanges);
        Assert.False(dto.FirstPublish);
        Assert.Equal("Nothing changes.", dto.Summary);
        Assert.Empty(dto.Factory);
        Assert.Empty(dto.Seats);
        Assert.Empty(dto.Starts);
        Assert.Empty(dto.Rules);
        Assert.Empty(dto.Grants);
        Assert.Empty(dto.Files);
    }

    [Fact]
    public void Compare_NothingPublished_ListsEverythingAsAdded()
    {
        var dto = FactoryDefinitionChanges.Compare(null, Valid());

        Assert.True(dto.FirstPublish);
        Assert.False(dto.NothingChanges);
        Assert.Equal(
            "First publish: 10 factory settings changed, 3 seats added, 2 starts created, rules tightened on base, outreach " +
            "and boss, rules loosened on base, 1 grant added, 1 file added, 1 check added, 2 skill files added.",
            dto.Summary);
        Assert.Equal(new[]
        {
            "factory devthrottle", "title DevThrottle", "purpose Builds DevThrottle", "goal text set: 1 line",
            "goalApprovedOn 2026-10-10", "bossSeat boss", "defaults.machine SOREN_NORTH", "defaults.agent ClaudeCode",
            "defaults.model claude-opus-5-5", "repository devthrottle added",
        }, dto.Factory.Select(f => f.Sentence).Take(10));
        Assert.Equal(new[] { "Seat Boss (boss) added.", "Seat Outreach (outreach) added.", "Seat Writer (writer) added." },
            dto.Seats.Select(s => s.Sentence));
        Assert.All(dto.Seats, s => Assert.Equal("added", s.Change));
        Assert.Equal(new[]
        {
            "role Sales", "line Find and sell", "machine SOREN_SOUTH", "agent Codex", "model gpt-6-luna",
            "repository devthrottle", "grant send-outreach-email added", "rule set base added", "rule set outreach added",
            "secret outreach-smtp-password added", "fleet skill browsers added", "file files/outreach_send.py added",
            "brief: 1 line",
        }, dto.Seats[1].Details);
        Assert.Equal(new[]
        {
            "role Writer", "machine SOREN_NORTH (the factory's default)", "agent ClaudeCode (the factory's default)",
            "model claude-opus-5-5 (the factory's default)", "brief: 1 line",
        }, dto.Seats[2].Details);
        Assert.Equal(new[]
        {
            "Boss: start created, Weekdays 08:00 Europe/Copenhagen.",
            "Outreach: start created, Every 10 minutes.",
        }, dto.Starts.Select(s => s.Sentence));
        Assert.Equal(new[] { "prompt \"Morning round.\"" }, dto.Starts[0].Details);
        Assert.Equal(new[]
        {
            "check checks/new_mail.py", "secret outreach-imap-password added", "prompt \"New mail: {count}. {context}\"",
        }, dto.Starts[1].Details);
        Assert.Equal(new[] { "Grant send-outreach-email added." }, dto.Grants.Select(g => g.Sentence));
        Assert.Equal(new[] { "allows Send email to prospects", "limits At most 20 a day", "seat outreach added" }, dto.Grants[0].Details);
        Assert.Equal(new[]
        {
            "Check checks/new_mail.py added: 1 line.",
            "File files/outreach_send.py added: 1 line.",
            "Skill file skills/mail-voice/SKILL.md added: 5 lines.",
            "Skill file skills/mail-voice/examples.md added: 1 line.",
        }, dto.Files.Select(f => f.Sentence));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Seats
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_SeatFieldsChange_NamesEachFieldInWords()
    {
        var after = Valid();
        Edit(after, "seats/outreach.yaml", "machine: SOREN_SOUTH", "machine: devlinux");
        Edit(after, "seats/outreach.yaml", "agent: Codex", "agent: ClaudeCode");
        Edit(after, "seats/outreach.yaml", "line: Find and sell", "line: Night shift");

        var dto = Changes(after);

        Assert.Equal("1 seat changed.", dto.Summary);
        var seat = Assert.Single(dto.Seats);
        Assert.Equal("outreach", seat.Seat);
        Assert.Equal("changed", seat.Change);
        Assert.Equal("Seat Outreach (outreach) changed.", seat.Sentence);
        Assert.Equal(new[] { "line Find and sell -> Night shift", "machine SOREN_SOUTH -> devlinux", "agent Codex -> ClaudeCode" }, seat.Details);
        Assert.Null(seat.BriefDiff);
    }

    [Fact]
    public void Compare_SeatRenamed_IsAChangeToThatSeatNotARemovalAndAnAddition()
    {
        var dto = Changes(DefinitionFixture.Edit("seats/outreach.yaml", "name: Outreach", "name: Sales Desk"));

        Assert.Equal("1 seat changed.", dto.Summary);
        var seat = Assert.Single(dto.Seats);
        Assert.Equal("Seat Sales Desk (outreach) changed.", seat.Sentence);
        Assert.Equal(new[] { "name Outreach -> Sales Desk" }, seat.Details);
        Assert.Empty(dto.Starts);
    }

    [Fact]
    public void Compare_SeatAddedAndRemoved_ListsBothAndTheirStarts()
    {
        var after = Valid();
        after.Remove("seats/outreach.yaml");
        after["seats/editor.yaml"] = "seat: editor\nname: Editor\nrole: Writer\nbrief: Edit the posts.\nstarts:\n  - cron: \"30 6 * * *\"\n    timeZone: UTC\n    prompt: Edit.\n";
        after["grants.yaml"] = "grants: []\n";

        var dto = Changes(after);

        Assert.Equal(new[] { "Seat Editor (editor) added.", "Seat Outreach (outreach) removed." }, dto.Seats.Select(s => s.Sentence));
        Assert.Equal(new[] { "role Writer", "machine SOREN_NORTH (the factory's default)", "agent ClaudeCode (the factory's default)", "model claude-opus-5-5 (the factory's default)", "brief: 1 line" },
            dto.Seats[0].Details);
        Assert.Empty(dto.Seats[1].Details);
        Assert.Equal(new[] { "Editor: start created, Daily 06:30 UTC.", "Outreach: start switched off, Every 10 minutes." },
            dto.Starts.Select(s => s.Sentence));
        Assert.Equal(new[] { "created", "switched off" }, dto.Starts.Select(s => s.Change));
        Assert.Equal(new[] { "Grant send-outreach-email removed." }, dto.Grants.Select(g => g.Sentence));
        Assert.Equal("1 seat added, 1 seat removed, 1 start created, 1 start switched off, 1 grant removed.", dto.Summary);
    }

    [Fact]
    public void Compare_BriefChanges_GivesTheLineDifferenceWithContext()
    {
        var after = DefinitionFixture.Edit("seats/boss.yaml", "  Run the factory.\n",
            "  Run the factory.\n  Read the ledger first.\n  Then the problems.\n");
        Edit(after, "seats/boss.yaml", "  Run the factory.\n", "  Run the whole factory.\n");

        var dto = Changes(after);

        var seat = Assert.Single(dto.Seats);
        Assert.Equal(new[] { "brief changed: 3 lines added, 1 removed" }, seat.Details);
        var diff = seat.BriefDiff!;
        Assert.Equal(3, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal(new[] { "-Run the factory.", "+Run the whole factory.", "+Read the ledger first.", "+Then the problems." },
            diff.Lines.Select(l => l.Marker + l.Text));
        Assert.Equal(new int?[] { 1, null, null, null }, diff.Lines.Select(l => l.OldLine));
        Assert.Equal(new int?[] { null, 1, 2, 3 }, diff.Lines.Select(l => l.NewLine));
        Assert.Null(diff.Note);
    }

    [Fact]
    public void Compare_DefaultMachineChanges_ReportsTheFactoryFieldAndEverySeatThatRunsOnIt()
    {
        var dto = Changes(DefinitionFixture.Edit("factory.yaml", "  machine: SOREN_NORTH", "  machine: devlinux"));

        Assert.Equal(new[] { "defaults.machine SOREN_NORTH -> devlinux" }, dto.Factory.Select(f => f.Sentence));
        // Outreach names its own machine, so it does not move.
        Assert.Equal(new[] { "boss", "writer" }, dto.Seats.Select(s => s.Seat));
        Assert.Equal(new[] { "machine SOREN_NORTH (the factory's default) -> devlinux (the factory's default)" }, dto.Seats[0].Details);
        Assert.Equal("1 factory setting changed, 2 seats changed.", dto.Summary);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Starts
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_StartTimeChanges_IsMovedInWords()
    {
        var dto = Changes(DefinitionFixture.Edit("seats/boss.yaml", "\"0 8 * * 1-5\"", "\"0 9 * * 1-5\""));

        var start = Assert.Single(dto.Starts);
        Assert.Equal("moved", start.Change);
        Assert.Equal("Boss: start moved, Weekdays 08:00 Europe/Copenhagen -> Weekdays 09:00 Europe/Copenhagen.", start.Sentence);
        Assert.Empty(start.Details);
        Assert.Empty(dto.Seats);
        Assert.Equal("1 start moved.", dto.Summary);
    }

    [Fact]
    public void Compare_StartTimeZoneChanges_IsMoved()
    {
        var dto = Changes(DefinitionFixture.Edit("seats/boss.yaml", "timeZone: Europe/Copenhagen", "timeZone: America/Toronto"));

        Assert.Equal("Boss: start moved, Weekdays 08:00 Europe/Copenhagen -> Weekdays 08:00 America/Toronto.", Assert.Single(dto.Starts).Sentence);
    }

    [Fact]
    public void Compare_StartIntervalChanges_IsMoved()
    {
        var dto = Changes(DefinitionFixture.Edit("seats/outreach.yaml", "every: 10m", "every: 1h"));

        Assert.Equal("Outreach: start moved, Every 10 minutes -> Every hour.", Assert.Single(dto.Starts).Sentence);
    }

    [Fact]
    public void Compare_StartPromptAndCheckChange_IsChangedWithEachPartInWords()
    {
        var after = DefinitionFixture.Edit("seats/outreach.yaml", "check: checks/new_mail.py", "check: checks/any_mail.py");
        after["checks/any_mail.py"] = after["checks/new_mail.py"];
        after.Remove("checks/new_mail.py");
        Edit(after, "seats/outreach.yaml", "prompt: \"New mail: {count}. {context}\"", "prompt: \"Mail: {count}.\"");

        var dto = Changes(after);

        var start = Assert.Single(dto.Starts);
        Assert.Equal("changed", start.Change);
        Assert.Equal("Outreach: start Every 10 minutes changed.", start.Sentence);
        Assert.Equal(new[] { "check checks/new_mail.py -> checks/any_mail.py", "prompt \"New mail: {count}. {context}\" -> \"Mail: {count}.\"" }, start.Details);
        Assert.Equal(new[] { "Check checks/any_mail.py added: 1 line.", "Check checks/new_mail.py removed: 1 line." }, dto.Files.Select(f => f.Sentence));
    }

    [Fact]
    public void Compare_StartAddedAndRemoved_IsCreatedAndSwitchedOff()
    {
        var added = Changes(DefinitionFixture.Edit("seats/boss.yaml", "    prompt: Morning round.\n",
            "    prompt: Morning round.\n  - cron: \"0 18 * * *\"\n    timeZone: Europe/Copenhagen\n    prompt: Evening round.\n"));
        Assert.Equal("Boss: start created, Daily 18:00 Europe/Copenhagen.", Assert.Single(added.Starts).Sentence);
        Assert.Equal(new[] { "prompt \"Evening round.\"" }, added.Starts[0].Details);
        Assert.Equal("1 start created.", added.Summary);

        var removed = Changes(DefinitionFixture.Edit("seats/boss.yaml",
            "starts:\n  - cron: \"0 8 * * 1-5\"\n    timeZone: Europe/Copenhagen\n    prompt: Morning round.\n", ""));
        Assert.Equal("Boss: start switched off, Weekdays 08:00 Europe/Copenhagen.", Assert.Single(removed.Starts).Sentence);
        Assert.Equal("1 start switched off.", removed.Summary);
    }

    [Fact]
    public void Compare_StartsReordered_ChangesNothingTheyDefine()
    {
        var two = "starts:\n  - cron: \"0 8 * * 1-5\"\n    timeZone: Europe/Copenhagen\n    prompt: Morning round.\n" +
                  "  - cron: \"0 18 * * *\"\n    timeZone: Europe/Copenhagen\n    prompt: Evening round.\n";
        var swapped = "starts:\n  - cron: \"0 18 * * *\"\n    timeZone: Europe/Copenhagen\n    prompt: Evening round.\n" +
                      "  - cron: \"0 8 * * 1-5\"\n    timeZone: Europe/Copenhagen\n    prompt: Morning round.\n";
        var before = DefinitionFixture.Edit("seats/boss.yaml", "starts:\n  - cron: \"0 8 * * 1-5\"\n    timeZone: Europe/Copenhagen\n    prompt: Morning round.\n", two);
        var after = DefinitionFixture.Edit("seats/boss.yaml", "starts:\n  - cron: \"0 8 * * 1-5\"\n    timeZone: Europe/Copenhagen\n    prompt: Morning round.\n", swapped);

        var dto = FactoryDefinitionChanges.Compare(before, after);

        Assert.Empty(dto.Starts);
        Assert.Empty(dto.Seats);
        var file = Assert.Single(dto.Files);
        Assert.Equal("definition", file.Kind);
        Assert.Equal("seats/boss.yaml: its text changes but nothing it defines does (layout, order or comments only).", file.Sentence);
        Assert.Equal("Layout only in 1 file.", dto.Summary);
    }

    [Fact]
    public void Compare_StartPreventOverlapAndSecretsChange_NamesEach()
    {
        var after = DefinitionFixture.Edit("seats/boss.yaml", "    prompt: Morning round.\n", "    prompt: Morning round.\n    preventOverlap: false\n");

        var dto = Changes(after);

        var start = Assert.Single(dto.Starts);
        Assert.Equal("Boss: start Weekdays 08:00 Europe/Copenhagen changed.", start.Sentence);
        Assert.Equal(new[] { "prevent overlap on -> off" }, start.Details);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rules
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_DenyAdded_IsTightened()
    {
        var dto = Changes(DefinitionFixture.Edit("rules/base.json", "\"deny\": [\"Bash(git push:*)\"]", "\"deny\": [\"Bash(git push:*)\", \"Bash(curl:*)\"]"));

        var rule = Assert.Single(dto.Rules);
        Assert.Equal("base", rule.RuleSet);
        Assert.Null(rule.Seat);
        Assert.Equal("tightened", rule.Direction);
        Assert.Equal("Rule set base tightened: deny Bash(curl:*) added.", rule.Sentence);
        Assert.Equal("Rules tightened on base.", dto.Summary);
    }

    [Fact]
    public void Compare_AskAdded_IsTightened()
    {
        var dto = Changes(DefinitionFixture.Edit("rules/base.json", "\"ask\": []", "\"ask\": [\"Bash(npm publish:*)\"]"));

        Assert.Equal("Rule set base tightened: ask Bash(npm publish:*) added.", Assert.Single(dto.Rules).Sentence);
    }

    [Fact]
    public void Compare_DenyRemoved_IsLoosened()
    {
        var dto = Changes(DefinitionFixture.Edit("rules/base.json", "\"deny\": [\"Bash(git push:*)\"]", "\"deny\": []"));

        var rule = Assert.Single(dto.Rules);
        Assert.Equal("loosened", rule.Direction);
        Assert.Equal("Rule set base loosened: deny Bash(git push:*) removed.", rule.Sentence);
        Assert.Equal("Rules loosened on base.", dto.Summary);
    }

    [Fact]
    public void Compare_AllowAdded_IsLoosened()
    {
        var dto = Changes(DefinitionFixture.Edit("rules/base.json", "\"allow\": [\"Bash(git status)\"]", "\"allow\": [\"Bash(git status)\", \"Bash(git log)\"]"));

        Assert.Equal("Rule set base loosened: allow Bash(git log) added.", Assert.Single(dto.Rules).Sentence);
    }

    [Fact]
    public void Compare_AllowRemoved_IsTightened()
    {
        var dto = Changes(DefinitionFixture.Edit("rules/base.json", "\"allow\": [\"Bash(git status)\"]", "\"allow\": []"));

        var rule = Assert.Single(dto.Rules);
        Assert.Equal("tightened", rule.Direction);
        Assert.Equal("Rule set base tightened: allow Bash(git status) removed.", rule.Sentence);
    }

    [Fact]
    public void Compare_DenyAddedAndAllowAdded_ReportsBothDirections()
    {
        var after = DefinitionFixture.Edit("rules/base.json", "\"deny\": [\"Bash(git push:*)\"]", "\"deny\": [\"Bash(git push:*)\", \"Bash(curl:*)\"]");
        Edit(after, "rules/base.json", "\"allow\": [\"Bash(git status)\"]", "\"allow\": [\"Bash(git status)\", \"Bash(git log)\"]");

        var dto = Changes(after);

        Assert.Equal(new[] { "Rule set base tightened: deny Bash(curl:*) added.", "Rule set base loosened: allow Bash(git log) added." },
            dto.Rules.Select(r => r.Sentence));
        Assert.Equal("Rules tightened on base, rules loosened on base.", dto.Summary);
    }

    [Fact]
    public void Compare_SeatTakesARuleSet_IsTightened()
    {
        var dto = Changes(DefinitionFixture.Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts.\nrules: [outreach]"));

        var rule = Assert.Single(dto.Rules);
        Assert.Equal("writer", rule.Seat);
        Assert.Null(rule.RuleSet);
        Assert.Equal("tightened", rule.Direction);
        Assert.Equal("Seat Writer (writer) tightened: now takes rule set outreach.", rule.Sentence);
        Assert.Equal(new[] { "rule set outreach added" }, Assert.Single(dto.Seats).Details);
        Assert.Equal("1 seat changed, rules tightened on writer.", dto.Summary);
    }

    [Fact]
    public void Compare_SeatDropsARuleSet_IsLoosened()
    {
        var dto = Changes(DefinitionFixture.Edit("seats/outreach.yaml", "rules: [base, outreach]", "rules: [base]"));

        var rule = Assert.Single(dto.Rules);
        Assert.Equal("loosened", rule.Direction);
        Assert.Equal("Seat Outreach (outreach) loosened: no longer takes rule set outreach.", rule.Sentence);
    }

    [Fact]
    public void Compare_RuleSetReordered_ChangesNothingItDefines()
    {
        var before = DefinitionFixture.Edit("rules/base.json", "\"allow\": [\"Bash(git status)\"]", "\"allow\": [\"Bash(git status)\", \"Bash(git log)\"]");
        var after = DefinitionFixture.Edit("rules/base.json", "\"allow\": [\"Bash(git status)\"]", "\"allow\": [\"Bash(git log)\", \"Bash(git status)\"]");

        var dto = FactoryDefinitionChanges.Compare(before, after);

        Assert.Empty(dto.Rules);
        Assert.Equal("rules/base.json", Assert.Single(dto.Files).Path);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Grants
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_GrantChanges_NamesAllowsLimitsAndSeats()
    {
        var after = DefinitionFixture.Edit("grants.yaml", "allows: Send email to prospects", "allows: Send email to customers");
        Edit(after, "grants.yaml", "limits: At most 20 a day", "limits: At most 50 a day");

        var dto = Changes(after);

        var grant = Assert.Single(dto.Grants);
        Assert.Equal("changed", grant.Change);
        Assert.Equal("Grant send-outreach-email changed.", grant.Sentence);
        Assert.Equal(new[] { "allows Send email to prospects -> Send email to customers", "limits At most 20 a day -> At most 50 a day" }, grant.Details);
        Assert.Equal("1 grant changed.", dto.Summary);
    }

    [Fact]
    public void Compare_GrantAddedWithASeat_IsAddedAndTheSeatChanges()
    {
        var after = DefinitionFixture.Edit("grants.yaml", "    seats: [outreach]",
            "    seats: [outreach]\n  - id: post-to-blog\n    allows: Publish a post\n    seats: [writer]");
        Edit(after, "seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts.\ngrants: [post-to-blog]");

        var dto = Changes(after);

        var grant = Assert.Single(dto.Grants);
        Assert.Equal("Grant post-to-blog added.", grant.Sentence);
        Assert.Equal(new[] { "allows Publish a post", "seat writer added" }, grant.Details);
        Assert.Equal(new[] { "grant post-to-blog added" }, Assert.Single(dto.Seats).Details);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // factory.yaml
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_FactoryFieldsChange_NamesEach()
    {
        var after = DefinitionFixture.Edit("factory.yaml", "title: DevThrottle", "title: DevThrottle Works");
        Edit(after, "factory.yaml", "purpose: Builds DevThrottle\n", "");
        Edit(after, "factory.yaml", "  agent: ClaudeCode", "  agent: Codex");
        Edit(after, "factory.yaml", "repositories: [devthrottle]", "repositories: [devthrottle, website]");
        Edit(after, "factory.yaml", "  Ship the product.\n", "  Ship the product.\n  Then sell it.\n");

        var dto = Changes(after);

        Assert.Equal(new[]
        {
            "title DevThrottle -> DevThrottle Works",
            "purpose removed (was Builds DevThrottle)",
            "goal text changed: 1 line added, 0 removed",
            "defaults.agent ClaudeCode -> Codex",
            "repository website added",
        }, dto.Factory.Select(f => f.Sentence));
        Assert.Equal(new[] { "title", "purpose", "goalText", "defaults.agent", "repositories" }, dto.Factory.Select(f => f.Field));
        Assert.Equal(new[] { " Ship the product.", "+Then sell it." }, dto.Factory[2].Diff!.Lines.Select(l => l.Marker + l.Text));
        // Boss and Writer run the factory's default agent; Outreach names its own.
        Assert.Equal(new[] { "boss", "writer" }, dto.Seats.Select(s => s.Seat));
        Assert.Equal(new[] { "agent ClaudeCode (the factory's default) -> Codex (the factory's default)" }, dto.Seats[0].Details);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Files, checks, skills
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_CheckChanges_ShowsItsFullTextDifference()
    {
        var before = DefinitionFixture.With("checks/new_mail.py", "import json\nimport os\n\ncount = 0\nprint(json.dumps({'count': count}))\n");
        var after = DefinitionFixture.With("checks/new_mail.py", "import json\nimport os\n\ncount = len(os.listdir('.'))\nprint(json.dumps({'count': count}))\n");

        var dto = FactoryDefinitionChanges.Compare(before, after);

        var file = Assert.Single(dto.Files);
        Assert.Equal("check", file.Kind);
        Assert.Equal("changed", file.Change);
        Assert.Equal("Check checks/new_mail.py changed: 1 line added, 1 removed.", file.Sentence);
        Assert.Equal(new[]
        {
            " import json", " import os", " ", "-count = 0", "+count = len(os.listdir('.'))", " print(json.dumps({'count': count}))",
        }, file.Diff.Lines.Select(l => l.Marker + l.Text));
        Assert.Equal("1 check changed.", dto.Summary);
    }

    [Fact]
    public void Compare_SkillChanges_ShowsItsFullTextDifference()
    {
        var dto = Changes(DefinitionFixture.Edit("skills/mail-voice/SKILL.md", "Write plainly.", "Write plainly.\nSign with the first name only."));

        var file = Assert.Single(dto.Files);
        Assert.Equal("skill", file.Kind);
        Assert.Equal("Skill file skills/mail-voice/SKILL.md changed: 1 line added, 0 removed.", file.Sentence);
        Assert.Equal(new[] { "... 2 unchanged lines ...", " description: How the factory writes mail.", " ---", " Write plainly.", "+Sign with the first name only." },
            file.Diff.Lines.Select(l => l.Marker + l.Text));
        Assert.Equal("1 skill file changed.", dto.Summary);
    }

    [Fact]
    public void Compare_FileAddedAndRemoved_ListsEach()
    {
        var after = Valid();
        after["files/mail-rules.yaml"] = "from: [ada@example.com]\n";
        after.Remove("skills/mail-voice/examples.md");

        var dto = Changes(after);

        Assert.Equal(new[] { "File files/mail-rules.yaml added: 1 line.", "Skill file skills/mail-voice/examples.md removed: 1 line." },
            dto.Files.Select(f => f.Sentence));
        Assert.Equal(new[] { "+from: [ada@example.com]" }, dto.Files[0].Diff.Lines.Select(l => l.Marker + l.Text));
        Assert.Equal(new[] { "-Dear Ada, thank you for writing." }, dto.Files[1].Diff.Lines.Select(l => l.Marker + l.Text));
        Assert.Equal("1 file added, 1 skill file removed.", dto.Summary);
    }

    [Fact]
    public void Compare_CommentAddedToASeat_IsALayoutChangeNotNothing()
    {
        var dto = Changes(DefinitionFixture.Edit("seats/writer.yaml", "role: Writer\n", "role: Writer  # writes the blog\n"));

        Assert.False(dto.NothingChanges);
        Assert.Empty(dto.Seats);
        var file = Assert.Single(dto.Files);
        Assert.Equal("seats/writer.yaml", file.Path);
        Assert.Equal("definition", file.Kind);
        Assert.Equal(new[] { "-role: Writer", "+role: Writer  # writes the blog" }, file.Diff.Lines.Where(l => l.Kind != "context").Select(l => l.Marker + l.Text));
    }

    [Fact]
    public void Compare_EmptyRuleSetAdded_IsListedAsAnEmptyDefinitionFile()
    {
        var after = Valid();
        after["rules/spare.json"] = "{ \"permissions\": {} }\n";

        var dto = Changes(after);

        Assert.Empty(dto.Rules);
        Assert.Equal("rules/spare.json added; it defines nothing yet.", Assert.Single(dto.Files).Sentence);
        Assert.Equal("1 empty definition file added.", dto.Summary);
    }

    [Fact]
    public void Compare_ComparedVersionDoesNotParse_IsRefusedWithItsProblem()
    {
        var bad = DefinitionFixture.With("seats/writer.yaml", "seat: writer\nname: [\n");

        var ex = Assert.Throws<ArgumentException>(() => FactoryDefinitionChanges.Compare(Valid(), bad));
        Assert.StartsWith("The compared definition does not parse, so what changes cannot be shown: seats/writer.yaml:", ex.Message);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Bounded and deterministic
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_LargeFileChange_IsCappedAndSaysHowManyWereLeftOut()
    {
        var oldText = string.Concat(Enumerable.Range(0, 1000).Select(i => $"line {i}\n"));
        var newText = string.Concat(Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? $"line {i}\n" : $"LINE {i}\n"));

        var dto = FactoryDefinitionChanges.Compare(DefinitionFixture.With("files/outreach_send.py", oldText), DefinitionFixture.With("files/outreach_send.py", newText));

        var diff = Assert.Single(dto.Files).Diff;
        Assert.Equal(500, diff.Added);
        Assert.Equal(500, diff.Removed);
        Assert.Equal(DefinitionTextDiff.MaxLinesShown, diff.Lines.Count(l => l.Kind != "skipped"));
        // 1500 lines in all (500 kept between the changes, 500 removed, 500 added), 400 shown.
        Assert.Equal(1100, diff.LinesNotShown);
        Assert.Equal("1100 more lines of this difference are not shown; at most 400 are shown.", diff.Note);
    }

    [Fact]
    public void Compare_TextsTooDifferentToMatch_ShowsTheChangedPartWholeAndSaysSo()
    {
        var oldText = "same\n" + string.Concat(Enumerable.Range(0, 700).Select(i => $"old {i}\n")) + "end\n";
        var newText = "same\n" + string.Concat(Enumerable.Range(0, 700).Select(i => $"new {i}\n")) + "end\n";

        var diff = DefinitionTextDiff.Compare(oldText, newText);

        Assert.Equal(700, diff.Added);
        Assert.Equal(700, diff.Removed);
        Assert.StartsWith("The two texts are too different to match line by line (more than 1000 changes)", diff.Note);
        Assert.Equal(new[] { " same", "-old 0" }, diff.Lines.Take(2).Select(l => l.Marker + l.Text));
    }

    [Fact]
    public void Compare_LongLine_IsCutSayingHowMuch()
    {
        var diff = DefinitionTextDiff.Compare("", new string('x', 600) + "\n");

        Assert.Equal(new string('x', 500) + " ... (100 more characters)", Assert.Single(diff.Lines).Text);
    }

    [Fact]
    public void TextDiff_DistantChanges_ShowsContextAndSkippedRuns()
    {
        var oldText = string.Concat(Enumerable.Range(1, 20).Select(i => $"{i}\n"));
        var newText = oldText.Replace("2\n3\n", "2\nthree\n").Replace("18\n", "eighteen\n");

        var diff = DefinitionTextDiff.Compare(oldText, newText);

        Assert.Equal(new[]
        {
            " 1", " 2", "-3", "+three", " 4", " 5", " 6", "... 8 unchanged lines ...", " 15", " 16", " 17", "-18", "+eighteen", " 19", " 20",
        }, diff.Lines.Select(l => l.Marker + l.Text));
    }

    [Fact]
    public void TextDiff_OnlyLineEndingsChange_SaysSo()
    {
        var diff = DefinitionTextDiff.Compare("a\nb\n", "a\r\nb\r\n");

        Assert.Empty(diff.Lines);
        Assert.Equal("Only the line endings or the final line break change.", diff.Note);
    }

    [Fact]
    public void Compare_SameInputs_GiveTheSameOutputWhateverTheMapOrder()
    {
        var after = DefinitionFixture.Edit("seats/outreach.yaml", "machine: SOREN_SOUTH", "machine: devlinux");
        Edit(after, "rules/base.json", "\"deny\": [\"Bash(git push:*)\"]", "\"deny\": []");
        after["files/new.txt"] = "hello\n";
        var reversed = after.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var publishedReversed = Valid().Reverse().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var first = JsonSerializer.Serialize(FactoryDefinitionChanges.Compare(Valid(), after));
        var second = JsonSerializer.Serialize(FactoryDefinitionChanges.Compare(Valid(), after));
        var third = JsonSerializer.Serialize(FactoryDefinitionChanges.Compare(publishedReversed, reversed));

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }
}
