using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// A factory's floor, decided once (owner decision, 8 October 2026; critical rule 7). Pure: it reads the registered
/// factory (its seats and each seat's production line), the Seats tab's rows (each seat's light), the factory page
/// (what is failing and what waits on the owner) and the map the factory published (its arrows), and returns every
/// position, word and tone finished. The layout is a grid decided here - lanes as rows, bays in the factory's own
/// seat order, the Boss's office on top, the owner's desk on the right - so the drawing never depends on Graphviz.
/// </summary>
public static class FactoryFloorFold
{
    internal const double Margin = 20;
    internal const double LabelWidth = 128;
    internal const double Inset = 18;
    internal const double BayWidth = 160;
    internal const double BayHeight = 50;
    internal const double GapX = 44;
    internal const double RowGap = 26;
    internal const double LanePad = 26;
    internal const double LaneGap = 12;
    internal const double OfficeHeight = 58;
    internal const double OfficeGap = 22;
    internal const double DeskGap = 56;
    internal const double DeskWidth = 260;
    internal const double DeskItemsTop = 58;
    internal const double DeskItemStep = 50;
    internal const int MaxPerRow = 4;
    internal const int MaxDeskItems = 6;

    public const string OtherLane = "Other";
    public const string OneLane = "Every seat";
    public const string Solid = "solid";
    public const string Dashed = "dashed";
    public const string Dotted = "dotted";

    private sealed record Box(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
        public double Cx => X + W / 2;
        public double Cy => Y + H / 2;
    }

    public static FactoryFloorViewDto View(RegisteredFactoryDto factory, FactorySeatsViewDto seats, FactoryPageViewDto page,
        StoredFactoryMap? map)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(page);

        var rows = seats.Rows.ToDictionary(r => r.SeatId, StringComparer.OrdinalIgnoreCase);
        var bossId = factory.BossSeat;
        bool IsBoss(string id) => bossId is not null && string.Equals(id, bossId, StringComparison.OrdinalIgnoreCase);
        var nodes = map?.Map.Nodes ?? new List<FactoryMapNodeRequest>();
        var edges = map?.Map.Edges ?? new List<FactoryMapEdgeRequest>();
        var nodeById = nodes.GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var ownerIds = nodes.Where(n => n.Kind == FactoryMapNodeKind.Owner).Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ---------- the lanes: each seat's line, in the factory's own seat order; the boss works from its office
        var workers = factory.Seats.Where(s => !IsBoss(s.Id)).ToList();
        var anyLine = workers.Any(s => s.Line is not null);
        string LaneOf(RegisteredFactorySeatDto s) => s.Line ?? (anyLine ? OtherLane : OneLane);
        var laneNames = workers.Select(LaneOf).Distinct(StringComparer.Ordinal).ToList();
        if (laneNames.Remove(OtherLane)) laneNames.Add(OtherLane);
        var laneItems = laneNames.ToDictionary(n => n, _ => new List<(string Id, string Kind)>(), StringComparer.Ordinal);

        // A source of work (a mailbox) sits at the front of the lane of the first seat it wakes.
        var seatLane = workers.ToDictionary(s => s.Id, LaneOf, StringComparer.OrdinalIgnoreCase);
        foreach (var source in nodes.Where(n => n.Kind == FactoryMapNodeKind.Source))
        {
            var target = edges.FirstOrDefault(e => string.Equals(e.From, source.Id, StringComparison.OrdinalIgnoreCase) && seatLane.ContainsKey(e.To));
            if (target is not null) laneItems[seatLane[target.To]].Add((source.Id, "source"));
        }
        foreach (var s in workers) laneItems[LaneOf(s)].Add((s.Id, "seat"));

        // ---------- the grid
        var columns = Math.Max(2, Math.Min(MaxPerRow, laneItems.Values.Select(v => v.Count).DefaultIfEmpty(0).Max()));
        var bayX0 = Margin + LabelWidth + Inset;
        var lanesRight = bayX0 + columns * BayWidth + (columns - 1) * GapX + Inset;
        var top = Margin;
        var view = new FactoryFloorViewDto { FactoryId = factory.Factory, Title = factory.Title };
        var boxes = new Dictionary<string, Box>(StringComparer.OrdinalIgnoreCase);

        Box? office = null;
        if (bossId is not null && factory.Seats.FirstOrDefault(s => IsBoss(s.Id)) is { } boss)
        {
            office = new Box(bayX0, top, lanesRight - bayX0, OfficeHeight);
            rows.TryGetValue(boss.Id, out var bossRow);
            var role = string.Equals(boss.Role, FactoriesScreenFold.BossRoleWord, StringComparison.Ordinal) ? "" : $"the {boss.Role} - ";
            view.Office = new FactoryFloorOfficeDto
            {
                Title = "Boss's office",
                Sub = bossRow is null ? $"{role}not run yet" : $"{role}{bossRow.WhenText} - {bossRow.LastRunText}",
                Tone = bossRow?.LastRunTone ?? FactoryTone.Idle,
                X = office.X, Y = office.Y, Width = office.W, Height = office.H,
            };
            boxes[boss.Id] = office;
            top += OfficeHeight + OfficeGap;
        }

        var lanesTop = top;
        for (var li = 0; li < laneNames.Count; li++)
        {
            var items = laneItems[laneNames[li]];
            var laneRows = Math.Max(1, (items.Count + columns - 1) / columns);
            var height = LanePad * 2 + laneRows * BayHeight + (laneRows - 1) * RowGap;
            view.Lanes.Add(new FactoryFloorLaneDto { Name = laneNames[li], Hue = li % 4, X = Margin, Y = top, Width = lanesRight - Margin, Height = height });
            for (var i = 0; i < items.Count; i++)
            {
                var box = new Box(bayX0 + (i % columns) * (BayWidth + GapX), top + LanePad + (i / columns) * (BayHeight + RowGap), BayWidth, BayHeight);
                boxes[items[i].Id] = box;
                view.Bays.Add(Bay(factory, items[i].Id, items[i].Kind, box, rows, nodeById));
            }
            top += height + LaneGap;
        }
        var lanesBottom = laneNames.Count == 0 ? lanesTop + 80 : top - LaneGap;

        // ---------- the desk: what needs the owner, from the factory page's own failures and questions
        var deskItems = new List<FactoryFloorDeskItemDto>();
        foreach (var f in page.Failures?.Items ?? new List<FactoryFailureItemDto>())
            deskItems.Add(new() { Title = Clip(f.By, 34), Text = Clip(f.What, 40), Tone = FactoryTone.Red });
        foreach (var w in page.Waiting.Items)
            deskItems.Add(new() { Title = Clip(w.By, 34), Text = Clip(w.What, 40), Tone = w.Tone });
        var more = deskItems.Count - MaxDeskItems;
        if (more > 0)
        {
            deskItems = deskItems.Take(MaxDeskItems - 1).ToList();
            deskItems.Add(new() { Text = $"and {more + 1} more on this page", Tone = FactoryTone.Neutral });
        }
        var emails = edges.Where(e => e.Kind == FactoryMapEdgeKind.Notify).Select(e => e.From).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var deskNeeds = DeskItemsTop + Math.Max(1, deskItems.Count) * DeskItemStep + (emails > 0 ? 44 : 16);
        var desk = new Box(lanesRight + DeskGap, lanesTop, DeskWidth, Math.Max(lanesBottom - lanesTop, deskNeeds));
        view.Desk = new FactoryFloorDeskDto
        {
            X = desk.X, Y = desk.Y, Width = desk.W, Height = desk.H,
            Items = deskItems,
            EmptyText = deskItems.Count == 0 ? "Nothing is waiting on you." : null,
            Note = emails == 0 ? null : $"Failures also reach you by email, from {emails} {(emails == 1 ? "seat" : "seats")}.",
        };
        foreach (var id in ownerIds) boxes[id] = desk;

        // ---------- the arrows the factory published, routed on this grid
        var unplaced = 0;
        var kinds = new HashSet<(string Tone, string Line, string Text)>();
        foreach (var e in edges)
        {
            if (e.Kind == FactoryMapEdgeKind.Notify) continue;                 // said once, on the desk
            if (!boxes.TryGetValue(e.From, out var from) || !boxes.TryGetValue(e.To, out var to)) { unplaced++; continue; }
            var (tone, line, meaning) = Style(e);
            kinds.Add((tone, line, meaning));
            var (points, lx, ly) = Route(from, to, ReferenceEquals(to, desk), ReferenceEquals(from, office), ReferenceEquals(to, office), lanesRight);
            var tip = points[^1];
            var u = Unit(points[^2], tip);
            var end = new[] { tip[0] - u.X * 8, tip[1] - u.Y * 8 };
            points[^1] = end;
            view.Arrows.Add(new FactoryFloorArrowDto
            {
                From = e.From, To = e.To, Label = Label(e.Label), Tone = tone, Line = line,
                Path = FactoryMapFold.PathOf(points),
                Head = FactoryMapFold.HeadOf(end, tip),
                LabelX = Math.Round(lx, 2), LabelY = Math.Round(ly, 2),
            });
        }
        view.Legend = kinds.OrderBy(k => LegendOrder(k.Tone, k.Line)).Select(k => new FactoryMapLegendDto { Text = k.Text, Tone = k.Tone, Line = k.Line }).ToList();

        view.Width = Math.Round(desk.Right + Margin, 2);
        view.Height = Math.Round(Math.Max(lanesBottom, desk.Bottom) + Margin, 2);

        // ---------- what it rests on
        if (!anyLine)
            view.Notes.Add("This factory has not named its lines yet, so every seat stands in one lane. A seat's line is \"line\" in the factory's manifest, for example \"line\": \"Night shift\".");
        view.Notes.Add(map is null
            ? "This factory has not published its map, so the floor shows its seats and lines but no arrows."
            : $"The arrows are the ones the factory published in its map, {map.PublishedUtc:d MMM yyyy}.");
        if (unplaced > 0)
            view.Notes.Add($"{unplaced} {(unplaced == 1 ? "arrow names a box" : "arrows name boxes")} the factory has not registered as a seat, so {(unplaced == 1 ? "it is" : "they are")} not drawn.");
        return view;
    }

    private static FactoryFloorBayDto Bay(RegisteredFactoryDto f, string id, string kind, Box box,
        IReadOnlyDictionary<string, FactorySeatRowDto> rows, IReadOnlyDictionary<string, FactoryMapNodeRequest> nodes)
    {
        nodes.TryGetValue(id, out var node);
        var bay = new FactoryFloorBayDto { Id = id, Kind = kind, X = box.X, Y = box.Y, Width = box.W, Height = box.H };
        if (kind == "source")
        {
            bay.Title = Clip(node?.Title ?? id, 22);
            bay.Sub = Clip(string.Join(" ", node?.Lines ?? new List<string>()), 30);
            bay.Tone = FactoryTone.Neutral;
            bay.ToneText = "Work arrives from here.";
            return bay;
        }
        var seat = f.Seats.First(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        rows.TryGetValue(id, out var row);
        bay.Title = Clip(seat.Name, 22);
        bay.Sub = Clip(row?.WhenText ?? "Not scheduled", 30);
        bay.Tone = row?.LastRunTone ?? FactoryTone.Idle;
        bay.ToneText = row?.LastRunText ?? "Not run yet";
        bay.Dashed = node is { Built: false };
        if (bay.Dashed)
        {
            bay.Sub = "not built yet";
            bay.Tone = FactoryTone.Grey;
            bay.ToneText = "Not built yet.";
        }
        bay.Href = $"/factories/{Uri.EscapeDataString(f.Factory)}/seats";
        return bay;
    }

    private static (string Tone, string Line, string Meaning) Style(FactoryMapEdgeRequest e)
    {
        var (tone, line, meaning) = e.Kind switch
        {
            FactoryMapEdgeKind.Chain => e.Result switch
            {
                FactoryMapResults.Succeeded => (FactoryTone.Ok, Solid, "On success, starts the next"),
                FactoryMapResults.Failed => (FactoryTone.Red, Dashed, "On failure, starts the next"),
                FactoryMapResults.NeedsYou => (FactoryTone.Amber, Solid, "Needs you"),
                _ => (FactoryTone.Neutral, Solid, "Starts the next"),
            },
            FactoryMapEdgeKind.Asks => (FactoryTone.Amber, Solid, "Needs you"),
            FactoryMapEdgeKind.Escalate => (FactoryTone.Amber, Solid, "Needs you"),
            FactoryMapEdgeKind.Call => (FactoryTone.Neutral, Dashed, "Calls another seat"),
            FactoryMapEdgeKind.Trigger => (FactoryTone.Neutral, Dotted, "Woken from outside"),
            _ => throw new InvalidOperationException($"A stored arrow has kind '{e.Kind}', which the map store refuses."),
        };
        return e.Enabled ? (tone, line, meaning) : (FactoryTone.Grey, line, "Not switched on");
    }

    private static int LegendOrder(string tone, string line) => (tone, line) switch
    {
        (FactoryTone.Grey, _) => 6,
        (FactoryTone.Ok, _) => 0,
        (FactoryTone.Amber, _) => 1,
        (FactoryTone.Red, _) => 2,
        (_, Dashed) => 3,
        (_, Dotted) => 4,
        _ => 5,
    };

    /// <summary>
    /// An arrow's points (a start, then whole cubic segments) and where its label goes. An arrow never runs through a
    /// bay: to the desk it drops into the channel under its row and runs along it; between neighbours it is straight,
    /// with its label above the gap; to a bay further along the same row it runs in the channel underneath.
    /// </summary>
    private static (List<double[]> Points, double LabelX, double LabelY) Route(Box a, Box b, bool toDesk, bool fromOffice, bool toOffice,
        double lanesRight)
    {
        if (toDesk)
        {
            var channel = a.Bottom + RowGap / 2;
            var sx = a.X + a.W * 0.6;
            var ey = Math.Clamp(channel, b.Y + 40, b.Bottom - 16);
            var turn = lanesRight - 10;
            var points = new List<double[]>
            {
                new[] { sx, a.Bottom },
                new[] { sx, channel }, new[] { sx, channel }, new[] { sx + 12, channel },
                new[] { sx + 40, channel }, new[] { turn - 40, channel }, new[] { turn, channel },
                new[] { turn + 20, channel }, new[] { b.X - 20, ey }, new[] { b.X, ey },
            };
            return (points, Math.Min(sx + 70, turn - 50), channel + 11);
        }
        if (fromOffice)
        {
            var x = Math.Clamp(b.Cx, a.X + 24, a.Right - 24);
            var p = Curve(x, a.Bottom, x, a.Bottom + 20, b.Cx, b.Y - 20, b.Cx, b.Y);
            return (p, b.Cx + 6, (a.Bottom + b.Y) / 2);
        }
        if (toOffice)
        {
            var x = Math.Clamp(a.Cx, b.X + 24, b.Right - 24);
            var p = Curve(a.Cx, a.Y, a.Cx, a.Y - 20, x, b.Bottom + 20, x, b.Bottom);
            return (p, a.Cx + 6, (a.Y + b.Bottom) / 2);
        }
        if (Math.Abs(a.Cy - b.Cy) < 1)
        {
            if (b.X >= a.Right && b.X - a.Right <= GapX + 1)
                return (Curve(a.Right, a.Cy, a.Right + 12, a.Cy, b.X - 12, b.Cy, b.X, b.Cy), (a.Right + b.X) / 2, a.Y - 7);
            var channel = a.Bottom + RowGap / 2 - 4;
            var p = new List<double[]>
            {
                new[] { a.Cx, a.Bottom },
                new[] { a.Cx, channel }, new[] { a.Cx, channel }, new[] { a.Cx + 12, channel },
                new[] { a.Cx + 30, channel }, new[] { b.Cx - 30, channel }, new[] { b.Cx - 12, channel },
                new[] { b.Cx, channel }, new[] { b.Cx, channel }, new[] { b.Cx, b.Bottom },
            };
            return (p, (a.Cx + b.Cx) / 2, channel + 11);
        }
        if (b.Cy < a.Cy)
        {
            var sx = a.X + a.W * 0.8;
            var ex = b.X + b.W * 0.8;
            var p = Curve(sx, a.Y, sx, a.Y - 34, ex, b.Bottom + 34, ex, b.Bottom);
            var (mx, my) = Mid(p);
            return (p, mx + 6, my);
        }
        var x0 = a.X + a.W * 0.2;
        var x1 = b.X + b.W * 0.2;
        var down = Curve(x0, a.Bottom, x0, a.Bottom + 34, x1, b.Y - 34, x1, b.Y);
        var (dx, dy) = Mid(down);
        return (down, dx + 6, dy);
    }

    private static List<double[]> Curve(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3) =>
        new() { new[] { x0, y0 }, new[] { x1, y1 }, new[] { x2, y2 }, new[] { x3, y3 } };

    private static (double X, double Y) Mid(IReadOnlyList<double[]> p) =>
        ((p[0][0] + 3 * p[1][0] + 3 * p[2][0] + p[3][0]) / 8, (p[0][1] + 3 * p[1][1] + 3 * p[2][1] + p[3][1]) / 8);

    private static (double X, double Y) Unit(double[] from, double[] to)
    {
        var (dx, dy) = (to[0] - from[0], to[1] - from[1]);
        var len = Math.Sqrt(dx * dx + dy * dy);
        return len < 0.001 ? (1, 0) : (dx / len, dy / len);
    }

    /// <summary>A map label on one line: "succeeded\nstarts" reads "succeeded: starts".</summary>
    internal static string Label(string raw) =>
        string.Join(": ", raw.Split('\n').Select(p => p.Trim().TrimEnd(':').Trim()).Where(p => p.Length > 0));

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 3)].TrimEnd() + "...";
}
