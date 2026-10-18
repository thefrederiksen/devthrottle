namespace CcDirector.Gateway.Contracts;

/// <summary>
/// What a factory does, drawn (owner, 18 October 2026): a small slice of BPMN, not executable. Each seat is a lane;
/// each lane runs from how the seat starts (a clock, an interval, or a person), through the steps its definition
/// lists, to how it ends. A decision is a diamond with one branch off the main line. The newest run is painted on the
/// start and the end. Every position, word and tone is decided on the Gateway; the Cockpit only draws it.
/// </summary>
public sealed class FactoryDrawingViewDto
{
    public string FactoryId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>The drawing's size, in the units every position below uses.</summary>
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>
    /// How the newest run went, in one line for the top of the Overview: the worst seat's, with the reason when it went
    /// wrong ("The midnight run of the Cleaner did not start."). Null when no seat has run yet.
    /// </summary>
    public FactoryDrawingStatusDto? Status { get; set; }

    public List<FactoryDrawingLaneDto> Lanes { get; set; } = new();
    public List<FactoryDrawingNodeDto> Nodes { get; set; } = new();
    public List<FactoryDrawingArrowDto> Arrows { get; set; } = new();

    /// <summary>What each shape and colour means, in the order shown under the drawing.</summary>
    public List<FactoryDrawingLegendDto> Legend { get; set; } = new();

    /// <summary>What the drawing rests on, in a sentence or two, shown under it.</summary>
    public List<string> Notes { get; set; } = new();
}

public sealed class FactoryDrawingStatusDto
{
    /// <summary>"The midnight run of the Cleaner did not start."</summary>
    public string Text { get; set; } = "";

    /// <summary>The reason in the run's own words, or null when it went well.</summary>
    public string? Sub { get; set; }
    public string Tone { get; set; } = FactoryTone.Neutral;
}

/// <summary>One seat's lane.</summary>
public sealed class FactoryDrawingLaneDto
{
    public string SeatId { get; set; } = "";

    /// <summary>"Cleaner".</summary>
    public string Title { get; set; } = "";

    /// <summary>"SOREN_NORTH".</summary>
    public string Sub { get; set; } = "";

    /// <summary>The seat's own page.</summary>
    public string Href { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>One shape on a lane.</summary>
public sealed class FactoryDrawingNodeDto
{
    /// <summary>Unique in the drawing: "cleaner/start", "cleaner/3", "cleaner/3/branch".</summary>
    public string Id { get; set; } = "";
    public string SeatId { get; set; } = "";

    /// <summary>One of <see cref="FactoryDrawingKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The words inside a box or a diamond; under a circle.</summary>
    public string Title { get; set; } = "";

    /// <summary>The second line, or null.</summary>
    public string? Sub { get; set; }
    public string Tone { get; set; } = FactoryTone.Neutral;

    /// <summary>Whether the step repeats: drawn as a circling arrow on the box.</summary>
    public bool Repeats { get; set; }

    /// <summary>Drawn dashed: the seat lists no steps yet, so this box stands for its brief.</summary>
    public bool Dashed { get; set; }

    /// <summary>The panel a click opens, or null for a shape with nothing more to say.</summary>
    public FactoryDrawingPanelDto? Panel { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>What a step says when it is opened: its heading, its lines, and the command it runs.</summary>
public sealed class FactoryDrawingPanelDto
{
    /// <summary>"Worktree sweep (step 2 of 9, Cleaner)".</summary>
    public string Heading { get; set; } = "";
    public List<string> Lines { get; set; } = new();

    /// <summary>The command exactly as the definition gives it, or null.</summary>
    public string? Runs { get; set; }
}

/// <summary>One arrow, as an SVG path of whole cubic segments, with its head and optional label.</summary>
public sealed class FactoryDrawingArrowDto
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Path { get; set; } = "";
    public string Head { get; set; } = "";
    public string? Label { get; set; }
    public double LabelX { get; set; }
    public double LabelY { get; set; }
    public string Tone { get; set; } = FactoryTone.Neutral;

    /// <summary>Dashed: the run did not take this way, or nothing ran.</summary>
    public bool Dashed { get; set; }
}

public sealed class FactoryDrawingLegendDto
{
    /// <summary>One of <see cref="FactoryDrawingKinds"/>, or "tone" for a colour swatch.</summary>
    public string Kind { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Neutral;
    public string Text { get; set; } = "";
}

/// <summary>The shapes a factory drawing has.</summary>
public static class FactoryDrawingKinds
{
    /// <summary>A start at a time (a clock).</summary>
    public const string StartClock = "start-clock";

    /// <summary>A start on an interval, when its check says so.</summary>
    public const string StartEvery = "start-every";

    /// <summary>A seat nothing starts: a person talks to it.</summary>
    public const string StartTalk = "start-talk";
    public const string Task = "task";
    public const string Decision = "decision";

    /// <summary>The seat finishes and closes itself.</summary>
    public const string EndDone = "end-done";

    /// <summary>The seat stops and waits for a person.</summary>
    public const string EndStop = "end-stop";
    public const string Tone = "tone";
}
