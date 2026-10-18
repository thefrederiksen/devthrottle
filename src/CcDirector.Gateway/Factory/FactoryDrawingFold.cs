using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// What a factory does, drawn (owner, 18 October 2026; critical rule 7). Pure: it reads the registered factory, the
/// Seats tab's rows (each seat's when and computer), the published definition (each seat's starts and steps) and the
/// newest runs of each seat's schedules, and returns every position, word and tone finished.
///
/// A small slice of BPMN, never executable: a lane per seat, in the factory's own seat order with the boss first; a
/// start (a clock, an interval, or a person); a box per step, a circling arrow on one that repeats; a diamond for a
/// decision with its one branch below; and an end (done, or stopped for a person). Steps are not tracked one by one,
/// so only the start and the main line's end carry the newest run's colour - and the notes say so.
/// </summary>
public static class FactoryDrawingFold
{
    internal const double Margin = 20;
    internal const double LabelWidth = 120;
    internal const double Inset = 22;
    internal const double Gap = 40;
    internal const double MaxRowWidth = 1000;
    internal const double LanePad = 22;
    internal const double LaneGap = 12;
    internal const double RowGap = 34;
    internal const double MainHeight = 64;
    internal const double LabelBand = 36;
    internal const double BranchDrop = 44;
    internal const double CircleSize = 44;
    internal const double TaskWidth = 150;
    internal const double TaskHeight = 56;
    internal const double DecisionWidth = 124;
    internal const double DecisionHeight = 64;

    private const string NotStartedPrefix = "it did not start a session: ";

    private sealed record Box(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
        public double Cx => X + W / 2;
        public double Cy => Y + H / 2;
    }

    /// <summary>The newest run of a seat, as the drawing reads it.</summary>
    private enum RunState { None, NotStarted, Unconfirmed, Checking, NothingToDo, Started }

    private sealed record SeatRun(CronRunRecord? Run, RunState State, string StartTone, string StartSub, string EndTone, string? EndSub)
    {
        /// <summary>Whether a session ran: only then is the start's arrow solid and the run's colour on the end.</summary>
        public bool Started => State == RunState.Started;
    }

    /// <summary>The most characters a caption under a circle, or an arrow's label, may hold.</summary>
    internal const int MaxCaption = 30;
    internal const int MaxLabel = 30;

    // Rough widths of one character, to leave room for the text the Cockpit draws: a 12 px semibold caption, a
    // 10.5 px second line, and a 10 px label.
    private const double CaptionChar = 6.6;
    private const double SubChar = 5.8;
    private const double LabelChar = 5.6;

    private static bool IsCircle(string kind) => kind is not (FactoryDrawingKinds.Task or FactoryDrawingKinds.Decision);

    private static double TextWidth(string text, double perChar) => text.Length * perChar;

    /// <summary>How wide a circle's caption is: its title or its second line, whichever is wider.</summary>
    private static double CaptionWidth(FactoryDrawingNodeDto n) =>
        Math.Max(TextWidth(n.Title, CaptionChar), n.Sub is null ? 0 : TextWidth(n.Sub, SubChar));

    public static FactoryDrawingViewDto View(RegisteredFactoryDto factory, FactorySeatsViewDto seats, FactoryDefinition? definition,
        IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> runs, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(zone);

        var rows = seats.Rows.ToDictionary(r => r.SeatId, StringComparer.OrdinalIgnoreCase);
        var defined = (definition?.Seats ?? Array.Empty<SeatDefinition>())
            .ToDictionary(s => s.Seat, StringComparer.OrdinalIgnoreCase);
        var ordered = factory.Seats
            .OrderBy(s => FactoriesScreenFold.SameId(s.Id, factory.BossSeat) ? 0 : 1)
            .ToList();

        var view = new FactoryDrawingViewDto { FactoryId = factory.Factory, Title = factory.Title };
        var x0 = Margin + LabelWidth + Inset;
        var top = Margin;
        var widest = x0 + 2 * (TaskWidth + Gap);
        var worst = (Rank: -1, Status: (FactoryDrawingStatusDto?)null);

        foreach (var seat in ordered)
        {
            rows.TryGetValue(seat.Id, out var row);
            defined.TryGetValue(seat.Id, out var seatDefinition);
            var run = RunOf(seat, runs, zone, nowUtc);
            var laneTop = top;
            var bottom = Lane(view, factory, seat, row, seatDefinition, run, x0, laneTop + LanePad, ref widest);
            var height = bottom - laneTop + LanePad;
            view.Lanes.Add(new FactoryDrawingLaneDto
            {
                SeatId = seat.Id, Title = seat.Name,
                Sub = row?.ComputerText ?? seat.Computer,
                Href = $"/factories/{Uri.EscapeDataString(factory.Factory)}/seats/{Uri.EscapeDataString(seat.Id)}",
                X = Margin, Y = laneTop, Height = height,
                Note = definition is null ? null
                    : seatDefinition is null ? $"{seat.Name} is not in the published definition, so only how it starts is drawn."
                    : seatDefinition.Steps.Count == 0
                        ? $"{seat.Name} lists no steps yet, so the dashed box stands for its brief. Add steps to seats/{seat.Id}.yaml in the definition to draw what it does."
                        : null,
            });
            top = laneTop + height + LaneGap;

            var (rank, status) = StatusOf(seat, run, zone, nowUtc);
            if (rank > worst.Rank) worst = (rank, status);
        }

        foreach (var lane in view.Lanes) lane.Width = widest + Inset - Margin;
        view.Width = Math.Round(widest + Inset + Margin, 2);
        view.Height = Math.Round((view.Lanes.Count == 0 ? Margin + 80 : top - LaneGap) + Margin, 2);
        view.Status = worst.Status;
        view.Legend = Legend(view.Nodes);

        if (definition is null)
            view.Notes.Add("This factory has no published definition, so the drawing shows only how each seat starts. Its steps are drawn once the definition lists them.");
        view.Notes.Add("Steps are not tracked one by one yet: the colours show how the newest run started and how it ended.");
        return view;
    }

    /// <summary>One seat's lane: its start, its steps (wrapping onto further rows), and its end. Returns its bottom.</summary>
    private static double Lane(FactoryDrawingViewDto view, RegisteredFactoryDto factory, RegisteredFactorySeatDto seat,
        FactorySeatRowDto? row, SeatDefinition? def, SeatRun run, double x0, double rowTop, ref double widest)
    {
        var id = seat.Id;
        var steps = def?.Steps ?? Array.Empty<SeatStepDefinition>();
        var total = Math.Max(1, steps.Count);

        // ---------- what goes on the main line, in order
        var items = new List<FactoryDrawingNodeDto> { Start(id, def, row, seat.Schedules.Count > 0, run) };
        if (steps.Count == 0)
        {
            items.Add(new FactoryDrawingNodeDto
            {
                Id = $"{id}/brief", SeatId = id, Kind = FactoryDrawingKinds.Task, Title = "Works from its brief",
                Sub = "no steps listed yet", Tone = FactoryTone.Grey, Dashed = true,
                Panel = new FactoryDrawingPanelDto
                {
                    Heading = $"{seat.Name}: no steps listed yet",
                    Lines = { "What this seat does is written only in its brief, which is on its own page.",
                              "List its steps in the seat's file in the definition and they are drawn here." },
                },
            });
        }
        for (var i = 0; i < steps.Count; i++) items.Add(Step(seat, steps[i], i, total));
        items.Add(new FactoryDrawingNodeDto
        {
            Id = $"{id}/end", SeatId = id, Kind = FactoryDrawingKinds.EndDone, Title = "Done",
            Sub = run.EndSub, Tone = run.EndTone,
        });

        // ---------- place them in rows, left to right
        var rowStart = rowTop;
        var x = x0;
        var rowHasBranch = false;
        var rowRight = x0;
        var placed = new List<(FactoryDrawingNodeDto Node, Box Box, int Row)>();
        var rowIndex = 0;
        var rowTops = new List<double> { rowStart };
        var rowRights = new List<double>();
        var rowHeights = new List<double>();
        foreach (var node in items)
        {
            var (w, h) = SizeOf(node.Kind);
            var branchWidth = node.Kind == FactoryDrawingKinds.Decision && BranchOf(steps, node) is not null
                ? TaskWidth / 2 + Gap + CircleSize - w / 2
                : 0;
            // A circle's caption is centred under it and may be wider than the circle: it reaches this far either side.
            var overhang = IsCircle(node.Kind) ? Math.Max(0, CaptionWidth(node) / 2 - w / 2) : 0;
            if (x > x0 && x + w + Math.Max(branchWidth, overhang) > x0 + MaxRowWidth)
            {
                rowRights.Add(rowRight);
                rowHeights.Add(RowHeight(rowHasBranch));
                rowStart += RowHeight(rowHasBranch) + RowGap;
                rowTops.Add(rowStart);
                rowIndex++;
                x = x0;
                rowHasBranch = false;
                rowRight = x0;
            }
            // The first shape of a row moves right until its caption clears the lane's label band.
            if (x == x0) x += Math.Max(0, overhang - (Inset - 4));
            var box = new Box(x, rowStart + (MainHeight - h) / 2, w, h);
            placed.Add((node, box, rowIndex));
            rowRight = Math.Max(rowRight, box.Right + Math.Max(branchWidth, overhang));
            if (branchWidth > 0) rowHasBranch = true;
            // After a decision the gap holds the answer that stays on the line.
            var label = node.Kind == FactoryDrawingKinds.Decision ? StepOf(steps, node)?.ContinueWhen : null;
            x = box.Right + (label is null ? Gap : Math.Max(Gap, TextWidth(Clip(label, MaxLabel), LabelChar) + 16));
        }
        rowRights.Add(rowRight);
        rowHeights.Add(RowHeight(rowHasBranch));

        foreach (var (node, box, _) in placed) Add(view, node, box);

        // ---------- the main line's arrows
        var startTone = run.Started ? FactoryTone.Neutral : run.StartTone;
        for (var i = 0; i + 1 < placed.Count; i++)
        {
            var (a, ab, ar) = placed[i];
            var (b, bb, br) = placed[i + 1];
            var fromStart = i == 0;
            var label = a.Kind == FactoryDrawingKinds.Decision ? StepOf(steps, a)?.ContinueWhen : null;
            if (label is not null) label = Clip(label, MaxLabel);
            List<double[]> points;
            double lx, ly;
            if (ar == br)
            {
                points = Straight(new[] { ab.Right, ab.Cy }, new[] { bb.X, bb.Cy });
                (lx, ly) = ((ab.Right + bb.X) / 2, ab.Cy - 8);
            }
            else
            {
                // Onto the next row: out to the right of everything on this row, down to the gap between the rows, back
                // along it, and down into the next row's first shape.
                var outX = rowRights[ar] + 16;
                var channel = rowTops[ar] + rowHeights[ar] + RowGap / 2;
                points = Polyline(new[] { ab.Right, ab.Cy }, new[] { outX, ab.Cy }, new[] { outX, channel },
                    new[] { bb.Cx, channel }, new[] { bb.Cx, bb.Y });
                (lx, ly) = (outX + 6, ab.Cy - 8);
            }
            Arrow(view, a.Id, b.Id, points, label, lx, ly,
                fromStart ? startTone : FactoryTone.Neutral, dashed: fromStart && !run.Started);
        }

        // ---------- each decision's branch, below it
        var bottom = rowTops[^1] + rowHeights[^1];
        foreach (var (node, box, r) in placed.Where(p => p.Node.Kind == FactoryDrawingKinds.Decision))
        {
            if (StepOf(steps, node) is not { Branch: { } branch } step) continue;
            var by = rowTops[r] + MainHeight + LabelBand / 2 + BranchDrop;
            var bbox = new Box(box.Cx - TaskWidth / 2, by, TaskWidth, TaskHeight);
            var stop = branch.Ends == "stop";
            var branchNode = new FactoryDrawingNodeDto
            {
                Id = $"{node.Id}/branch", SeatId = id, Kind = FactoryDrawingKinds.Task, Title = Clip(branch.Name, 22),
                Sub = branch.Detail is null ? null : Clip(branch.Detail, 26), Tone = FactoryTone.Neutral,
                Panel = new FactoryDrawingPanelDto
                {
                    Heading = $"{branch.Name} (when {step.Name}: {branch.When}, {seat.Name})",
                    Lines = Lines(branch.Detail, stop ? "Then the seat stops and waits for you." : "Then the seat finishes."),
                },
            };
            Add(view, branchNode, bbox);
            var ebox = new Box(bbox.Right + Gap, bbox.Cy - CircleSize / 2, CircleSize, CircleSize);
            Add(view, new FactoryDrawingNodeDto
            {
                Id = $"{node.Id}/branch/end", SeatId = id,
                Kind = stop ? FactoryDrawingKinds.EndStop : FactoryDrawingKinds.EndDone,
                Title = stop ? "Stops for you" : "Done", Tone = stop ? FactoryTone.Red : FactoryTone.Neutral,
            }, ebox);
            // The answer that takes the branch sits beside its arrow, never on it.
            var when = Clip(branch.When, MaxLabel);
            Arrow(view, node.Id, branchNode.Id, Straight(new[] { box.Cx, box.Bottom }, new[] { box.Cx, bbox.Y }),
                when, box.Cx + 8 + TextWidth(when, LabelChar) / 2, (box.Bottom + bbox.Y) / 2 + 4, stop ? FactoryTone.Red : FactoryTone.Neutral, dashed: false);
            Arrow(view, branchNode.Id, $"{node.Id}/branch/end", Straight(new[] { bbox.Right, bbox.Cy }, new[] { ebox.X, ebox.Cy }),
                null, 0, 0, stop ? FactoryTone.Red : FactoryTone.Neutral, dashed: false);
        }

        // Every shape's right edge and every caption under a circle, plus the branch ends below.
        widest = Math.Max(widest, rowRights.Max() + 24);
        foreach (var n in view.Nodes.Where(n => n.SeatId == id && IsCircle(n.Kind)))
            widest = Math.Max(widest, n.X + n.Width / 2 + CaptionWidth(n) / 2 + 8);
        return bottom;
    }

    private static double RowHeight(bool hasBranch) =>
        MainHeight + LabelBand + (hasBranch ? BranchDrop + TaskHeight - LabelBand / 2 : 0);

    private static (double W, double H) SizeOf(string kind) => kind switch
    {
        FactoryDrawingKinds.Task => (TaskWidth, TaskHeight),
        FactoryDrawingKinds.Decision => (DecisionWidth, DecisionHeight),
        _ => (CircleSize, CircleSize),
    };

    private static FactoryDrawingNodeDto Start(string seatId, SeatDefinition? def, FactorySeatRowDto? row, bool scheduled, SeatRun run)
    {
        var start = def?.Starts.FirstOrDefault();
        var (kind, title) = start switch
        {
            { Every: { } every } => (FactoryDrawingKinds.StartEvery, $"Every {every}, by its check"),
            { Cron: not null } => (FactoryDrawingKinds.StartClock, row?.WhenText ?? "On a schedule"),
            null when scheduled => (FactoryDrawingKinds.StartClock, row?.WhenText ?? "On a schedule"),
            _ => (FactoryDrawingKinds.StartTalk, "When you talk to it"),
        };
        var more = def is { Starts.Count: > 1 } ? $" (and {def.Starts.Count - 1} more)" : "";
        return new FactoryDrawingNodeDto
        {
            Id = $"{seatId}/start", SeatId = seatId, Kind = kind, Title = Clip(title, MaxCaption - more.Length) + more,
            Sub = kind == FactoryDrawingKinds.StartTalk ? null : run.StartSub, Tone = kind == FactoryDrawingKinds.StartTalk ? FactoryTone.Neutral : run.StartTone,
            Panel = run.Run is { } r && !run.Started && r.ResultReason is { } why
                ? new FactoryDrawingPanelDto { Heading = $"{title}: {run.StartSub}", Lines = { Reason(why) } }
                : null,
        };
    }

    private static FactoryDrawingNodeDto Step(RegisteredFactorySeatDto seat, SeatStepDefinition step, int index, int total)
    {
        var decision = step.Decision is not null;
        var node = new FactoryDrawingNodeDto
        {
            Id = $"{seat.Id}/{index + 1}", SeatId = seat.Id,
            Kind = decision ? FactoryDrawingKinds.Decision : FactoryDrawingKinds.Task,
            Title = Clip(step.Name, decision ? 16 : 22),
            Sub = decision ? null : step.Detail is null ? null : Clip(step.Detail, 26),
            Repeats = step.Repeats is not null,
            Tone = FactoryTone.Neutral,
        };
        node.Panel = new FactoryDrawingPanelDto
        {
            Heading = $"{step.Name} (step {index + 1} of {total}, {seat.Name})",
            Lines = Lines(decision ? $"Decides: {step.Decision}" : step.Detail,
                step.Repeats is null ? null : $"Repeats {step.Repeats}.",
                decision && step.ContinueWhen is not null ? $"{Capital(step.ContinueWhen)}: carries on along the line." : null,
                step.Branch is { } b ? $"{Capital(b.When)}: {b.Name}, then {(b.Ends == "stop" ? "stops and waits for you" : "finishes")}." : null),
            Runs = step.Runs,
        };
        return node;
    }

    private static SeatStepDefinition? StepOf(IReadOnlyList<SeatStepDefinition> steps, FactoryDrawingNodeDto node)
    {
        var slash = node.Id.LastIndexOf('/');
        return int.TryParse(node.Id[(slash + 1)..], out var n) && n >= 1 && n <= steps.Count ? steps[n - 1] : null;
    }

    private static SeatStepBranchDefinition? BranchOf(IReadOnlyList<SeatStepDefinition> steps, FactoryDrawingNodeDto node) =>
        StepOf(steps, node)?.Branch;

    /// <summary>
    /// The newest run of any of the seat's schedules, and how its start and its end are coloured. A run with no session
    /// is ruled on what the Gateway already folded onto it - its ending and its result - never on one status string: a
    /// work-list drain went well, a due check is still checking, a start the Gateway never heard back from is not
    /// confirmed, a check that found nothing ran nothing, and every other run with no session did not start.
    /// </summary>
    private static SeatRun RunOf(RegisteredFactorySeatDto seat, IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> runs,
        TimeZoneInfo zone, DateTime nowUtc)
    {
        var newest = seat.Schedules
            .SelectMany(j => runs.TryGetValue(j, out var list) ? list : Array.Empty<CronRunRecord>())
            .OrderByDescending(r => r.ScheduledUtc)
            .FirstOrDefault();
        if (newest is null) return new SeatRun(null, RunState.None, FactoryTone.Grey, "not run yet", FactoryTone.Grey, null);

        var when = FactoriesScreenFold.When(newest.ScheduledUtc, zone, nowUtc, capital: false);
        var endText = newest.EndingText is null ? null : Clip(newest.EndingText, MaxCaption);
        if (newest.SessionId is null)
        {
            if (newest.Ending == CronRunEndings.WorkList)
                return new SeatRun(newest, RunState.Started, FactoryTone.Ok, $"started {when}",
                    newest.Result == CronRunResults.Problem ? FactoryTone.Red : FactoryTone.Ok, endText);
            if (newest.Ending == CronRunEndings.Checking || newest.Result == CronRunResults.Checking)
                return new SeatRun(newest, RunState.Checking, FactoryTone.Blue, $"checking, {when}", FactoryTone.Grey, null);
            if (newest.Ending == CronRunEndings.StartUnknown || newest.Result == CronRunResults.StartUnknown)
                return new SeatRun(newest, RunState.Unconfirmed, FactoryTone.Amber, $"start not confirmed, {when}", FactoryTone.Grey, null);
            if (newest.Ending == CronRunEndings.NothingToDo || newest.Result == CronRunResults.NothingToDo)
                return new SeatRun(newest, RunState.NothingToDo, FactoryTone.Ok, $"nothing to do, {when}", FactoryTone.Grey, null);
            return new SeatRun(newest, RunState.NotStarted, FactoryTone.Red, $"did not start, {when}", FactoryTone.Grey, null);
        }

        var endTone = newest.Result == CronRunResults.Problem ? FactoryTone.Red
            : newest.Ending is null or CronRunEndings.StillOpen or CronRunEndings.Checking ? FactoryTone.Blue
            : newest.Ending == CronRunEndings.ClosedItself || newest.Result is CronRunResults.Ok or CronRunResults.NothingToDo
                ? FactoryTone.Ok
                : FactoryTone.Amber;
        return new SeatRun(newest, RunState.Started, FactoryTone.Ok, $"started {when}", endTone,
            endTone == FactoryTone.Blue ? "running now" : endText);
    }

    /// <summary>One seat's line for the top of the Overview, with how bad it is (higher is worse).</summary>
    private static (int Rank, FactoryDrawingStatusDto? Status) StatusOf(RegisteredFactorySeatDto seat, SeatRun run, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (run.Run is not { } r) return (-1, null);
        var when = FactoriesScreenFold.When(r.ScheduledUtc, zone, nowUtc, capital: false);
        var why = r.ResultReason is null ? null : Reason(r.ResultReason);
        switch (run.State)
        {
            case RunState.NotStarted:
                return (4, new FactoryDrawingStatusDto { Text = $"The {seat.Name}'s run of {when} did not start.", Sub = why, Tone = FactoryTone.Red });
            case RunState.Unconfirmed:
                return (2, new FactoryDrawingStatusDto { Text = $"The {seat.Name}'s run of {when} may not have started.", Sub = why, Tone = FactoryTone.Amber });
            case RunState.Checking:
                return (1, new FactoryDrawingStatusDto { Text = $"The {seat.Name}'s check of {when} has not answered yet.", Tone = FactoryTone.Blue });
            case RunState.NothingToDo:
                return (0, new FactoryDrawingStatusDto { Text = $"The {seat.Name}'s check of {when} found nothing to do.", Tone = FactoryTone.Ok });
        }
        return run.EndTone switch
        {
            FactoryTone.Red => (3, new FactoryDrawingStatusDto { Text = $"The {seat.Name}'s run of {when} ended with a problem.", Sub = why, Tone = FactoryTone.Red }),
            FactoryTone.Amber => (2, new FactoryDrawingStatusDto { Text = $"The {seat.Name}'s run of {when} {r.EndingText ?? "ended"}.", Tone = FactoryTone.Amber }),
            FactoryTone.Blue => (1, new FactoryDrawingStatusDto { Text = $"The {seat.Name} is running now (started {when}).", Tone = FactoryTone.Blue }),
            _ => (0, new FactoryDrawingStatusDto
            {
                Text = $"The {seat.Name}'s run of {when} went well.",
                Sub = r.EndingText is null ? null : Capital(r.EndingText) + ".", Tone = FactoryTone.Ok,
            }),
        };
    }

    /// <summary>What each shape and colour on THIS drawing means: only the shapes it has, then the colours.</summary>
    private static List<FactoryDrawingLegendDto> Legend(IReadOnlyList<FactoryDrawingNodeDto> nodes)
    {
        var kinds = nodes.Select(n => n.Kind).ToHashSet(StringComparer.Ordinal);
        var all = new List<FactoryDrawingLegendDto>
        {
            new() { Kind = FactoryDrawingKinds.StartClock, Text = "starts on a schedule" },
            new() { Kind = FactoryDrawingKinds.StartEvery, Text = "starts on an interval, when its check says so" },
            new() { Kind = FactoryDrawingKinds.StartTalk, Text = "starts when you talk to it" },
            new() { Kind = FactoryDrawingKinds.Task, Text = nodes.Any(n => n.Repeats) ? "a step (a circling arrow: it repeats)" : "a step" },
            new() { Kind = FactoryDrawingKinds.Decision, Text = "a decision" },
            new() { Kind = FactoryDrawingKinds.EndDone, Text = "finishes" },
            new() { Kind = FactoryDrawingKinds.EndStop, Tone = FactoryTone.Red, Text = "stops and waits for you" },
        };
        var legend = all.Where(l => kinds.Contains(l.Kind)).ToList();
        legend.Add(new() { Kind = FactoryDrawingKinds.Tone, Tone = FactoryTone.Ok, Text = "went well" });
        legend.Add(new() { Kind = FactoryDrawingKinds.Tone, Tone = FactoryTone.Red, Text = "failed or did not start" });
        legend.Add(new() { Kind = FactoryDrawingKinds.Tone, Tone = FactoryTone.Grey, Text = "not run" });
        return legend;
    }

    private static void Add(FactoryDrawingViewDto view, FactoryDrawingNodeDto node, Box box)
    {
        node.X = Math.Round(box.X, 2);
        node.Y = Math.Round(box.Y, 2);
        node.Width = box.W;
        node.Height = box.H;
        view.Nodes.Add(node);
    }

    private static void Arrow(FactoryDrawingViewDto view, string from, string to, List<double[]> points, string? label,
        double lx, double ly, string tone, bool dashed)
    {
        // The arrow stops short of the shape, and its head fills the gap; its direction is from the last point that
        // is not the tip (a straight segment repeats its end as a control point).
        var tip = points[^1];
        var prev = points.Take(points.Count - 1).Reverse()
            .FirstOrDefault(p => Math.Abs(p[0] - tip[0]) + Math.Abs(p[1] - tip[1]) > 0.001) ?? new[] { tip[0] - 1, tip[1] };
        var (dx, dy) = (tip[0] - prev[0], tip[1] - prev[1]);
        var len = Math.Sqrt(dx * dx + dy * dy);
        var (ux, uy) = (dx / len, dy / len);
        var end = new[] { tip[0] - ux * 8, tip[1] - uy * 8 };
        for (var i = points.Count - 1; i >= 0 && ReferenceEquals(points[i], tip); i--) points[i] = end;
        view.Arrows.Add(new FactoryDrawingArrowDto
        {
            From = from, To = to, Path = FactoryMapFold.PathOf(points), Head = FactoryMapFold.HeadOf(end, tip),
            Label = label, LabelX = Math.Round(lx, 2), LabelY = Math.Round(ly, 2),
            Tone = tone, Dashed = dashed,
        });
    }

    /// <summary>A straight line as one cubic segment.</summary>
    private static List<double[]> Straight(double[] a, double[] b) => new() { a, a, b, b };

    /// <summary>Straight lines through every point, each as one cubic segment.</summary>
    private static List<double[]> Polyline(params double[][] points)
    {
        var list = new List<double[]> { points[0] };
        for (var i = 1; i < points.Length; i++)
        {
            list.Add(points[i - 1]);
            list.Add(points[i]);
            list.Add(points[i]);
        }
        return list;
    }

    private static List<string> Lines(params string?[] lines) => lines.Where(l => l is not null).Select(l => l!).ToList();

    private static string Reason(string why) =>
        Capital(why.StartsWith(NotStartedPrefix, StringComparison.Ordinal) ? why[NotStartedPrefix.Length..] : why);

    private static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 3)].TrimEnd() + "...";
}
