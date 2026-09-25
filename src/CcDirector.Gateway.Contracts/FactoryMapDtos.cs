namespace CcDirector.Gateway.Contracts;

// THE FACTORY MAP (issue #3383): how a factory's agents connect, drawn in the Cockpit on the factory's Map tab.
//
// A factory is defined in its own files and changed by talking to an agent; nobody draws it. The factory's tool
// lays the map out with Graphviz and PUBLISHES it here (PUT /gateway/factory/map): every box with its place and
// the facts about the agent it stands for, every arrow with its path. The Gateway keeps the latest map per
// factory and, when the owner opens it, adds what only the Gateway knows - each agent's status and last run, the
// same words the Factories tab shows - and returns it finished (critical rule 7). The Cockpit draws it verbatim.

/// <summary>The kinds of box on a map.</summary>
public static class FactoryMapNodeKind
{
    /// <summary>A factory agent: the only kind the Gateway adds live status to.</summary>
    public const string Agent = "agent";

    /// <summary>The person at the end of every chain.</summary>
    public const string Owner = "owner";

    /// <summary>Something outside the factory that wakes an agent: a mailbox, a queue.</summary>
    public const string Source = "source";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Agent, Owner, Source };
}

/// <summary>The kinds of arrow on a map. The Gateway turns each into a tone and a line style.</summary>
public static class FactoryMapEdgeKind
{
    /// <summary>One agent's result starts another (on success: ok; on failure: red).</summary>
    public const string Chain = "chain";

    /// <summary>A failed run emails the owner.</summary>
    public const string Notify = "notify";

    /// <summary>An agent waits for the owner's yes.</summary>
    public const string Asks = "asks";

    /// <summary>One agent calls another inside its run.</summary>
    public const string Call = "call";

    /// <summary>An agent hands something to the owner.</summary>
    public const string Escalate = "escalate";

    /// <summary>Something outside the factory wakes an agent.</summary>
    public const string Trigger = "trigger";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Chain, Notify, Asks, Call, Escalate, Trigger };
}

/// <summary>One label and its words, for an agent's spec beside the map ("Inputs": "draft-queue: ...").</summary>
public sealed class FactoryMapSpecRow
{
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>One box as the factory publishes it. Coordinates are Graphviz points, origin top left.</summary>
public sealed class FactoryMapNodeRequest
{
    /// <summary>For an agent, its factory agent id, the same id the record and the triggers use.</summary>
    public string Id { get; set; } = "";

    public string Kind { get; set; } = FactoryMapNodeKind.Agent;
    public string Title { get; set; } = "";

    /// <summary>The small lines under the title, as the files say them ("daily 07:00", "claude-code").</summary>
    public List<string> Lines { get; set; } = new();

    /// <summary>False for an agent the files say is planned, not built yet: drawn dashed.</summary>
    public bool Built { get; set; } = true;

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>The agent's spec, shown beside the map when the box is picked.</summary>
    public List<FactoryMapSpecRow> Spec { get; set; } = new();
}

/// <summary>One arrow as the factory publishes it.</summary>
public sealed class FactoryMapEdgeRequest
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Kind { get; set; } = FactoryMapEdgeKind.Chain;

    /// <summary>For a chain or a notify: the run result it listens for ("succeeded", "failed", ...).</summary>
    public string? Result { get; set; }

    /// <summary>The words on the arrow ("succeeded: starts").</summary>
    public string Label { get; set; } = "";

    /// <summary>False when the file says what drives this arrow is not switched on: drawn grey.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The path: a start point then cubic Bezier segments, 1 + 3n points, as Graphviz lays them out.</summary>
    public List<double[]> Points { get; set; } = new();

    /// <summary>Where the arrow ends (the tip of the arrowhead); null draws no head.</summary>
    public double[]? Tip { get; set; }

    public double? LabelX { get; set; }
    public double? LabelY { get; set; }
}

/// <summary>Body of <c>PUT /gateway/factory/map</c>: one factory's map, replacing the one before.</summary>
public sealed class PublishFactoryMapRequest
{
    public string Factory { get; set; } = "";

    /// <summary>The factory's name as its files say it ("Website Business").</summary>
    public string Title { get; set; } = "";

    /// <summary>Where the map was drawn from, in words ("factory.yaml in cc-consult at 687a1e9").</summary>
    public string Source { get; set; } = "";

    public double Width { get; set; }
    public double Height { get; set; }
    public List<FactoryMapNodeRequest> Nodes { get; set; } = new();
    public List<FactoryMapEdgeRequest> Edges { get; set; } = new();
}

/// <summary>What <c>PUT /gateway/factory/map</c> answers.</summary>
public sealed class PublishFactoryMapResponse
{
    public string Factory { get; set; } = "";
    public int Nodes { get; set; }
    public int Edges { get; set; }
    public DateTime PublishedUtc { get; set; }
}

/// <summary>One box, finished.</summary>
public sealed class FactoryMapNodeDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = FactoryMapNodeKind.Agent;
    public string Title { get; set; } = "";
    public List<string> Lines { get; set; } = new();

    /// <summary>The agent's status word from the Factories tab ("IDLE", "PAUSED"), or null for a box with none.</summary>
    public string? StatusWord { get; set; }

    /// <summary>The box's tone: its status tone for an agent the Gateway knows, otherwise neutral or grey.</summary>
    public string Tone { get; set; } = FactoryTone.Neutral;

    /// <summary>The agent's last run in words ("07:02 - 1 done"), or null.</summary>
    public string? LastRun { get; set; }

    /// <summary>True draws the box dashed: planned, not built yet.</summary>
    public bool Dashed { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>The spec rows shown beside the map when the box is picked; the live rows come first.</summary>
    public List<FactoryMapSpecRow> Spec { get; set; } = new();

    /// <summary>The factory agent's page, or null for a box that has none.</summary>
    public string? Href { get; set; }
}

/// <summary>One arrow, finished.</summary>
public sealed class FactoryMapEdgeDto
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Label { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Neutral;

    /// <summary>"solid", "dashed" or "dotted".</summary>
    public string Line { get; set; } = "solid";

    /// <summary>An SVG path ("M x y C x y x y x y ...").</summary>
    public string Path { get; set; } = "";

    /// <summary>The arrowhead as an SVG polygon's points, or null for none.</summary>
    public string? Head { get; set; }

    public double? LabelX { get; set; }
    public double? LabelY { get; set; }
}

/// <summary>One line of the legend under the map.</summary>
public sealed class FactoryMapLegendDto
{
    public string Text { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Neutral;
    public string Line { get; set; } = "solid";
}

/// <summary><c>GET /gateway/factory-agents/factories/{factory}/map</c>: the factory page's Map tab.</summary>
public sealed class FactoryMapViewDto
{
    public string FactoryId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>What the tab says when this factory has published no map, or null when there is one.</summary>
    public string? EmptyText { get; set; }

    /// <summary>"Drawn from factory.yaml in cc-consult at 687a1e9. Published 24 Sep 14:02 by ...".</summary>
    public string? SourceText { get; set; }

    /// <summary>One sentence on what the colours mean and where they come from.</summary>
    public string StatusNote { get; set; } = "";

    public double Width { get; set; }
    public double Height { get; set; }
    public List<FactoryMapNodeDto> Nodes { get; set; } = new();
    public List<FactoryMapEdgeDto> Edges { get; set; } = new();
    public List<FactoryMapLegendDto> Legend { get; set; } = new();

    /// <summary>The words on the button that opens the page a factory is changed from, and where it goes.</summary>
    public string ChangeLabel { get; set; } = "";
    public string ChangeHref { get; set; } = "";

    /// <summary>The factory's tabs, with the Map tab first.</summary>
    public List<FactoryTabDto> Tabs { get; set; } = new();

    /// <summary>The factory's agents as rows, for the Agents tab (the same rows its card shows).</summary>
    public List<FactoryAgentRowDto> Agents { get; set; } = new();

    public string WaitingHref { get; set; } = "";
}
