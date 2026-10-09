namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A factory's floor (owner decision, 8 October 2026): the factory drawn flat, as production lines. The Boss's office
/// runs across the top, each line is a lane of bays (one bay per seat, with its light), arrows are what the factory
/// published in its map, and the owner's desk is the far end where what needs the owner arrives. Every position, word
/// and tone is decided on the Gateway; the Cockpit only draws it.
/// </summary>
public sealed class FactoryFloorViewDto
{
    public string FactoryId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>The drawing's size, in the units every position below uses.</summary>
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>The Boss's office across the top; null when the factory has no boss.</summary>
    public FactoryFloorOfficeDto? Office { get; set; }

    public List<FactoryFloorLaneDto> Lanes { get; set; } = new();
    public List<FactoryFloorBayDto> Bays { get; set; } = new();
    public List<FactoryFloorArrowDto> Arrows { get; set; } = new();
    public FactoryFloorDeskDto Desk { get; set; } = new();
    public List<FactoryMapLegendDto> Legend { get; set; } = new();

    /// <summary>What the drawing rests on, in a sentence or two, shown under it.</summary>
    public List<string> Notes { get; set; } = new();
}

public sealed class FactoryFloorOfficeDto
{
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Neutral;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>One production line: a named lane across the floor.</summary>
public sealed class FactoryFloorLaneDto
{
    public string Name { get; set; } = "";

    /// <summary>The lane's colour, by its place on the floor (0 to 3, then round again), so neighbours differ.</summary>
    public int Hue { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>One bay: a seat (or a source of work such as a mailbox) with its light.</summary>
public sealed class FactoryFloorBayDto
{
    public string Id { get; set; } = "";

    /// <summary>"seat" or "source".</summary>
    public string Kind { get; set; } = "seat";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Neutral;

    /// <summary>The light's meaning in words, for a tooltip: the seat's last run as the Seats tab says it.</summary>
    public string ToneText { get; set; } = "";

    /// <summary>A seat the factory's map marks as not built yet.</summary>
    public bool Dashed { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>Where clicking the bay goes; null for a source.</summary>
    public string? Href { get; set; }
}

public sealed class FactoryFloorArrowDto
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Label { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Neutral;

    /// <summary>"solid", "dashed" or "dotted".</summary>
    public string Line { get; set; } = "solid";

    /// <summary>The SVG path, finished.</summary>
    public string Path { get; set; } = "";

    /// <summary>The arrowhead's three points, finished.</summary>
    public string Head { get; set; } = "";
    public double LabelX { get; set; }
    public double LabelY { get; set; }
}

/// <summary>The owner's desk: what needs the owner, from the same failures and questions the factory page shows.</summary>
public sealed class FactoryFloorDeskDto
{
    public string Title { get; set; } = "Your desk";
    public string Sub { get; set; } = "approve, answer, read";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<FactoryFloorDeskItemDto> Items { get; set; } = new();

    /// <summary>"Nothing is waiting on you." when there are no items; else null.</summary>
    public string? EmptyText { get; set; }

    /// <summary>A line under the items, for example that failures also reach the owner by email; null when none.</summary>
    public string? Note { get; set; }
}

public sealed class FactoryFloorDeskItemDto
{
    /// <summary>Who, and when: "Sender, today 02:40". Empty for the "and more" line.</summary>
    public string Title { get; set; } = "";

    /// <summary>What it is, in the factory's words.</summary>
    public string Text { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Amber;
    public string? Href { get; set; }
}
