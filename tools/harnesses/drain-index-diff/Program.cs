using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.ControlApi.Drain;
using CcDirector.Gateway.Contracts;

namespace DrainIndexDiff;

/// <summary>
/// THE PHASE 4 PROOF (issue #2723): seed a TEST DIRECTOR to look like DevThrottle_1 did on 2026-09-06,
/// run the REAL drain over it, and compare the index it produces against the one written BY HAND during
/// that first run, field by field.
///
/// It is a harness rather than a test because the hand-written index sits in the owner's vault and does
/// not belong in the repository. It takes it as a path.
///
///   dotnet run --project tools/harnesses/drain-index-diff --
///       --index &lt;the hand-written index.json&gt;
///       [--out &lt;report path&gt;] [--seed-secret]
///
/// WHAT IS SEEDED AND WHAT IS UNDER TEST - read this before believing the number at the bottom.
///
/// SEEDED, from the hand-written index, because these are the two steps that need a language model and
/// this phase deliberately leaves them with one:
///
///   - each seat's handover DOCUMENT (its existence, at the path the drain names);
///   - each seat's own DECLARATION: drained or blocked, restore yes or no and why, which subordinates its
///     document covers, and the questions it is leaving on the owner. In the real run those facts were
///     spoken by the seats and read by a human. Here they are replayed from what those seats said.
///
/// UNDER TEST, entirely - none of it is copied from the index:
///
///   - WHO IS MESSAGED. The chain is rebuilt from reportsTo, and the drain messages the heads.
///   - THE COVERED SEATS. The hand index says four seats were covered; the drain is told nothing of the
///     kind. It learns it from the senior's "covered:" lines and CHECKS each claim against the tree.
///   - THE CLOSE ORDER, and that nothing closed before its subordinates.
///   - EVERY handoverPath, including a covered seat's, which must point at its SENIOR's document.
///   - closedAtUtc, which is written only after the session is verified genuinely absent.
///   - restoreAfterRestart: which seats, and in what order.
///   - THE SECRET SWEEP over the real documents, and the proof it could have failed.
///   - The integrity verdict, and whether a restart may proceed.
///
/// So a difference in a seeded field means the replay is wrong; a difference in a derived field means the
/// DRAIN is wrong, or is deliberately better. The report says which for each.
///
/// --seed-secret runs the whole thing again with a fake credential planted in one document, and the run
/// must come back NOT READY with that document named. A sweep that has never been seen to fail is not a
/// sweep, and this is where it is seen to fail on real input rather than on its own control string.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions DocumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Run the harness.</summary>
    /// <param name="args">--index, and optionally --out and --seed-secret.</param>
    public static async Task<int> Main(string[] args)
    {
        string? indexPath = null;
        string? outPath = null;
        var seedSecret = false;
        var prove = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index" when i + 1 < args.Length: indexPath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--seed-secret": seedSecret = true; break;
                case "--prove": prove = true; break;
            }
        }

        if (indexPath is null || !File.Exists(indexPath))
        {
            Console.Error.WriteLine(
                "usage: --index <the hand-written index.json> [--out <report>] [--seed-secret] [--prove]");
            return 2;
        }

        var hand = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        var report = new StringBuilder();
        report.AppendLine("DRAIN INDEX DIFF - the real drain, over a Director seeded from the hand-written index");
        report.AppendLine("====================================================================================");
        report.AppendLine($"hand-written index : {indexPath}");
        report.AppendLine($"run at             : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        var differences = 0;
        var run = await RunDrainAsync(hand, seedSecret: false);
        try
        {
            report.AppendLine("1. WHAT THE DRAIN DID");
            report.AppendLine("---------------------");
            report.AppendLine($"  seats                 : {run.Result.Document.Seats.Count}");
            report.AppendLine($"  drain messages sent   : {run.Sessions.Sent.Count(m => m.Text.Contains("START NOTHING NEW"))}");
            report.AppendLine($"  seats flagged to close: {run.Sessions.Flagged.Count}");
            report.AppendLine($"  documents swept       : {run.Result.Document.Integrity?.DocumentsSwept}");
            report.AppendLine($"  sweep patterns proved : {run.Result.Document.Integrity?.SweepPatternsProved}" +
                              $" of {run.Result.Document.Integrity?.SweepPatternsTotal}");
            report.AppendLine($"  ready to restart      : {run.Result.ReadyToRestart}");
            if (run.Result.NotReadyReason is not null)
                report.AppendLine($"  not ready because     : {run.Result.NotReadyReason}");
            report.AppendLine();

            differences += ReportSeatDiff(report, hand, run.Result.Document, run.HandoverPaths);
            differences += ReportDocumentDiff(report, hand, run.Result.Document);
            ReportCloseOrder(report, run);
            ReportDeliberateDifferences(report);

            if (seedSecret)
                await ReportSecretRunAsync(report, hand);

            if (prove)
                await ReportProveRunAsync(report, hand);
        }
        finally
        {
            run.Dispose();
        }

        report.AppendLine();
        report.AppendLine($"TOTAL UNEXPLAINED DIFFERENCES: {differences}");
        report.AppendLine("(A difference listed under \"deliberate\" below is not counted here. Everything");
        report.AppendLine(" counted here is a bug or a gap and has to be looked at.)");

        var text = report.ToString();
        Console.WriteLine(text);
        if (outPath is not null)
        {
            await File.WriteAllTextAsync(outPath, text);
            Console.WriteLine($"written to {outPath}");
        }
        return differences == 0 ? 0 : 1;
    }

    // ===================== seed and run =====================

    private sealed class Run : IDisposable
    {
        public required HarnessSessions Sessions { get; init; }
        public required HarnessSink Sink { get; init; }
        public required DirectorDrainResult Result { get; init; }
        public required string Directory { get; init; }
        public required Dictionary<string, string> HandoverPaths { get; init; }

        public void Dispose()
        {
            try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
            catch (IOException) { }
        }
    }

    /// <summary>How a run is deliberately spoiled, so the comparison can be watched failing.</summary>
    private enum Spoil
    {
        /// <summary>Nothing spoiled - the honest run.</summary>
        None,
        /// <summary>A fake credential planted in one real handover.</summary>
        Secret,
        /// <summary>Every seat writes its own document, INCLUDING the four that reported up. The drain
        /// must then record those four "drained" rather than "covered", and the comparison must say so.
        /// This is the harness being run against a known-bad input: a report of zero differences means
        /// nothing until the thing producing it has been seen to produce a non-zero one.</summary>
        NobodyReportsUp,
    }

    private static async Task<Run> RunDrainAsync(JsonObject hand, bool seedSecret)
        => await RunDrainAsync(hand, seedSecret ? Spoil.Secret : Spoil.None);

    private static async Task<Run> RunDrainAsync(JsonObject hand, Spoil spoil)
    {
        var seedSecret = spoil == Spoil.Secret;
        var dir = Path.Combine(Path.GetTempPath(), "drain-index-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var handSeats = ((hand["sessions"] as JsonArray) ?? new JsonArray())
            .Select(n => n!.AsObject()).ToList();

        // The captured document, folded from the hand-written seats through the SAME WorkspaceCapture the
        // Gateway runs. This is the roster the drain sees - and it carries NO judgments: no drain state,
        // no handover path, no restore decision. Everything the drain writes into those, it derived.
        var captured = new WorkspaceDocument
        {
            SchemaVersion = 1,
            Id = "drain-index-diff",
            Name = "drain index diff",
            Origin = WorkspaceOrigins.Captured,
            Machine = Str(hand["machine"]),
            DirectorId = Str(hand["directorId"]),
            DirectorName = Str(hand["directorName"]),
            DirectorVersionBefore = Str(hand["directorVersionBefore"]),
            Outcome = WorkspaceOutcomes.Draining,
            Seats = handSeats.Select((s, i) => WorkspaceCapture.CaptureSeat(ToSessionDto(s), i)).ToList(),
        };

        var sessions = new HarnessSessions { PollsBeforeReap = 1 };
        foreach (var seat in captured.Seats) sessions.Live.Add(seat.SessionId!);

        // Seed each seat's own document and declaration, exactly as that seat declared it in the real run.
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var questionsBySeat = ((hand["ownerQuestions"] as JsonArray) ?? new JsonArray())
            .Select(n => n!.AsObject())
            .GroupBy(o => Str(o["fromSessionId"]) ?? "")
            .ToDictionary(g => g.Key, g => g.Select(o => Str(o["question"]) ?? "").ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var handSeat in handSeats)
        {
            var id = Str(handSeat["sessionId"])!;
            var state = Str(handSeat["drainState"]);
            if (spoil == Spoil.NobodyReportsUp && state == WorkspaceDrainStates.Covered)
                state = WorkspaceDrainStates.Drained;

            // A COVERED seat wrote nothing. That is the whole point of the state, and seeding a file for
            // it here would prove the opposite of what this is checking - which is exactly what
            // Spoil.NobodyReportsUp deliberately does, to watch the comparison fire.
            if (state == WorkspaceDrainStates.Covered && spoil != Spoil.NobodyReportsUp) continue;

            var covered = spoil == Spoil.NobodyReportsUp
                ? new List<(string Id, string Note)>()
                : handSeats
                    .Where(o => Str(o["drainState"]) == WorkspaceDrainStates.Covered
                                && Str(o["reportsTo"]) == id)
                    .Select(o => (Id: Str(o["sessionId"])!, Note: Str(o["coveredNote"]) ?? ""))
                    .ToList();

            var restore = Str(handSeat["restore"]?.AsObject()["decision"]) == WorkspaceRestoreDecisions.Restore;
            var why = Str(handSeat["restore"]?.AsObject()["why"]);
            questionsBySeat.TryGetValue(id, out var questions);

            var body = SeatBody(Str(handSeat["name"]) ?? id, why);
            if (seedSecret && handSeats.IndexOf(handSeat) == 0)
                body += "\n\nThe virtual machine password: Zx9kkQQmm44rrSS\n";

            // QUEUED, not written. The drain refuses to start over a directory that already holds
            // documents - at start time a file for one of its own seats is indistinguishable from a stale
            // one left by a run cancelled a minute earlier - so the harness writes them when the drain
            // message goes out, which is also simply what happens.
            var path = DrainPaths.HandoverFor(dir, id, Str(handSeat["name"]));
            sessions.Queue(path, body + "\n\n" + Block(
                state ?? WorkspaceDrainStates.Drained, restore, why, covered, questions));
            paths[id] = path;
        }

        var sink = new HarnessSink { Captured = captured };
        var now = DateTime.UtcNow;
        var drain = new DirectorDrain(sessions, sink, Str(hand["directorId"]) ?? "director", null,
            utcNow: () => now,
            delay: (d, _) => { now = now.Add(d); return Task.CompletedTask; });

        var result = await drain.RunAsync(new DrainOptions
        {
            WorkspaceId = "drain-index-diff",
            WorkspaceName = "drain index diff",
            Reason = Str(hand["reason"]),
            HandoverDeadline = TimeSpan.FromMinutes(90),
            PollInterval = TimeSpan.FromSeconds(10),
        }, dir);

        return new Run
        {
            Sessions = sessions, Sink = sink, Result = result, Directory = dir, HandoverPaths = paths,
        };
    }

    private static string SeatBody(string name, string? why) => $"""
        # Handover - {name}

        ## Where this seat is

        Replayed from the hand-written index of the first real drain on 2026-09-06. The prose here is
        not that document's prose - only the FACTS the seat declared are replayed, because those are the
        inputs the drain acts on. This body exists so the file is a real document rather than a stub.

        ## What is proven and what is only believed

        Proven: nothing in this file. It is a harness fixture.

        ## The exact next action

        {why ?? "Named in the block below."}
        """;

    private static string Block(
        string state, bool restore, string? why,
        List<(string Id, string Note)> covered, List<string>? questions)
    {
        var lines = new List<string> { "<!-- drain-report", $"state: {state}" };
        lines.Add($"restore: {(restore ? "yes" : "no")}");
        if (why is not null) lines.Add($"why: {OneLine(why)}");
        foreach (var (id, note) in covered) lines.Add($"covered: {id} | {OneLine(note)}");
        foreach (var q in questions ?? new List<string>()) lines.Add($"question: {OneLine(q)}");
        lines.Add("-->");
        return string.Join("\n", lines);
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    // ===================== the comparison =====================

    private static int ReportSeatDiff(
        StringBuilder report, JsonObject hand, WorkspaceDocument drained, Dictionary<string, string> paths)
    {
        report.AppendLine("2. SEAT BY SEAT, FIELD BY FIELD");
        report.AppendLine("-------------------------------");

        var handSeats = ((hand["sessions"] as JsonArray) ?? new JsonArray())
            .Select(n => n!.AsObject())
            .ToDictionary(o => Str(o["sessionId"])!, o => o, StringComparer.OrdinalIgnoreCase);

        var differences = 0;
        var naming = new List<string>();
        string? driverNote = null;
        foreach (var seat in drained.Seats.OrderBy(s => s.SortOrder))
        {
            if (!handSeats.TryGetValue(seat.SessionId!, out var h))
            {
                report.AppendLine($"  EXTRA {DrainPaths.ShortId(seat.SessionId)}: the drain produced a seat " +
                                  "the hand-written index does not have.");
                differences++;
                continue;
            }

            differences += Compare(report, seat.SessionId!, "drainState",
                Str(h["drainState"]), seat.DrainState, derived: true);
            differences += Compare(report, seat.SessionId!, "restore.decision",
                Str(h["restore"]?.AsObject()["decision"]), seat.Restore?.Decision, derived: false);
            differences += Compare(report, seat.SessionId!, "coveredBySeatIsInTheSameTree",
                Str(h["reportsTo"]) is null || Str(h["drainState"]) != WorkspaceDrainStates.Covered
                    ? null : Str(h["reportsTo"]),
                seat.DrainState == WorkspaceDrainStates.Covered ? seat.CoveredBy : null, derived: true);

            // handoverPath. The hand-written index carries an absolute path in the owner's vault and this
            // run wrote into a temporary directory, so only the FILE NAME can be compared - and the
            // hand-written names are not a reliable oracle: a person typed them, and four of the eighteen
            // do not match the session they belong to (a dropped prefix, a shortened name, a colon left
            // in). What IS checked, and is the thing that matters, is that the drain's path is the one
            // DrainPaths computes from the seat's own captured name. That is exactly the property a seat
            // depends on: the drain tells it where to write and then watches that path, so a name the two
            // compute differently is a seat that looks unreachable while its document sits on disk.
            var expectedName = seat.DrainState == WorkspaceDrainStates.Covered && seat.CoveredBy is not null
                ? DrainPaths.HandoverFileName(seat.CoveredBy, NameOf(drained, seat.CoveredBy))
                : DrainPaths.HandoverFileName(seat.SessionId!, seat.Name);
            var drainFile = seat.HandoverPath is not null ? Path.GetFileName(seat.HandoverPath) : null;
            differences += Compare(report, seat.SessionId!, "handoverPath is derived from the seat's own name",
                expectedName, drainFile, derived: true);

            var handFile = Str(h["handoverPath"]) is string hp ? Path.GetFileName(hp) : null;
            if (!string.Equals(handFile, drainFile, StringComparison.Ordinal))
                naming.Add($"    {DrainPaths.ShortId(seat.SessionId)}  person wrote '{handFile}'  " +
                           $"drain writes '{drainFile}'");

            var handClosed = Str(h["closedAtUtc"]) is not null;
            var drainClosed = seat.ClosedAtUtc is not null;
            if (handClosed != drainClosed)
            {
                // THE ONE EXPECTED DISAGREEMENT, and it is the point of the whole phase. The seat that
                // differs is the one that DROVE the hand-written drain: a session, sitting on the Director
                // it was draining, which could not close itself and died at the restart instead - taking
                // the restore with it. The drain now runs inside the Director, so there is no such seat and
                // it closes this one like any other. Named by id against the index's own drivenBySessionId
                // rather than waved through as a category.
                if (!handClosed && drainClosed
                    && string.Equals(seat.SessionId, Str(hand["drivenBySessionId"]), StringComparison.OrdinalIgnoreCase))
                {
                    driverNote = $"    {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) drove the " +
                                 "hand-written drain from a session ON the target, so it never closed " +
                                 "itself. The drain is not a session, so it closes this seat like any other.";
                }
                else
                {
                    report.AppendLine($"  DIFFERS {DrainPaths.ShortId(seat.SessionId)} closedAtUtc: " +
                                      $"hand-written {(handClosed ? "closed" : "not closed")} vs drain " +
                                      $"{(drainClosed ? "closed" : "not closed")}  [DERIVED - look at this]");
                    differences++;
                }
            }
        }

        foreach (var id in handSeats.Keys)
            if (!drained.Seats.Any(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase)))
            {
                report.AppendLine($"  MISSING {DrainPaths.ShortId(id)}: in the hand-written index, not in the drain.");
                differences++;
            }

        report.AppendLine($"  {drained.Seats.Count} seat(s) compared; " +
                          $"{drained.Seats.Count(x => x.DrainState == WorkspaceDrainStates.Covered)} covered, " +
                          $"{drained.Seats.Count(x => x.ClosedAtUtc is not null)} closed, " +
                          $"{drained.Seats.Count(x => x.Restore?.Decision == WorkspaceRestoreDecisions.Restore)} " +
                          "marked for restore.");

        if (naming.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"  NAMING - {naming.Count} document(s) the person named differently from the");
            report.AppendLine("  session they belong to. NOT a drain difference: the drain's path is derived");
            report.AppendLine("  from the seat's own name, which is the property checked above. Listed because");
            report.AppendLine("  a reader comparing the two directories by eye will otherwise wonder.");
            foreach (var n in naming) report.AppendLine(n);
        }

        if (driverNote is not null)
        {
            report.AppendLine();
            report.AppendLine("  THE DRIVER SEAT - one expected disagreement about closing:");
            report.AppendLine(driverNote);
        }

        report.AppendLine();
        return differences;
    }

    private static string? NameOf(WorkspaceDocument doc, string sessionId)
        => doc.Seats.FirstOrDefault(s =>
            string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))?.Name;

    private static int ReportDocumentDiff(StringBuilder report, JsonObject hand, WorkspaceDocument drained)
    {
        report.AppendLine("3. THE DOCUMENT ITSELF");
        report.AppendLine("----------------------");

        var differences = 0;

        var handRestore = ((hand["restoreAfterRestart"] as JsonArray) ?? new JsonArray())
            .Select(n => n!.GetValue<string>()).ToList();
        var drainRestore = drained.RestoreAfterRestart;

        if (!handRestore.OrderBy(x => x).SequenceEqual(drainRestore.OrderBy(x => x)))
        {
            report.AppendLine("  DIFFERS restoreAfterRestart  [DERIVED - look at this]");
            report.AppendLine($"    hand-written: {string.Join(", ", handRestore.Select(DrainPaths.ShortId))}");
            report.AppendLine($"    drain       : {string.Join(", ", drainRestore.Select(DrainPaths.ShortId))}");
            differences++;
        }
        else
        {
            report.AppendLine($"  restoreAfterRestart: same {handRestore.Count} seat(s). " +
                              $"Order hand-written [{string.Join(", ", handRestore.Select(DrainPaths.ShortId))}], " +
                              $"drain [{string.Join(", ", drainRestore.Select(DrainPaths.ShortId))}] " +
                              "(the drain orders seniors first, so a restore walks down the tree).");
        }

        var handQuestions = ((hand["ownerQuestions"] as JsonArray) ?? new JsonArray()).Count;
        if (handQuestions != drained.OwnerQuestions.Count)
        {
            report.AppendLine($"  DIFFERS ownerQuestions: hand-written {handQuestions} vs drain " +
                              $"{drained.OwnerQuestions.Count}  [DERIVED - look at this]");
            differences++;
        }
        else
        {
            report.AppendLine($"  ownerQuestions: {handQuestions} rolled up, attributed to the seats that asked.");
        }

        report.AppendLine();
        return differences;
    }

    private static void ReportCloseOrder(StringBuilder report, Run run)
    {
        report.AppendLine("4. THE CLOSE ORDER (the hand-written index cannot be compared here - it records");
        report.AppendLine("   close TIMES, and this run's clock is injected. What is checked is the RULE.)");
        report.AppendLine("   ---------------------------------------------------------------------------");

        var chain = DrainChain.Build(run.Result.Document.Seats);
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < run.Sessions.Flagged.Count; i++) position[run.Sessions.Flagged[i]] = i;

        var violations = 0;
        foreach (var node in chain.Nodes)
        {
            if (!position.TryGetValue(node.SessionId, out var senior)) continue;
            foreach (var sub in node.Subordinates)
            {
                if (!position.TryGetValue(sub, out var junior)) continue;
                if (junior >= senior)
                {
                    report.AppendLine($"  VIOLATION: {DrainPaths.ShortId(node.SessionId)} was asked to close " +
                                      $"before {DrainPaths.ShortId(sub)}, which reports to it.");
                    violations++;
                }
            }
        }
        report.AppendLine(violations == 0
            ? $"  {run.Sessions.Flagged.Count} seat(s) asked to close, none before a seat reporting to it."
            : $"  {violations} violation(s).");
        report.AppendLine();
    }

    private static void ReportDeliberateDifferences(StringBuilder report)
    {
        report.AppendLine("5. DIFFERENCES THAT ARE DELIBERATE, AND WHY");
        report.AppendLine("-------------------------------------------");
        foreach (var line in new[]
        {
            "outcome: the hand-written index says \"restarted-and-restored\", which is not one of the " +
            "schema's values. The drain writes \"drained\" and stops there, because Phase 4 ends before " +
            "the restart. \"restarted\" and \"restored\" are written later, by Phase 5.",

            "coveredBy: the hand-written index names the covering seat by its DOCUMENT FILE NAME " +
            "(\"9cec65de - New Studio Cube - Manager R-D\"). The drain writes the covering seat's SESSION " +
            "ID. The file is already in handoverPath, and an id is the thing a later reader can act on.",

            "restore.command: the hand-written index left \"--director <NEW>\" and a described seed. The " +
            "drain builds the whole command from the seat's captured facts and leaves the same two " +
            "placeholders, because the new Director id does not exist until the restart and the seed file " +
            "is written by the restore. An id that looked real and was stale would be worse than a " +
            "placeholder that is obviously one.",

            "restore.seedPrompt: the hand-written index carries an inline seed prompt on two entries. The " +
            "schema has no such field and the drain writes none - a long inline prompt parks in the " +
            "agent's composer unsubmitted. The seed belongs in a FILE, which is Phase 5's job, and the " +
            "seat's restoredSeedFile is where it is named.",

            "integrity: a block the hand-written index does not have at all. It records the index checks, " +
            "the sweep findings without the secrets, and HOW MANY PATTERNS WERE PROVED ABLE TO FIRE " +
            "before the sweep ran - so a later reader can tell a real clean sweep from an unrun one " +
            "without taking anybody's word for it.",

            "restoreAfterRestart ORDER: the hand-written list is in the order the driver decided them. The " +
            "drain orders seniors first, so a restore can point each seat at its senior's NEW id rather " +
            "than at a session that no longer exists.",
        })
        {
            report.AppendLine("  * " + Wrap(line));
        }
        report.AppendLine();
    }

    private static async Task ReportSecretRunAsync(StringBuilder report, JsonObject hand)
    {
        report.AppendLine("6. THE SWEEP, SHOWN FAILING ON A SEEDED DOCUMENT");
        report.AppendLine("------------------------------------------------");
        report.AppendLine("   The same run again, with a fake credential planted in one real handover. A");
        report.AppendLine("   clean result from the run above means nothing unless this one is dirty.");

        var run = await RunDrainAsync(hand, seedSecret: true);
        try
        {
            var integrity = run.Result.Document.Integrity!;
            report.AppendLine($"   ready to restart : {run.Result.ReadyToRestart}   (must be False)");
            report.AppendLine($"   findings         : {integrity.SecretFindings.Count}   (must be at least 1)");
            foreach (var f in integrity.SecretFindings)
                report.AppendLine($"     {Path.GetFileName(f.File)}:{f.Line}  {f.Pattern}  {f.RedactedExcerpt}");
            report.AppendLine($"   the record carries the secret: " +
                              (JsonSerializer.Serialize(run.Result.Document, DocumentOptions)
                                  .Contains("Zx9kkQQmm44rrSS") ? "YES - THAT IS A DEFECT" : "no"));
            report.AppendLine($"   verdict          : " +
                              (!run.Result.ReadyToRestart && integrity.SecretFindings.Count > 0
                                  ? "THE SWEEP FIRED. The clean result above is evidence."
                                  : "THE SWEEP DID NOT FIRE. Nothing above is evidence."));
        }
        finally
        {
            run.Dispose();
        }
        report.AppendLine();
    }

    private static async Task ReportProveRunAsync(StringBuilder report, JsonObject hand)
    {
        report.AppendLine("7. THE COMPARISON ITSELF, SHOWN FIRING ON A KNOWN-BAD RUN");
        report.AppendLine("---------------------------------------------------------");
        report.AppendLine("   \"Zero differences\" is an ABSENCE, and an absence is what a broken comparison");
        report.AppendLine("   also produces. So the same drain is run again over a spoiled seeding - every");
        report.AppendLine("   seat writes its own document, including the four that reported up - and the");
        report.AppendLine("   comparison must come back with a non-zero count naming those four.");

        var run = await RunDrainAsync(hand, Spoil.NobodyReportsUp);
        try
        {
            var scratch = new StringBuilder();
            var differences = ReportSeatDiff(scratch, hand, run.Result.Document, run.HandoverPaths)
                              + ReportDocumentDiff(scratch, hand, run.Result.Document);

            report.AppendLine($"   differences on the spoiled run : {differences}   (must be at least 4)");
            foreach (var line in scratch.ToString().Split('\n')
                         .Where(l => l.Contains("DIFFERS")).Take(8))
                report.AppendLine("     " + line.Trim());
            report.AppendLine("   verdict : " + (differences >= 4
                ? "THE COMPARISON FIRED. The zero above is evidence."
                : "THE COMPARISON DID NOT FIRE. Nothing above is evidence."));
        }
        finally
        {
            run.Dispose();
        }
        report.AppendLine();
    }

    // ===================== plumbing =====================

    private static int Compare(
        StringBuilder report, string sessionId, string field, string? hand, string? drain, bool derived)
    {
        if (string.Equals(hand ?? "", drain ?? "", StringComparison.Ordinal)) return 0;
        report.AppendLine($"  DIFFERS {DrainPaths.ShortId(sessionId)} {field}: hand-written '{hand}' vs " +
                          $"drain '{drain}'  [{(derived ? "DERIVED - look at this" : "SEEDED - the replay is wrong")}]");
        return 1;
    }

    private static string Wrap(string s)
    {
        var sb = new StringBuilder();
        var width = 0;
        foreach (var word in s.Split(' '))
        {
            if (width + word.Length > 92) { sb.AppendLine().Append("    "); width = 4; }
            sb.Append(word).Append(' ');
            width += word.Length + 1;
        }
        return sb.ToString().TrimEnd();
    }

    private static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static SessionDto ToSessionDto(JsonObject hand)
    {
        var mission = hand["mission"]?.AsObject();
        var state = hand["stateAtDrain"]?.AsObject();
        var model = hand["model"];

        return new SessionDto
        {
            SessionId = Str(hand["sessionId"]) ?? "",
            Name = Str(hand["name"]),
            Agent = Str(hand["agent"]) ?? "",
            RepoPath = Str(hand["repoPath"]) ?? "",
            CurrentModel = model is JsonValue v && v.TryGetValue<string>(out var id) ? id : null,
            ModelDisplay = model is JsonObject mo
                ? new ModelDisplay
                {
                    Kind = Str(mo["kind"]) ?? "",
                    Text = Str(mo["text"]) ?? "",
                    ModelId = Str(mo["modelId"]),
                    Tooltip = Str(mo["tooltip"]) ?? "",
                    IsAbsent = mo["isAbsent"]?.GetValue<bool>() ?? false,
                }
                : null,
            MissionId = Guid.TryParse(Str(mission?["id"]), out var mid) ? mid : null,
            MissionName = Str(mission?["name"]),
            SessionRole = Str(hand["role"]),
            ControllerSessionId = Str(hand["reportsTo"]),
            IsControlled = Str(hand["reportsTo"]) is not null,
            WorkflowRunId = Guid.TryParse(Str(hand["workflowRunId"]), out var wid) ? wid : null,
            Status = Str(state?["status"]) ?? "",
            ActivityState = Str(state?["activityState"]) ?? "",
            StateLabel = Str(state?["stateLabel"]),
            TurnCount = state?["turnCount"]?.GetValue<int>(),
            UncommittedCount = state?["uncommittedCount"]?.GetValue<int>(),
            ClaudeSessionId = Str(hand["claudeSessionId"]),
            ClaudeTranscriptPath = Str(hand["claudeTranscriptPath"]),
            CreatedAt = DateTime.TryParse(Str(hand["createdAt"]), out var created) ? created : default,
        };
    }
}

/// <summary>The seeded Director: the eighteen session ids, live, and a record of what was done to them.</summary>
internal sealed class HarnessSessions : IDrainSessionControl
{
    private readonly Dictionary<string, int> _countdown = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Live { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string SessionId, string Text)> Sent { get; } = new();
    public List<string> Flagged { get; } = new();
    public int PollsBeforeReap { get; set; } = 1;

    public bool IsPresent(string sessionId)
    {
        if (!Live.Contains(sessionId)) return false;
        if (!_countdown.TryGetValue(sessionId, out var left)) return true;
        if (left <= 0) { Live.Remove(sessionId); return false; }
        _countdown[sessionId] = left - 1;
        return true;
    }

    private readonly List<(string Path, string Text)> _queued = new();
    private bool _responded;

    /// <summary>Queue a seat's handover, to be written WHEN THE DRAIN MESSAGE GOES OUT. Not before: the
    /// drain refuses to start over a directory that already holds documents, because at start time a file
    /// for one of its own seats is indistinguishable from a stale one left by a run cancelled a minute
    /// earlier. Writing on the message is also simply what happens.</summary>
    public void Queue(string path, string text) => _queued.Add((path, text));

    public Task<bool> SendAsync(string sessionId, string text)
    {
        if (!Live.Contains(sessionId)) return Task.FromResult(false);
        Sent.Add((sessionId, text));

        if (!_responded && text.Contains("START NOTHING NEW"))
        {
            _responded = true;
            foreach (var (path, content) in _queued) File.WriteAllText(path, content);
        }

        return Task.FromResult(true);
    }

    public bool Rename(string sessionId, string name) => Live.Contains(sessionId);

    /// <summary>Every id in the hand-written index is a real session GUID, so the harness can address
    /// them all - and the drain's roster and the live set agree, which is what it checks at the end.</summary>
    public bool CanDrive(string? sessionId) => !string.IsNullOrWhiteSpace(sessionId);

    public IReadOnlyList<string> LiveSessionIds() => Live.ToList();

    public bool MarkForDeletion(string sessionId, string reason)
    {
        if (!Live.Contains(sessionId)) return false;
        Flagged.Add(sessionId);
        _countdown[sessionId] = PollsBeforeReap;
        return true;
    }
}

/// <summary>The seeded Gateway: hands back the capture, keeps every save.</summary>
internal sealed class HarnessSink : IDrainWorkspaceSink
{
    public WorkspaceDocument Captured { get; set; } = new();
    public List<WorkspaceDocument> Saves { get; } = new();

    public Task<WorkspaceDocument> CaptureAsync(WorkspaceCaptureRequest request, CancellationToken ct)
        => Task.FromResult(Captured);

    public Task<WorkspaceDocument> SaveAsync(WorkspaceDocument doc, CancellationToken ct)
    {
        Saves.Add(doc);
        return Task.FromResult(doc);
    }
}
