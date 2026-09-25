using System.Globalization;
using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// The factory page's Map tab, decided once (issue #3383, critical rule 7). Pure: it reads the map the factory
/// published and the factory's card from <see cref="FactoryAgentsFold.Factories"/>, and returns every word, tone,
/// line style and path finished. The layout is the factory's (Graphviz, in the factory's own tool); what is added
/// here is only what the Gateway knows - each agent's status and last run, the same words its card shows.
/// </summary>
public static class FactoryMapFold
{
    public const string TabMap = "map";
    public const string TabAgents = "agents";

    private const string Solid = "solid";
    private const string Dashed = "dashed";
    private const string Dotted = "dotted";

    public static FactoryMapViewDto View(string factory, StoredFactoryMap? stored, FactoryCardDto? card, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var title = stored?.Map.Title ?? card?.Title ?? FactoryAgentsFold.Title(factory);
        var agents = card?.Agents ?? new List<FactoryAgentRowDto>();
        var view = new FactoryMapViewDto
        {
            FactoryId = factory,
            Title = title,
            Tabs = new()
            {
                new() { Key = TabMap, Label = "Map" },
                new() { Key = TabAgents, Label = $"Agents ({agents.Count})" },
            },
            Agents = agents,
            WaitingHref = FactoryAgentsFold.WaitingHref(factory),
            ChangeLabel = "Change this factory with the Fleet Manager",
            ChangeHref = "/fleet-manager?ask=" + Uri.EscapeDataString(
                $"Please change the factory {title} ({factory}). Read its map first, then show me the new map before anything goes live: "),
            StatusNote = "Each agent's box carries its status and last run, the same as on the factory's card. "
                         + "The boxes and arrows are what the factory's files say; pick a box to see its spec.",
            Legend = Legend(),
        };
        if (stored is null)
        {
            view.EmptyText = $"{title} has not published a map yet. A factory publishes its map from its own files "
                             + "(for the website factory: cc-website-factory publish-map); nobody draws it here.";
            return view;
        }

        var map = stored.Map;
        var rows = agents.ToDictionary(a => a.AgentId, StringComparer.OrdinalIgnoreCase);
        view.SourceText = $"Drawn from {map.Source}. Published {When(stored.PublishedUtc, zone)} by {stored.PublishedBy}.";
        view.Width = map.Width;
        view.Height = map.Height;
        view.Nodes = map.Nodes.Select(n => Node(n, rows)).ToList();
        view.Edges = map.Edges.Select(Edge).ToList();
        return view;
    }

    private static FactoryMapNodeDto Node(FactoryMapNodeRequest n, IReadOnlyDictionary<string, FactoryAgentRowDto> rows)
    {
        var dto = new FactoryMapNodeDto
        {
            Id = n.Id,
            Kind = n.Kind,
            Title = n.Title,
            Lines = n.Lines.ToList(),
            Dashed = !n.Built,
            X = n.X,
            Y = n.Y,
            Width = n.Width,
            Height = n.Height,
            Tone = n.Kind == FactoryMapNodeKind.Owner ? FactoryTone.Amber : FactoryTone.Neutral,
        };
        var spec = new List<FactoryMapSpecRow>();
        if (n.Kind == FactoryMapNodeKind.Agent)
        {
            if (!n.Built)
            {
                dto.Tone = FactoryTone.Grey;
                spec.Add(new() { Label = "Status", Text = "Planned: not built yet." });
            }
            else if (rows.TryGetValue(n.Id, out var row))
            {
                dto.StatusWord = row.StatusWord;
                dto.Tone = row.StatusTone;
                dto.LastRun = row.LastRun;
                dto.Href = row.Href;
                spec.Add(new() { Label = "Status", Text = $"{row.StatusWord}. {row.LastRun}." });
                spec.Add(new() { Label = "Woken by (on the Gateway)", Text = row.WokenBy });
            }
            else
            {
                dto.Tone = FactoryTone.Idle;
                spec.Add(new() { Label = "Status", Text = "Nothing recorded yet: no trigger names it and it has written nothing to the record." });
            }
        }
        spec.AddRange(n.Spec);
        dto.Spec = spec;
        return dto;
    }

    private static FactoryMapEdgeDto Edge(FactoryMapEdgeRequest e)
    {
        var (tone, line) = e.Kind switch
        {
            FactoryMapEdgeKind.Chain => (e.Result switch
            {
                FactoryMapResults.Succeeded => FactoryTone.Ok,
                FactoryMapResults.Failed => FactoryTone.Red,
                FactoryMapResults.NeedsYou => FactoryTone.Amber,
                _ => FactoryTone.Neutral,
            }, Solid),
            FactoryMapEdgeKind.Notify => (FactoryTone.Red, Solid),
            FactoryMapEdgeKind.Asks => (FactoryTone.Amber, Solid),
            FactoryMapEdgeKind.Call => (FactoryTone.Neutral, Dashed),
            FactoryMapEdgeKind.Escalate => (FactoryTone.Neutral, Dashed),
            FactoryMapEdgeKind.Trigger => (FactoryTone.Neutral, Dotted),
            _ => throw new InvalidOperationException($"A stored arrow has kind '{e.Kind}', which the store refuses."),
        };
        if (!e.Enabled) tone = FactoryTone.Grey;
        return new FactoryMapEdgeDto
        {
            From = e.From,
            To = e.To,
            Label = e.Label,
            Tone = tone,
            Line = line,
            Path = PathOf(e.Points),
            Head = e.Tip is null ? null : HeadOf(e.Points[^1], e.Tip),
            LabelX = e.LabelX,
            LabelY = e.LabelY,
        };
    }

    private static List<FactoryMapLegendDto> Legend() => new()
    {
        new() { Text = "On success, starts the next agent", Tone = FactoryTone.Ok, Line = Solid },
        new() { Text = "On failure, emails you", Tone = FactoryTone.Red, Line = Solid },
        new() { Text = "Waits for your yes", Tone = FactoryTone.Amber, Line = Solid },
        new() { Text = "Calls another agent inside its run", Tone = FactoryTone.Neutral, Line = Dashed },
        new() { Text = "Woken from outside", Tone = FactoryTone.Neutral, Line = Dotted },
        new() { Text = "Not switched on", Tone = FactoryTone.Grey, Line = Solid },
    };

    /// <summary>An SVG path from a start point and whole cubic Bezier segments.</summary>
    internal static string PathOf(IReadOnlyList<double[]> points)
    {
        var sb = new StringBuilder("M ").Append(N(points[0][0])).Append(' ').Append(N(points[0][1]));
        for (var i = 1; i + 2 < points.Count; i += 3)
        {
            sb.Append(" C");
            for (var j = 0; j < 3; j++)
                sb.Append(' ').Append(N(points[i + j][0])).Append(' ').Append(N(points[i + j][1]));
        }
        return sb.ToString();
    }

    /// <summary>A small triangle from the path's end to the tip: the arrowhead.</summary>
    internal static string HeadOf(double[] end, double[] tip)
    {
        var dx = tip[0] - end[0];
        var dy = tip[1] - end[1];
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return $"{N(tip[0])},{N(tip[1])}";
        var (ux, uy) = (dx / len, dy / len);
        const double half = 3.5;
        var left = (end[0] - uy * half, end[1] + ux * half);
        var right = (end[0] + uy * half, end[1] - ux * half);
        return $"{N(tip[0])},{N(tip[1])} {N(left.Item1)},{N(left.Item2)} {N(right.Item1)},{N(right.Item2)}";
    }

    private static string N(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

    private static string When(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }
}
