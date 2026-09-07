using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;

namespace WorkspaceCaptureDiff;

/// <summary>
/// Runs the workspace CAPTURE over a real fleet roster and diffs the result against the restart index
/// that was written BY HAND on 2026-09-06 (issue #2722).
///
/// This is the proof the phase owes, and it is a harness rather than a test because the two inputs are
/// private to the machine: the roster is this fleet's live sessions, and the hand-written index sits in
/// the owner's vault. Neither belongs in the repository, so the check takes both as paths.
///
/// What it answers, and nothing more:
///
///  - FIELD PARITY. Every field the hand-written index carries, at the document level and on a seat, is
///    either produced by the capture, or NAMED as a gap. A gap is not a failure - some of those fields
///    are judgments a capture must not make - but it has to be said out loud rather than dropped.
///  - VALUE PARITY. For any session that is in BOTH the roster and the hand-written index, every mapped
///    field is compared and any difference is printed.
///
/// What it does NOT answer: whether the Gateway stores what the fold produced (WorkspaceStoreTests), and
/// whether the routes carry it (WorkspaceEndpointTests). It exercises the fold and the schema.
///
///   dotnet run --project tools/harnesses/workspace-capture-diff --
///       --roster  &lt;cc-devthrottle session list --json output&gt;
///       --index   &lt;the hand-written index.json&gt;
///       [--director &lt;id&gt;]   [--out &lt;report path&gt;]
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions RosterOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions DocumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Which field of the hand-written index each captured field answers.
    ///
    /// Written down rather than inferred by name, because two of these are exactly where a silent
    /// mismatch would hide: the hand-written index calls the seat list "sessions" where a workspace calls
    /// it "seats", and it overloads one "model" field with two different shapes.
    /// </summary>
    private static readonly (string HandWritten, string Captured, string Note)[] DocumentMap =
    {
        ("schemaVersion", "schemaVersion", ""),
        ("startedAtUtc", "startedAtUtc", ""),
        ("completedAtUtc", "completedAtUtc", "written back when the run finishes"),
        ("machine", "machine", ""),
        ("directorId", "directorId", ""),
        ("directorName", "directorName", ""),
        ("directorVersionBefore", "directorVersionBefore", ""),
        ("directorVersionAfter", "directorVersionAfter", "written back after the restart"),
        ("drivenBySessionId", "drivenBySessionId", ""),
        ("drivenByDirectorId", "drivenByDirectorId", ""),
        ("drivenByNote", "drivenByNote", ""),
        ("reason", "reason", ""),
        ("outcome", "outcome", ""),
        ("sessions", "seats", "renamed: a workspace holds SEATS, which is what they are before and after"),
        ("ownerQuestions", "ownerQuestions", "collected from handovers, not from a capture"),
        ("restoreAfterRestart", "restoreAfterRestart", "decided by a reader of the handovers"),
        ("restartCommand", "restartCommand", ""),
        ("launcherUpdate", "launcherUpdate", ""),
        ("restartBlocked", "restartBlocked", ""),
        ("restartMechanism", "restartMechanism", ""),
        ("restartPerformed", "restartPerformed", "written back after the restart"),
        ("restoredBy", "restoredBy", "written back after the restore"),
    };

    private static readonly (string HandWritten, string Captured, string Note)[] SeatMap =
    {
        ("sessionId", "sessionId", ""),
        ("name", "name", ""),
        ("agent", "agent", ""),
        ("model", "model", "split: the id here, the folded verdict in modelDisplay"),
        ("repoPath", "repoPath", ""),
        ("mission", "mission", ""),
        ("role", "role", ""),
        ("reportsTo", "reportsTo", ""),
        ("workflowRunId", "workflowRunId", ""),
        ("stateAtDrain", "stateAtDrain", ""),
        ("claudeSessionId", "claudeSessionId", ""),
        ("claudeTranscriptPath", "claudeTranscriptPath", ""),
        ("createdAt", "createdAt", ""),
        ("handoverPath", "handoverPath", "a judgment: written when a handover has been read"),
        ("drainState", "drainState", "a judgment: written when a handover has been read"),
        ("blockedReason", "blockedReason", "a judgment"),
        ("restore", "restore", "a judgment"),
        ("closedAtUtc", "closedAtUtc", "written when the session is verified gone"),
        ("restoredSessionId", "restoredSessionId", "written after the restore"),
        ("restoredSeedFile", "restoredSeedFile", "written after the restore"),
        ("coveredBy", "coveredBy", "a judgment"),
        ("coveredNote", "coveredNote", "a judgment"),
    };

    /// <summary>Entry point.</summary>
    /// <param name="args">Command line arguments; see the class summary.</param>
    public static int Main(string[] args)
    {
        string? rosterPath = null, indexPath = null, directorId = null, outPath = null;
        string? directorName = null, directorVersion = null, drivenBy = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--roster": rosterPath = args[++i]; break;
                case "--index": indexPath = args[++i]; break;
                case "--director": directorId = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--director-name": directorName = args[++i]; break;
                case "--director-version": directorVersion = args[++i]; break;
                case "--driven-by": drivenBy = args[++i]; break;
            }
        }

        if (rosterPath is null || indexPath is null)
        {
            Console.Error.WriteLine(
                "USAGE: --roster <session list --json output> --index <hand-written index.json> " +
                "[--director <id>] [--out <report path>]");
            return 2;
        }

        var sessions = JsonSerializer.Deserialize<List<SessionDto>>(File.ReadAllText(rosterPath), RosterOptions)
                       ?? throw new InvalidOperationException($"the roster at {rosterPath} did not parse");

        var handWritten = JsonNode.Parse(File.ReadAllText(indexPath))?.AsObject()
                          ?? throw new InvalidOperationException($"the index at {indexPath} did not parse");

        directorId ??= sessions
            .GroupBy(s => s.DirectorId)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("the roster names no Director to capture");

        var mine = sessions.Where(s =>
            string.Equals(s.DirectorId, directorId, StringComparison.OrdinalIgnoreCase)).ToList();

        var captured = WorkspaceCapture.Capture(
            new WorkspaceCaptureRequest
            {
                Id = "capture-diff-proof",
                Name = "Capture diff proof",
                DirectorId = directorId,
                Reason = "proof for issue 2722: diff the capture against the hand-written index",
                DrivenBySessionId = drivenBy,
                DrivenByDirectorId = drivenBy is null ? null : directorId,
                DrivenByNote = drivenBy is null ? null : "capture diff harness",
            },
            mine,
            directorName,
            directorVersion,
            machine: null,
            DateTime.UtcNow);

        var report = new StringBuilder();
        report.AppendLine("WORKSPACE CAPTURE DIFF - issue 2722");
        report.AppendLine("===================================");
        report.AppendLine($"roster file      : {rosterPath}");
        report.AppendLine($"hand-written idx : {indexPath}");
        report.AppendLine($"director captured: {directorId}");
        report.AppendLine($"sessions captured: {mine.Count} of {sessions.Count} in the roster");
        report.AppendLine($"seats in the hand-written index: {(handWritten["sessions"] as JsonArray)?.Count ?? 0}");
        report.AppendLine();

        var lost = ReportFieldParity(report, handWritten, captured);
        var valueDifferences = ReportValueParity(report, handWritten, captured);
        var replayDifferences = ReportReplay(report, handWritten);

        report.AppendLine();
        report.AppendLine("VERDICT");
        report.AppendLine("-------");
        report.AppendLine($"hand-written fields the schema has NO HOME for : {lost}");
        report.AppendLine($"value differences on sessions present in both  : {valueDifferences}");
        report.AppendLine($"replay differences over the hand-written seats  : {replayDifferences}");
        report.AppendLine();
        report.AppendLine("NO HOME is the count that is a defect on its face: it means a fact the hand-written");
        report.AppendLine("index carried would be LOST. A field with a home that the capture does not fill is a");
        report.AppendLine("different thing and is named above - usually a judgment nobody has made yet.");

        var text = report.ToString();
        Console.Write(text);
        if (outPath is not null)
        {
            File.WriteAllText(outPath, text);
            Console.WriteLine($"(written to {outPath})");
        }

        return 0;
    }

    /// <summary>
    /// Report field parity, and report it as TWO separate facts, because collapsing them is how this
    /// harness first lied to its own author.
    ///
    ///  - HAS A HOME: the workspace schema carries a field for it. A "no" here is a fact the schema
    ///    LOSES - the thing that must never happen.
    ///  - CAPTURE FILLS IT: the fold actually populates it from live sessions. A "no" here is often
    ///    correct - a handover path or a drain state is a judgment nobody has made yet - but it is
    ///    always named.
    ///
    /// The first version of this method judged only whether the produced JSON carried the key, and the
    /// document is serialised with nulls omitted, so every field this particular run left null read as
    /// "NOT PRODUCED" - including six the capture fills perfectly well when it is given them. A check
    /// that invents gaps is no better than one that hides them.
    /// </summary>
    private static int ReportFieldParity(
        StringBuilder report, JsonObject handWritten, WorkspaceDocument captured)
    {
        var documentSchema = SchemaFields(typeof(WorkspaceDocument));
        var seatSchema = SchemaFields(typeof(WorkspaceSeat));

        // What the fold actually filled in on THIS run, unioned across every seat: one seat that
        // happens to have a mission must not make "mission" look filled for a fleet that has none.
        var documentFilled = FilledFields(JsonSerializer.SerializeToNode(captured, DocumentOptions)!.AsObject());
        var seatFilled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seat in captured.Seats)
            foreach (var f in FilledFields(JsonSerializer.SerializeToNode(seat, DocumentOptions)!.AsObject()))
                seatFilled.Add(f);

        var lost = 0;

        report.AppendLine("1. FIELD PARITY - DOCUMENT LEVEL");
        report.AppendLine("--------------------------------");
        report.AppendLine($"  {"hand-written field",-38} {"schema",-7} {"filled",-6}note");
        lost += ReportLevel(report, DocumentMap, HandFields(handWritten), documentSchema, documentFilled);

        report.AppendLine();
        report.AppendLine("2. FIELD PARITY - SEAT LEVEL");
        report.AppendLine("----------------------------");
        report.AppendLine($"  {"hand-written field",-38} {"schema",-7} {"filled",-6}note");
        var handSeatFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seat in (handWritten["sessions"] as JsonArray) ?? new JsonArray())
            foreach (var kv in seat!.AsObject())
                handSeatFields.Add(kv.Key);
        lost += ReportLevel(report, SeatMap, handSeatFields, seatSchema, seatFilled);

        report.AppendLine();
        report.AppendLine("  Seat fields the capture produces that the hand-written index did not have:");
        var mappedCaptured = SeatMap.Select(m => m.Captured).ToHashSet(StringComparer.Ordinal);
        foreach (var k in seatFilled.Where(k => !mappedCaptured.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            report.AppendLine($"    {k}");

        return lost;
    }

    /// <summary>
    /// One level of the comparison. Returns how many hand-written fields the schema has NO HOME for -
    /// the only count that is a defect on its face.
    /// </summary>
    private static int ReportLevel(
        StringBuilder report,
        (string HandWritten, string Captured, string Note)[] map,
        HashSet<string> handFields,
        HashSet<string> schema,
        HashSet<string> filled)
    {
        var lost = 0;
        foreach (var (hand, cap, note) in map)
        {
            if (!handFields.Contains(hand)) continue;   // the hand-written index never used this one

            var hasHome = schema.Contains(cap);
            if (!hasHome) lost++;

            var suffix = note.Length > 0 ? $"  [{note}]" : "";
            var homeText = hasHome ? "yes" : "NO HOME";
            var filledText = filled.Contains(cap) ? "yes" : "no";
            var arrow = hand == cap ? "" : $" -> {cap}";
            report.AppendLine($"  {hand + arrow,-38} {homeText,-7} {filledText,-6}{suffix}");
        }

        var mapped = map.Select(m => m.HandWritten).ToHashSet(StringComparer.Ordinal);
        var unmapped = handFields.Where(k => !mapped.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unmapped.Count > 0)
        {
            lost += unmapped.Count;
            report.AppendLine();
            report.AppendLine("  UNMAPPED - the hand-written index has these and the schema does not know them:");
            foreach (var k in unmapped) report.AppendLine($"    {k}");
        }

        return lost;
    }

    /// <summary>The camelCase names of every property on a workspace type - what the schema can hold.</summary>
    private static HashSet<string> SchemaFields(Type t) =>
        t.GetProperties()
            .Select(pr => JsonNamingPolicy.CamelCase.ConvertName(pr.Name))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The names actually present (non-null) in one serialised object.</summary>
    private static HashSet<string> FilledFields(JsonObject o) =>
        o.Where(kv => kv.Value is not null).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// REPLAY: rebuild the session records the hand-written index was written FROM, fold each one
    /// through the real capture, and compare every field against what the person wrote down.
    ///
    /// This is what the value comparison above cannot do. Those sessions were destroyed by the very
    /// restart the index records, so no live roster will ever contain one again - and a section that
    /// can only ever report "no overlap" proves nothing at all. Replaying the index's OWN recorded
    /// facts through the fold does prove something specific: that every seat field is read from the
    /// right place and written under the right name, across all eighteen real seats, including the
    /// awkward ones - a mission, a controller, a workflow run, and the model field the hand-written
    /// index gave two different shapes.
    ///
    /// HOW IT FAILS, which is the only thing that makes it a check: it fails when this harness and the
    /// fold DISAGREE about where a fact comes from. Read the role out of the wrong property of a session
    /// and all eighteen seats report a difference - watched, by moving the fold to GroupRole and to the
    /// transcript path and seeing thirty-six differences printed.
    ///
    /// What CANNOT fail it, and is not a check: mutating the index. Both sides of the comparison are
    /// built from the same hand-written seat, so a changed value moves both and the count stays zero.
    /// That was tried first and it reported a clean run over a deliberately corrupted index.
    ///
    /// What it does NOT prove: that the Gateway holds the same value the person saw, because the
    /// input here is the person's own transcription of it. It proves the MAPPING, not the source.
    /// </summary>
    private static int ReportReplay(StringBuilder report, JsonObject handWritten)
    {
        report.AppendLine();
        report.AppendLine("4. REPLAY - the hand-written seats folded back through the capture");
        report.AppendLine("------------------------------------------------------------------");

        var seats = (handWritten["sessions"] as JsonArray) ?? new JsonArray();
        var differences = 0;
        var replayed = 0;

        foreach (var node in seats)
        {
            var hand = node?.AsObject();
            if (hand is null) continue;

            var dto = ToSessionDto(hand);
            var seat = WorkspaceCapture.CaptureSeat(dto, replayed);
            replayed++;

            var id = seat.SessionId ?? "(no id)";
            differences += Compare(report, id, "name", Str(hand["name"]), seat.Name);
            differences += Compare(report, id, "agent", Str(hand["agent"]), seat.Agent);
            differences += Compare(report, id, "repoPath", Str(hand["repoPath"]), seat.RepoPath);
            differences += Compare(report, id, "role", Str(hand["role"]), seat.Role);
            differences += Compare(report, id, "reportsTo", Str(hand["reportsTo"]), seat.ReportsTo);
            differences += Compare(report, id, "workflowRunId", Str(hand["workflowRunId"]), seat.WorkflowRunId);
            differences += Compare(report, id, "claudeSessionId", Str(hand["claudeSessionId"]), seat.ClaudeSessionId);
            differences += Compare(report, id, "claudeTranscriptPath",
                Str(hand["claudeTranscriptPath"]), seat.ClaudeTranscriptPath);

            var mission = hand["mission"]?.AsObject();
            differences += Compare(report, id, "mission.id", Str(mission?["id"]), seat.Mission?.Id);
            differences += Compare(report, id, "mission.name", Str(mission?["name"]), seat.Mission?.Name);

            // The model field carries a plain id in some seats and the whole folded verdict in others.
            // The capture separates them, so both shapes are checked against the half they belong to.
            var model = hand["model"];
            if (model is JsonValue v && v.TryGetValue<string>(out var modelId))
                differences += Compare(report, id, "model", modelId, seat.Model);
            else if (model is JsonObject mo)
            {
                differences += Compare(report, id, "model (absent)", null, seat.Model);
                differences += Compare(report, id, "modelDisplay.kind", Str(mo["kind"]), seat.ModelDisplay?.Kind);
                differences += Compare(report, id, "modelDisplay.text", Str(mo["text"]), seat.ModelDisplay?.Text);
            }

            var state = hand["stateAtDrain"]?.AsObject();
            differences += Compare(report, id, "stateAtDrain.status",
                Str(state?["status"]), seat.StateAtDrain?.Status);
            differences += Compare(report, id, "stateAtDrain.activityState",
                Str(state?["activityState"]), seat.StateAtDrain?.ActivityState);
            differences += Compare(report, id, "stateAtDrain.stateLabel",
                Str(state?["stateLabel"]), seat.StateAtDrain?.StateLabel);
            differences += Compare(report, id, "stateAtDrain.turnCount",
                state?["turnCount"]?.ToString(), seat.StateAtDrain?.TurnCount?.ToString());
            differences += Compare(report, id, "stateAtDrain.uncommittedCount",
                state?["uncommittedCount"]?.ToString(), seat.StateAtDrain?.UncommittedCount?.ToString());
        }

        report.AppendLine($"  {replayed} hand-written seat(s) replayed; {differences} field(s) differ.");
        return differences;
    }

    /// <summary>Rebuild the session record one hand-written seat was written from.</summary>
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

    private static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The names present in the hand-written index at the document level.</summary>
    private static HashSet<string> HandFields(JsonObject o) =>
        o.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

    private static int ReportValueParity(
        StringBuilder report, JsonObject handWritten, WorkspaceDocument captured)
    {
        report.AppendLine();
        report.AppendLine("3. VALUE PARITY - SESSIONS PRESENT IN BOTH");
        report.AppendLine("------------------------------------------");

        var handSeats = ((handWritten["sessions"] as JsonArray) ?? new JsonArray())
            .Where(n => n is not null)
            .Select(n => n!.AsObject())
            .Where(o => o["sessionId"] is not null)
            .ToDictionary(o => o["sessionId"]!.GetValue<string>(), o => o, StringComparer.OrdinalIgnoreCase);

        var overlap = captured.Seats
            .Where(s => s.SessionId is not null && handSeats.ContainsKey(s.SessionId))
            .ToList();

        if (overlap.Count == 0)
        {
            report.AppendLine("  NO OVERLAP. Not one session in the roster is in the hand-written index.");
            report.AppendLine("  That is expected when the index describes a fleet that has since been");
            report.AppendLine("  restarted - every session in it was destroyed and every restored one has a");
            report.AppendLine("  new id. It means this section proves NOTHING about value parity, and the");
            report.AppendLine("  field parity above plus WorkspaceCaptureTests (which folds the very session");
            report.AppendLine("  records the index was written from) are what carry that weight.");
            return 0;
        }

        var differences = 0;
        foreach (var seat in overlap)
        {
            var hand = handSeats[seat.SessionId!];
            differences += Compare(report, seat.SessionId!, "name", hand["name"]?.GetValue<string>(), seat.Name);
            differences += Compare(report, seat.SessionId!, "agent", hand["agent"]?.GetValue<string>(), seat.Agent);
            differences += Compare(report, seat.SessionId!, "repoPath", hand["repoPath"]?.GetValue<string>(), seat.RepoPath);
            differences += Compare(report, seat.SessionId!, "role", hand["role"]?.GetValue<string>(), seat.Role);
            differences += Compare(report, seat.SessionId!, "reportsTo", hand["reportsTo"]?.GetValue<string>(), seat.ReportsTo);
            differences += Compare(report, seat.SessionId!, "claudeSessionId",
                hand["claudeSessionId"]?.GetValue<string>(), seat.ClaudeSessionId);
        }

        report.AppendLine($"  {overlap.Count} session(s) in both; {differences} field(s) differ.");
        return differences;
    }

    private static int Compare(StringBuilder report, string sessionId, string field, string? hand, string? cap)
    {
        if (string.Equals(hand ?? "", cap ?? "", StringComparison.Ordinal)) return 0;
        report.AppendLine($"  DIFFERS {sessionId} {field}: hand-written '{hand}' vs captured '{cap}'");
        return 1;
    }
}
