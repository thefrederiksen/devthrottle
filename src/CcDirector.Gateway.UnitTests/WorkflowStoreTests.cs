using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="WorkflowStore"/> and <see cref="BuiltInWorkflowSeeder"/> (Workflows
/// mission, phase 1). Covers: fresh seeding of the shipped built-ins, idempotent re-seeding (a
/// restart mints nothing), the ours/yours upgrade trade (newer shipped content auto-publishes ONLY
/// while the user has not customized the workflow), and the read projections' legacy-shape fields.
///
/// Also the one place the mission workflow's two copies of its own steps are held equal: the
/// WorkflowStep records the Cockpit shows, against the markdown step table an agent is served.
/// </summary>
public sealed class WorkflowStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Seeds_the_four_built_ins_in_shipped_order()
    {
        var store = new WorkflowStore(_h.Open());

        var workflows = store.ListPublished();

        Assert.Equal(new[] { "mission", "standalone", "standalone-with-review", "fleet-manager" },
            workflows.Select(w => w.Id).ToArray());
        Assert.All(workflows, w =>
        {
            Assert.True(w.IsBuiltIn);
            Assert.Equal(1, w.Version);
            Assert.False(w.HasDraft);
            Assert.False(string.IsNullOrWhiteSpace(w.ContentHash));
            Assert.NotEmpty(w.Steps);
        });
    }

    [Fact]
    public void Reseeding_on_restart_mints_nothing_new()
    {
        _ = new WorkflowStore(_h.Open());

        // A "restart" is a brand-new database + store over the same file.
        var store = new WorkflowStore(_h.Open());

        var workflows = store.ListPublished();
        Assert.Equal(4, workflows.Count);
        Assert.All(workflows, w => Assert.Equal(1, w.Version));

        using var ctx = _h.Open().CreateContext();
        Assert.Equal(4, ctx.WorkflowVersions.Count());
    }

    [Fact]
    public void GetPublished_is_case_insensitive_and_null_for_unknown()
    {
        var store = new WorkflowStore(_h.Open());

        Assert.NotNull(store.GetPublished("MISSION"));
        Assert.Equal("Mission", store.GetPublished("mission")!.Name);
        Assert.Null(store.GetPublished("does-not-exist"));
        Assert.Null(store.GetPublished(""));
    }

    [Fact]
    public void Every_built_in_version_row_stores_its_instruction_body()
    {
        _ = new WorkflowStore(_h.Open());

        using var ctx = _h.Open().CreateContext();
        foreach (var id in new[] { "mission", "standalone", "standalone-with-review", "fleet-manager" })
        {
            var version = ctx.WorkflowVersions.Single(v => v.WorkflowId == id);
            Assert.Equal(BuiltInWorkflows.InstructionsFor(id), version.InstructionsMarkdown);
            Assert.Equal(WorkflowVersionStatus.Published, version.Status);
        }
    }

    [Fact]
    public void Every_built_in_ships_a_non_empty_instruction_body()
    {
        // The comparison above cannot see an empty resource: the shipped body and the stored row would
        // both be empty, and an empty conduct would be served silently to every seat.
        var ids = BuiltInWorkflows.All().Select(w => w.Id).ToArray();
        Assert.Contains("fleet-manager", ids);

        foreach (var id in ids)
            Assert.False(string.IsNullOrWhiteSpace(BuiltInWorkflows.InstructionsFor(id)),
                $"Built-in workflow '{id}' ships an empty instruction body.");
    }

    [Fact]
    public void Fleet_manager_conduct_learns_of_stops_from_events_and_never_asks_for_reports()
    {
        // The owner ruled (2026-09-16) that the sessions a Fleet Manager starts never report to it: the Gateway's
        // events carry each stop, with the Wingman's reading, and the Fleet Manager acknowledges them by id.
        var body = BuiltInWorkflows.InstructionsFor("fleet-manager");

        Assert.Contains("[Fleet Manager events]", body);
        Assert.Contains("acknowledge", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event ids you have handled", body);
        Assert.DoesNotContain("devthrottle session report", body);
        Assert.DoesNotContain("`session report`", body);
        Assert.DoesNotContain("emporary", body);
    }

    [Fact]
    public void Fleet_manager_conduct_leaves_a_stop_waiting_for_its_reading_alone_and_reads_every_event()
    {
        // Step 4, round 3: a stop still waiting for its reading is neither acted on nor acknowledged, and the digest
        // may say more events remain. The phrases span wrapped lines, and a Windows checkout embeds the body with CRLF.
        var body = Normalize(BuiltInWorkflows.InstructionsFor("fleet-manager"));

        Assert.Contains("Leave alone any stop still waiting\n   for the Wingman's reading - do not act on it and do not acknowledge it", body);
        Assert.Contains("A stop still waiting for the Wingman's reading is not yours to act on yet.", body);
        Assert.Contains("If\n   the digest says more remain, read the rest before you act.", body);
    }

    [Fact]
    public void Fleet_manager_conduct_acts_on_an_answered_card_event_and_starts_on_a_marked_event()
    {
        // Steps 5 and 6 fixes. One phrase spans a wrapped line, and a Windows checkout embeds the body with CRLF.
        var body = Normalize(BuiltInWorkflows.InstructionsFor("fleet-manager"));

        Assert.Contains("**An `answered` event is the owner pressing a button on one of your cards.**", body);
        Assert.Contains("do not answer the record again. Then acknowledge the event.", body);
        Assert.Contains("**A `marked` event means you have just become the Fleet Manager**", body);
        Assert.Contains("Until it arrives, a session\n  started to take over does nothing", body);
    }

    [Fact]
    public void Fleet_manager_conduct_writes_its_advice_when_it_files_and_reads_the_owners_answers_in_the_digest()
    {
        // Step 7: the walkthrough's advice is written when the record is filed, and an answer given there is recorded on
        // the record rather than typed to the Fleet Manager. Two phrases span a wrapped line, and a Windows checkout
        // embeds the body with CRLF.
        var body = Normalize(BuiltInWorkflows.InstructionsFor("fleet-manager"));

        Assert.Contains("Write it when you FILE the record (`--advice`, and `--pick` for\n    the option you would choose)", body);
        Assert.Contains("replace it with `fleet advise` if the picture changes", body);
        Assert.Contains("the Gateway refuses a line break or more than 300 characters", body);
        Assert.Contains("read what they decided in `fleet digest` (`answered`)", body);
    }

    [Fact]
    public void Uncustomized_built_in_auto_publishes_newer_shipped_content()
    {
        var db = _h.Open();
        _ = new WorkflowStore(db);

        // Simulate a database seeded by an OLDER binary: what is published matches what that binary
        // shipped, but both differ from what THIS binary ships.
        using (var ctx = db.CreateContext())
        {
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            var published = ctx.WorkflowVersions.Single(
                v => v.WorkflowId == "mission" && v.Status == WorkflowVersionStatus.Published);
            head.ShippedContentHash = "old-shipped-hash";
            published.ContentHash = "old-shipped-hash";
            ctx.SaveChanges();
        }

        var store = new WorkflowStore(_h.Open());

        var mission = store.GetPublished("mission")!;
        Assert.Equal(2, mission.Version);
        using (var ctx = _h.Open().CreateContext())
        {
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            Assert.Equal(2, head.LatestVersion);
            Assert.Equal(2, head.PublishedVersion);
            var superseded = ctx.WorkflowVersions.Single(
                v => v.WorkflowId == "mission" && v.Version == 1);
            Assert.Equal(WorkflowVersionStatus.Superseded, superseded.Status);
        }
    }

    [Fact]
    public void Previously_customized_built_in_is_republished_to_shipped_with_history_kept()
    {
        // Built-ins are READ-ONLY (Shared Workflow Library phase 3, owner ruling 2026-07-24,
        // reversing the 2026-07-17 editable-with-reset trade): a customization published under the
        // OLD ruling is superseded by shipped content on the next seed - even when the binary's own
        // content did not change - and the edit stays as forever-readable pinned history.
        var db = _h.Open();
        _ = new WorkflowStore(db);

        string customizedHash = "the-users-own-edit";
        using (var ctx = db.CreateContext())
        {
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            var published = ctx.WorkflowVersions.Single(
                v => v.WorkflowId == "mission" && v.Status == WorkflowVersionStatus.Published);
            head.ShippedContentHash = "old-shipped-hash";
            published.ContentHash = customizedHash;
            published.InstructionsMarkdown = "# The user's own conduct";
            ctx.SaveChanges();
        }

        var store = new WorkflowStore(_h.Open());

        var mission = store.GetPublished("mission")!;
        Assert.Equal(2, mission.Version); // shipped content took the head as a NEW version
        Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), store.GetInstructions("mission", null));
        // The customization is history, not gone: the pinned read still serves it.
        Assert.Equal("# The user's own conduct", store.GetInstructions("mission", version: 1));
        using (var ctx = _h.Open().CreateContext())
        {
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            Assert.Equal(2, head.LatestVersion);
            Assert.Equal(2, head.PublishedVersion);
            Assert.Equal(head.ShippedContentHash,
                ctx.WorkflowVersions.Single(v => v.WorkflowId == "mission" && v.Version == 2).ContentHash);
        }
    }

    // ---- the content-hash invariant ----------------------------------------------------------------
    // The whole ours/yours upgrade trade rides on the bundle hash: "uncustomized" is a hash equality
    // and "this binary ships something different" is a hash inequality. A hash function that returned
    // any CONSTANT would leave every store test above green while silently breaking upgrade detection
    // forever - so the function's two invariants are pinned directly.

    [Fact]
    public void Bundle_hash_is_deterministic_and_sensitive_to_every_content_change()
    {
        var steps = new List<Contracts.WorkflowStepDto>
        {
            new() { Name = "Do", Description = "d", Doer = "Worker", Reviewer = null, Done = "merged" },
        };
        string Hash(string name = "n", string summary = "s", string instructions = "i") =>
            WorkflowContentHash.ForBundle(name, summary, "w", "h", steps,
                Array.Empty<Contracts.WorkflowOutcomeCriterionDto>(), instructions,
                Array.Empty<(string, string)>());

        // Deterministic: the same bundle always hashes the same.
        Assert.Equal(Hash(), Hash());

        // Sensitive: any changed piece - metadata, instructions, steps, files - changes the hash.
        Assert.NotEqual(Hash(), Hash(name: "other"));
        Assert.NotEqual(Hash(), Hash(summary: "other"));
        Assert.NotEqual(Hash(), Hash(instructions: "other"));
        var reviewedSteps = new List<Contracts.WorkflowStepDto>
        {
            new() { Name = "Do", Description = "d", Doer = "Worker", Reviewer = "Reviewer", Done = "merged" },
        };
        Assert.NotEqual(Hash(),
            WorkflowContentHash.ForBundle("n", "s", "w", "h", reviewedSteps,
                Array.Empty<Contracts.WorkflowOutcomeCriterionDto>(), "i", Array.Empty<(string, string)>()));
        Assert.NotEqual(Hash(),
            WorkflowContentHash.ForBundle("n", "s", "w", "h", steps,
                Array.Empty<Contracts.WorkflowOutcomeCriterionDto>(), "i",
                new[] { ("helpers.py", WorkflowContentHash.ForFile("print()")) }));
    }

    // ---- the mission step table against the mission definition -------------------------------------
    // The mission workflow's five steps are written TWICE, and served to two different readers: as
    // WorkflowStep records in BuiltInWorkflows.cs, which is what the Gateway and the Cockpit show, and
    // as a five-row markdown table in mission.instructions.md, which is what an agent is served when it
    // runs `cc-devthrottle workflow instructions mission`. Nothing else holds the two together, so
    // replacing a doer or a reviewer in one of them would leave this whole suite green while the
    // Cockpit advertised a different mission from the one agents are told to run.
    //
    // This is deliberately a NARROW contract - five rows against five records, four fields each - and
    // not the byte-for-byte fidelity test it replaces. It says nothing about the prose around the
    // table, and it is not meant to.

    /// <summary>The literal Reviewer cell the markdown table uses for a step with no separate review
    /// seat. A null <see cref="WorkflowStep.Reviewer"/> is a statement the workflow is making, so the
    /// table spells it rather than leaving the cell blank.</summary>
    private const string NoReviewerCell = "none";

    [Fact]
    public void Mission_step_table_in_the_conduct_matches_the_mission_definition()
    {
        var mission = BuiltInWorkflows.All().Single(w => w.Id == "mission");
        var rows = StepTableRows(Normalize(BuiltInWorkflows.InstructionsFor("mission")));

        Assert.True(mission.Steps.Count == rows.Count,
            "The mission workflow's step table and its C# definition have different numbers of steps." + Environment.NewLine +
            $"  BuiltInWorkflows.cs has {mission.Steps.Count}: {string.Join(", ", mission.Steps.Select(s => Quote(s.Name)))}" + Environment.NewLine +
            $"  mission.instructions.md has {rows.Count}: {string.Join(", ", rows.Select(r => Quote(r[0])))}" + Environment.NewLine +
            "Add or remove the step in BOTH, or the Cockpit and the agents are running different missions.");

        for (var i = 0; i < mission.Steps.Count; i++)
        {
            var step = mission.Steps[i];
            var row = rows[i];
            var number = i + 1;

            // A literal "none" in the record would read as a reviewer actually named none in the Cockpit
            // and as no reviewer at all in the table: the two would agree while meaning opposite things.
            Assert.True(step.Reviewer != NoReviewerCell,
                $"Row {number}: BuiltInWorkflows.cs gives the step {Quote(step.Name)} the literal reviewer " +
                $"{Quote(NoReviewerCell)}, which is the table's spelling for NO reviewer. Use a null Reviewer.");

            AssertCell(number, step.Name, "Step", step.Name, row[0]);
            AssertCell(number, step.Name, "Doer", step.Doer, row[1]);
            AssertCell(number, step.Name, "Reviewer", step.Reviewer ?? NoReviewerCell, row[2]);
            AssertCell(number, step.Name, "Done when", step.Done, row[3]);
        }
    }

    /// <summary>Compare one cell against one field, and say WHICH row and WHICH column disagreed and
    /// what each side holds. "Assert.Equal() Failure" on its own costs the next reader exactly the time
    /// this test exists to save.</summary>
    private static void AssertCell(int rowNumber, string stepName, string column, string definition, string table)
    {
        Assert.True(string.Equals(definition, table, StringComparison.Ordinal),
            "The mission workflow's step table and its C# definition disagree." + Environment.NewLine +
            $"  Row {rowNumber} ({Quote(stepName)}), column {Quote(column)}." + Environment.NewLine +
            $"  BuiltInWorkflows.cs:      {Quote(definition)}" + Environment.NewLine +
            $"  mission.instructions.md:  {Quote(table)}" + Environment.NewLine +
            "Both are served - the Cockpit shows the record, an agent is served the table - so a change " +
            "to either one has to be made in the other.");
    }

    private static string Quote(string value) => "\"" + value + "\"";

    /// <summary>
    /// The rows of the "The five steps" table in the shipped mission conduct, each as its four visible
    /// cells in table order: Step, Doer, Reviewer, Done when. Read out of the embedded resource - the
    /// same place the product reads it - so this cannot pass against a file the Gateway does not ship.
    ///
    /// The markdown cell padding is stripped and an escaped pipe is unescaped (the one character a
    /// GitHub-flavoured table cell MUST escape). Nothing else is normalised: a real difference in
    /// wording, capitalisation or punctuation is a difference, and must fail.
    ///
    /// Every failure to FIND the table is loud. A parser that quietly returned no rows would pass this
    /// test the day the table was deleted, which is the drift it exists to catch.
    /// </summary>
    private static IReadOnlyList<string[]> StepTableRows(string body)
    {
        var lines = body.Split('\n');
        var header = -1;
        for (var i = 0; i < lines.Length && header < 0; i++)
        {
            if (!IsTableRow(lines[i]))
                continue;

            var cells = TableCells(lines[i]);
            if (cells.Length == 4 && cells[0] == "Step" && cells[1] == "Doer"
                && cells[2] == "Reviewer" && cells[3] == "Done when")
                header = i;
        }

        Assert.True(header >= 0,
            "The shipped mission conduct has no step table: no row with the cells "
            + "\"Step | Doer | Reviewer | Done when\" was found in the embedded "
            + "Workflows/Content/mission.instructions.md. That table is what every agent running a "
            + "mission is served, so it cannot be removed or renamed without moving this test with it.");

        Assert.True(header + 1 < lines.Length && IsDelimiterRow(lines[header + 1]),
            "The step table's header row is not followed by a markdown delimiter row (|---|---|---|---|), "
            + "so what was found is not a table.");

        var rows = new List<string[]>();
        for (var i = header + 2; i < lines.Length && IsTableRow(lines[i]); i++)
        {
            var cells = TableCells(lines[i]);
            Assert.True(cells.Length == 4,
                $"Step table row {rows.Count + 1} has {cells.Length} cells, not 4: {lines[i].Trim()}");
            rows.Add(cells);
        }

        Assert.True(rows.Count > 0, "The step table has a header row and no step rows at all.");
        return rows;
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith("|", StringComparison.Ordinal);

    private static bool IsDelimiterRow(string line)
    {
        if (!IsTableRow(line))
            return false;

        var cells = TableCells(line);
        return cells.Length > 0 && cells.All(c => c.Length > 0 && c.All(ch => ch == '-' || ch == ':'));
    }

    /// <summary>The visible cells of one markdown table row: split on UNESCAPED pipes, drop the fencing
    /// pipe at each end, unescape an escaped pipe, and strip the padding spaces.</summary>
    private static string[] TableCells(string line)
    {
        var row = line.Trim();
        Assert.True(row.StartsWith("|", StringComparison.Ordinal) && row.EndsWith("|", StringComparison.Ordinal),
            $"A markdown table row is fenced with a pipe at each end, and this one is not: {row}");

        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        for (var i = 1; i < row.Length - 1; i++)
        {
            // i + 1 == row.Length - 1 is the closing fence, never an escaped cell pipe.
            if (row[i] == '\\' && i + 1 < row.Length - 1 && row[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }

            if (row[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(row[i]);
        }

        cells.Add(cell.ToString().Trim());
        return cells.ToArray();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
