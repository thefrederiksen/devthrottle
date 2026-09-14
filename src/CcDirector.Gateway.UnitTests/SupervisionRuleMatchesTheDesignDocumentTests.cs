using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE WRITTEN RULE AND THE CODE, CHECKED AGAINST EACH OTHER.
///
/// Reads the supervision table out of <c>docs/new_architecture/sessions.html</c> and asserts
/// that the shipped attention rule answers what the table says, for every case, in both directions.
///
/// IT DRIVES THE REAL PATH, NOT THE FIELD. <see cref="SessionOrdering.IsSupervised"/> now reads a single
/// stamped fact, so a test that set that fact and then asserted it came back would be a tautology dressed
/// as a guard - it would pass with the whole resolver deleted. Each row therefore BUILDS A FLEET, hands it
/// to <see cref="FleetRoleResolver"/> exactly as the Gateway does, and asks the question afterwards. What
/// is under test is the chain the product actually runs: a supervisor that is alive or is not, resolved
/// across the fleet, folded into an answer.
///
/// WHY THIS EXISTS, AND WHY A BEHAVIOUR TEST WAS NOT ENOUGH. The owner amended the attention rule on
/// 2026-07-09 - the Architect stops surfacing to him - and that amendment sat in the design document,
/// unimplemented, until 2026-09-03. Nothing was broken. Nothing was flaky. Every test was green, because
/// every test asserted the SHIPPED behaviour in the PRESENT TENSE, which is what tests do. A document
/// saying the opposite could not make any of them go red. The divergence was invisible to the machine and
/// was only ever going to be found by a person happening to read both halves side by side, and for two
/// months nobody did.
///
/// So the answer is not "write the behaviour assertion the other way round" - that has now been done
/// three times, in three directions, and it would not have caught any of the drifts. The answer is a test
/// whose INPUTS come from the DOCUMENT and whose EXPECTED ANSWERS come from the CODE, so that changing one
/// without the other is itself the failure.
///
/// IT CANNOT PASS BY FINDING NOTHING. The table must be present, fenced by its markers, and it must name
/// EVERY combination of the four roles in <see cref="SessionRoles.All"/>, the two origin kinds, and the
/// two supervisor states. A missing marker, an empty table, or a table that quietly stopped covering a
/// case fails with a message naming what is missing - never a silent pass. That is deliberate: a check
/// whose pass condition is an ABSENCE certifies a run that never happened.
/// </summary>
public sealed class SupervisionRuleMatchesTheDesignDocumentTests
{
    private const string DocRelativePath = "docs/new_architecture/sessions.html";
    private const string Begin = "<!-- SUPERVISION-TABLE-BEGIN -->";
    private const string End = "<!-- SUPERVISION-TABLE-END -->";

    /// <summary>The two origin kinds the table distinguishes. "(none)" is how it spells an ordinary session
    /// that nothing scheduled; "schedule" is a cron firing or a work-list item. Neither decides anything any
    /// more - they are in the table precisely so that stays provable.</summary>
    private static readonly string[] OriginKinds = { "(none)", "schedule" };

    /// <summary>The two answers to the only question the rule asks.</summary>
    private static readonly string[] SupervisorStates = { "no", "yes" };

    private sealed record Row(string Role, string Origin, bool LiveSupervisor, bool Supervised);

    /// <summary>
    /// How a row with no live supervisor can come about. BOTH are built and both must answer the same, because
    /// they are two different failures wearing one verdict: a session nothing ever supervised, and a session
    /// whose supervisor has died underneath it. The second is the one that was broken - an explicitly stamped
    /// seat skipped the liveness check, so the orphan went on being quietened by a supervisor that had exited
    /// hours earlier.
    /// </summary>
    private enum NoSupervisor { NeverHadOne, ItDied }

    [Fact]
    public void EveryCaseInTheDesignDocument_GetsTheAnswerTheDocumentStates()
    {
        var rows = ReadTable();

        foreach (var row in rows)
        {
            if (row.LiveSupervisor)
            {
                AssertRow(row, BuildFleet(row, null));
            }
            else
            {
                // Both shapes of "nobody is there", so neither can regress behind the other.
                AssertRow(row, BuildFleet(row, NoSupervisor.NeverHadOne));
                AssertRow(row, BuildFleet(row, NoSupervisor.ItDied));
            }
        }
    }

    private static void AssertRow(Row row, SessionDto subject)
    {
        var actual = SessionOrdering.IsSupervised(subject);

        Assert.True(row.Supervised == actual,
            $"{DocRelativePath} says a {row.Role} with origin {row.Origin} and live supervisor " +
            $"\"{(row.LiveSupervisor ? "yes" : "no")}\" is {(row.Supervised ? "SUPERVISED" : "HUMAN-FACING")}, " +
            $"and the shipped rule says {(actual ? "SUPERVISED" : "HUMAN-FACING")}. One of the two is wrong " +
            "and BOTH have to change in the same pull request. This exact divergence - a rule written down " +
            "in one half and not the other - went unnoticed for two months in 2026.");
    }

    /// <summary>
    /// The subject session plus whatever supervisor its row calls for, resolved by the production resolver.
    /// The seat is set EXPLICITLY, which is not a shortcut to reach the role - it is the shape that was
    /// broken. An explicit stamp short-circuits role derivation, so if supervision were still read off the
    /// seat this fleet is exactly where it would give the wrong answer.
    /// </summary>
    private static SessionDto BuildFleet(Row row, NoSupervisor? absence)
    {
        var subject = new SessionDto
        {
            SessionId = "subject",
            Name = "subject",
            ActivityState = "WaitingForInput",
            ExplicitRole = row.Role,
            OriginKind = row.Origin == "(none)" ? null : row.Origin,
            IsControlled = absence != NoSupervisor.NeverHadOne,
            ControllerSessionId = absence == NoSupervisor.NeverHadOne ? null : "supervisor",
        };

        var fleet = new List<SessionDto> { subject };

        if (absence != NoSupervisor.NeverHadOne)
        {
            fleet.Add(new SessionDto
            {
                SessionId = "supervisor",
                Name = "supervisor",
                // Exited is the ONE state that takes a session out of the liveness set.
                ActivityState = absence == NoSupervisor.ItDied ? "Exited" : "Working",
            });
        }

        FleetRoleResolver.Stamp(fleet);
        return subject;
    }

    [Fact]
    public void TheTableCoversEveryCase_soItCannotPassBySayingNothing()
    {
        var rows = ReadTable();

        // THE PRESENCE HALF. Derive what the table MUST cover from SessionRoles itself, so teaching the
        // product a fifth role makes this fail with the missing row named, rather than passing over a table
        // that has silently stopped describing the fleet.
        var expected = SessionRoles.All
            .SelectMany(role => OriginKinds.SelectMany(origin =>
                SupervisorStates.Select(sup => (role, origin, sup))))
            .ToList();

        var have = rows.Select(r => (r.Role, r.Origin, r.LiveSupervisor ? "yes" : "no")).ToHashSet();

        var missing = expected.Where(e => !have.Contains(e)).ToList();
        Assert.True(missing.Count == 0,
            $"The supervision table in {DocRelativePath} does not name: " +
            string.Join(", ", missing.Select(m => $"{m.role}/{m.origin}/supervisor={m.sup}")) +
            ". Every role in SessionRoles.All crossed with every origin kind and both supervisor states must " +
            "appear, so the document cannot describe a smaller fleet than the code has.");

        Assert.Equal(expected.Count, rows.Count);

        // And the verdicts are not all one word - a table of sixteen identical answers would satisfy the
        // count and prove nothing about the rule it claims to state.
        Assert.Contains(rows, r => r.Supervised);
        Assert.Contains(rows, r => !r.Supervised);
    }

    [Fact]
    public void TheVerdictTurnsOnTheLiveSupervisorAlone_TheOwnersRulingOf13September2026()
    {
        // THE CLAIM THE SIXTEEN ROWS EXIST TO MAKE, asserted by name so that making a seat matter again is a
        // deliberate act with the ruling in front of you: "I think it is wrong that somebody without a parent
        // can automatically be put on snooze because nobody knows they existed."
        //
        // A two-row table would have stated the rule correctly and proved nothing about what went wrong. For
        // two years the answer turned on a category stamped at birth. This says it turns on one live fact,
        // and it says it over the whole cross-product rather than over the one row somebody remembered.
        var rows = ReadTable();

        foreach (var group in rows.GroupBy(r => r.LiveSupervisor))
        {
            var verdicts = group.Select(r => r.Supervised).Distinct().ToList();
            Assert.True(verdicts.Count == 1,
                $"The supervision table in {DocRelativePath} gives more than one verdict for live " +
                $"supervisor = \"{(group.Key ? "yes" : "no")}\". The seat and the origin have no vote: every " +
                "row with a live supervisor is SUPERVISED and every row without one is HUMAN-FACING. A table " +
                "that splits within a block has put a category back in charge of who may reach the owner.");
        }

        Assert.True(rows.Where(r => r.LiveSupervisor).All(r => r.Supervised),
            "A session with a live supervisor must be SUPERVISED in every seat.");
        Assert.True(rows.Where(r => !r.LiveSupervisor).All(r => !r.Supervised),
            "A session with nobody holding it must be HUMAN-FACING in every seat - including a scheduled run.");
    }

    [Fact]
    public void AnOrphanedWorker_ReachesTheOwner_EvenWhenItsSeatWasStampedByHand()
    {
        // THE DEFECT THIS CHANGE CLOSES, PINNED SO IT CANNOT COME BACK QUIETLY.
        //
        // Observed on the live fleet on 2026-09-13: sessions carrying an explicit "Worker" stamp whose
        // supervisor was not in the fleet at all, sitting grey and labelled "Snoozed" with nothing snoozed.
        // The document promised an orphan escape hatch; it could not fire, because an explicit role returns
        // from FleetRoleResolver before the liveness check is ever reached, so the seat outlived the
        // relationship it was shorthand for.
        var orphan = new SessionDto
        {
            SessionId = "orphan",
            Name = "orphan",
            ActivityState = "WaitingForInput",
            ExplicitRole = SessionRoles.Worker,
            IsControlled = true,
            ControllerSessionId = "supervisor-that-is-gone",
        };

        // The supervisor is absent from the fleet entirely - not exited, simply not there.
        FleetRoleResolver.Stamp(new List<SessionDto> { orphan });

        Assert.Equal(SessionRoles.Worker, orphan.SessionRole);   // the stamp still wins for the SEAT
        Assert.False(orphan.HasLiveSupervisor);                  // but it cannot buy the session quiet
        Assert.False(SessionOrdering.IsSupervised(orphan));
        Assert.Equal("red", SessionOrdering.EffectiveColor(orphan));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(orphan));
    }

    [Fact]
    public void AScheduledRun_ReachesTheOwner_TheReversalOf13September2026()
    {
        // The schedule arm used to silence this row on the grounds that a cron run escalates by email. The
        // owner reversed it: a session nobody is holding is his, whatever started it. An email path that
        // nothing on the roster can attest to is not a supervisor.
        var cron = new SessionDto
        {
            SessionId = "cron",
            Name = "cron",
            ActivityState = "WaitingForInput",
            OriginKind = "schedule",
        };

        FleetRoleResolver.Stamp(new List<SessionDto> { cron });

        Assert.False(SessionOrdering.IsSupervised(cron));
        Assert.Equal("red", SessionOrdering.EffectiveColor(cron));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(cron));
    }

    [Fact]
    public void AWorkerWithALiveSupervisor_IsStillHeld()
    {
        // The negative control. If everything surfaced, every assertion above would pass and the rule would
        // be doing no work at all - the roster would simply have stopped quietening anything.
        var worker = new SessionDto
        {
            SessionId = "worker",
            Name = "worker",
            ActivityState = "WaitingForInput",
            IsControlled = true,
            ControllerSessionId = "supervisor",
        };
        var supervisor = new SessionDto { SessionId = "supervisor", Name = "supervisor", ActivityState = "Working" };

        FleetRoleResolver.Stamp(new List<SessionDto> { worker, supervisor });

        Assert.True(worker.HasLiveSupervisor);
        Assert.True(SessionOrdering.IsSupervised(worker));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(worker));
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(worker));
    }

    /// <summary>
    /// The rows between the two markers. Fails loudly - naming the file and the marker - when the fence is
    /// gone or the table between it is empty, rather than returning an empty list that would let every
    /// assertion above pass over nothing.
    /// </summary>
    private static IReadOnlyList<Row> ReadTable()
    {
        var path = Path.Combine(RepoRoot(), DocRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"The design document was not found at {path}.");

        var text = File.ReadAllText(path).Replace("\r\n", "\n");

        var begin = text.IndexOf(Begin, StringComparison.Ordinal);
        Assert.True(begin >= 0,
            $"{DocRelativePath} no longer contains the marker {Begin}. The supervision table is the written " +
            "half of SessionOrdering.IsSupervised and this test is what holds the two together - if the " +
            "table has moved, move the marker with it; do not delete it.");

        var end = text.IndexOf(End, begin, StringComparison.Ordinal);
        Assert.True(end > begin, $"{DocRelativePath} contains {Begin} but no closing {End}.");

        var body = text[(begin + Begin.Length)..end];

        // THE PARSER'S TABLE AND THE READER'S TABLE MUST BE THE SAME TABLE. The markers are HTML comments,
        // which a reader cannot see, so every check below exists to close a way of making this test read
        // something the person opening the document does not. All five were found by adversarial review on
        // 14 September 2026, and four of them PASSED before these lines existed.
        //
        // 1. A table inside <pre>/<code> renders as a code SAMPLE - the document would state no rule at all.
        Assert.DoesNotContain("<pre", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<code", body, StringComparison.OrdinalIgnoreCase);

        // 2. A COMMENTED-OUT ROW is the sharpest of them. This parser matches <tr> inside an HTML comment
        //    perfectly well, so the coverage check counts a row the reader cannot see: the document says
        //    nothing about that case while the machine certifies every case is covered.
        Assert.DoesNotContain("<!--", body, StringComparison.Ordinal);

        // 3. A HIDDEN table satisfies every content check and renders nothing.
        foreach (var hider in new[] { "hidden", "display:none", "display: none", "visibility:hidden", "visibility: hidden" })
            Assert.True(body.IndexOf(hider, StringComparison.OrdinalIgnoreCase) < 0,
                $"The supervision table in {DocRelativePath} carries \"{hider}\", so it does not render. A rule " +
                "nobody can read is not a written rule, however well this parser can still find it.");

        Assert.True(body.Contains("<table", StringComparison.OrdinalIgnoreCase),
            $"There is no <table> between the supervision markers in {DocRelativePath}. The rows must be a " +
            "real table that renders for a reader, not raw text that only this parser can see.");

        // 4. EXACTLY ONE TABLE, AND IT IS THIS ONE. A second table elsewhere in the document, saying the
        //    opposite, is invisible to a parser that only reads between the markers - and a reader has no
        //    way to know which of the two the machine checked.
        Assert.True(body.Contains("id=\"supervision-table\"", StringComparison.Ordinal),
            $"The table between the supervision markers in {DocRelativePath} must carry " +
            "id=\"supervision-table\", so that the table this guard reads is identifiable as the one the " +
            "document presents.");
        Assert.Equal(1, CountOccurrences(text, "id=\"supervision-table\""));
        Assert.True(CountOccurrences(text, "| Resolved role") == 0,
            $"{DocRelativePath} still contains a markdown-style supervision header. The table moved to HTML; " +
            "a leftover markdown copy is a second written answer to a question that has one.");
        var headerElsewhere = CountOccurrences(text, "<th>Resolved role</th>");
        Assert.True(headerElsewhere == 1,
            $"{DocRelativePath} has {headerElsewhere} tables with a \"Resolved role\" header. There must be " +
            "exactly one - a second one is a contradicting rule this guard would never read.");

        // The header is REQUIRED and must name all four columns in order, so a table that quietly lost a
        // column cannot go on being read positionally against the wrong meanings.
        var header = Cells(body, "th");
        Assert.True(
            header.Count == 4 && header[0] == "Resolved role" && header[1] == "Origin kind" &&
            header[2] == "Live supervisor" && header[3] == "Verdict",
            $"The supervision table in {DocRelativePath} must have the header row \"Resolved role | Origin " +
            $"kind | Live supervisor | Verdict\". Found: \"{string.Join(" | ", header)}\". The rows below " +
            "are read by POSITION, so a renamed or reordered column would silently change what every row " +
            "claims.");

        var rows = new List<Row>();
        foreach (Match tr in Regex.Matches(body, @"<tr(?:\s[^>]*)?>(.*?)</tr>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var row = tr.Groups[1].Value;

            // The header row is the ONLY row allowed to contribute nothing, and it is identified by having
            // <th> cells rather than by being first or by being empty. Anything else that yields no data
            // cells FAILS below rather than being skipped - skipping is how a case silently stops being
            // covered while everything stays green.
            if (row.Contains("<th", StringComparison.OrdinalIgnoreCase)) continue;

            var cells = Cells(row, "td");
            Assert.True(cells.Count == 4,
                $"A row of the supervision table in {DocRelativePath} has {cells.Count} cells, not 4: " +
                $"\"{row.Trim()}\". A malformed row FAILS rather than being skipped - skipping is how a " +
                "case silently stops being covered while everything stays green.");

            var supervisor = cells[2];
            Assert.True(supervisor is "yes" or "no",
                $"The supervision table in {DocRelativePath} has the live-supervisor value \"{supervisor}\" " +
                $"for {cells[0]}/{cells[1]}. The only two answers are yes and no - there is no third state, " +
                "and inventing one here would be the document claiming a rule the code cannot express.");

            var verdict = cells[3];
            Assert.True(verdict is "SUPERVISED" or "HUMAN-FACING",
                $"The supervision table in {DocRelativePath} has the verdict \"{verdict}\" for " +
                $"{cells[0]}/{cells[1]}/{supervisor}. The only two answers are SUPERVISED and HUMAN-FACING - " +
                "a third word means the document is saying something this rule cannot express.");

            rows.Add(new Row(cells[0], cells[1], supervisor == "yes", verdict == "SUPERVISED"));
        }

        Assert.NotEmpty(rows);
        // A case named twice, with two different verdicts, would let the coverage check pass while the
        // document contradicted itself. One row per case.
        Assert.Equal(rows.Count, rows.Select(r => (r.Role, r.Origin, r.LiveSupervisor)).Distinct().Count());
        return rows;
    }

    /// <summary>
    /// The text of every <c>&lt;th&gt;</c> or <c>&lt;td&gt;</c> cell in one fragment, tags stripped and the
    /// handful of entities this document uses resolved, so a cell written as <c>&lt;strong&gt;yes&lt;/strong&gt;</c>
    /// reads as "yes" rather than failing the vocabulary check on its own markup.
    /// </summary>
    private static List<string> Cells(string fragment, string tag) =>
        // The tag name must END here - "<th[^>]*>" also matches "<thead>", which made the header read
            // "<tr><th>Resolved role" and only looked right because tag-stripping tidied it up.
            Regex.Matches(fragment, $@"<{tag}(?:\s[^>]*)?>(.*?)</{tag}>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Select(raw =>
            {
                // A CELL IS PLAIN TEXT, and this refusal is the point rather than a parsing convenience.
                // Stripping tags would read <s>SUPERVISED</s> as "SUPERVISED" - the machine seeing a rule
                // the reader sees struck through. Any markup at all in a verdict cell fails.
                Assert.True(raw.IndexOf('<') < 0,
                    $"A cell of the supervision table in {DocRelativePath} contains markup: \"{raw.Trim()}\". " +
                    "Cells must be plain text, because markup can strike out, hide or qualify a word that " +
                    "this guard would still read at face value.");
                return raw.Replace("&amp;", "&").Replace("&nbsp;", " ").Trim();
            })
            .ToList();

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// The repository root, located from this source file's own path - the pattern the other document guards
    /// in this repository already use (<c>WorkflowStoreTests</c>, <c>SpokenPhraseTests</c>), because the
    /// suites are run with <c>dotnet test</c> from a checkout and a bin-relative path breaks under different
    /// runners.
    ///
    /// WHAT THIS DOES NOT COVER, said plainly rather than left to be discovered. <c>CallerFilePath</c> is
    /// baked in at COMPILE time, so it names the tree this assembly was BUILT from, not one sitting beside
    /// the assembly as it runs. Copy the built binaries to another machine and the path is simply absent -
    /// which fails, loudly, and is the safe direction. The unsafe direction is narrow but real: build here,
    /// then change the checkout underneath, and this reads that checkout rather than the code under test. It
    /// can only pass WRONGLY when the other tree's table happens to agree with this assembly's rule, in
    /// which case there was nothing to report anyway.
    ///
    /// The check below closes the remaining gap - a path that exists but is not this repository at all. It
    /// asserts both files this guard is about are present, so a stale or unrelated directory fails with the
    /// path named instead of being read as though it were the product.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/SupervisionRuleMatchesTheDesignDocumentTests.cs
        var dir = Path.GetDirectoryName(thisFile)!;
        var root = Path.GetFullPath(Path.Combine(dir, "..", ".."));

        foreach (var marker in new[]
                 {
                     Path.Combine("src", "CcDirector.Gateway.Contracts", "SessionOrdering.cs"),
                     Path.Combine("docs", "new_architecture", "sessions.html"),
                 })
        {
            Assert.True(File.Exists(Path.Combine(root, marker)),
                $"Resolved the repository root to {root}, but it does not contain {marker}. This guard was " +
                "built from a tree it can no longer find, so it is not reading the product - it is reading " +
                "whatever happens to sit at that path. Run the suites from a checkout.");
        }

        return root;
    }
}
