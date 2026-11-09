namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Who a watch view is folded for (Watch your factory, version one). The owner sees the factory's own words; a
/// contributor sees only what an allow-list lets through (piece 3). The audience is decided by the Gateway from who
/// is asking, never sent by a client.
/// </summary>
public enum FactoryWatchAudience
{
    Owner,
    Contributor,
}

/// <summary>How a factory's floor is laid out, picked on the Gateway from the factory's own shape.</summary>
public static class FactoryWatchLayout
{
    /// <summary>Up to 3 seats and no lines: the benches stand in one row, the Boss first.</summary>
    public const string Workshop = "workshop";

    /// <summary>4 or more seats and no lines: the Boss in the middle, the others on rings around it (as many rings
    /// as the seats need, so no station overlaps another).</summary>
    public const string Hub = "hub";

    /// <summary>Any seat names a production line: one hall per line, the Boss's office above them.</summary>
    public const string Lines = "lines";
}

/// <summary>
/// What a step on the watch timeline is. A closed list: every factory activity outcome maps to exactly one of these
/// on the Gateway, and the player draws each one its own way.
/// </summary>
public static class FactoryWatchStepKind
{
    /// <summary>A seat woke and started work.</summary>
    public const string Woke = "woke";

    /// <summary>A seat finished a piece of work.</summary>
    public const string Delivered = "delivered";

    /// <summary>A seat said something: a talk, a question, a hand-up to a person.</summary>
    public const string Talked = "talked";

    /// <summary>A seat checked for work and found nothing; one step can stand for a run of such checks.</summary>
    public const string CheckedNothing = "checked-nothing";

    /// <summary>Something went wrong: a failure, a block, work sent back.</summary>
    public const string Hiccup = "hiccup";

    /// <summary>CONTRIBUTOR VIEW ONLY. The seat could not start or did not finish as scheduled - a machine problem,
    /// which a contributor never sees as one: "Your request is waiting its turn." The owner view never emits it.</summary>
    public const string Waiting = "waiting";

    /// <summary>Every step kind the owner view emits.</summary>
    public static readonly string[] All = { Woke, Delivered, Talked, CheckedNothing, Hiccup };

    /// <summary>Every step kind the contributor view may emit: the owner's, and <see cref="Waiting"/>.</summary>
    public static readonly string[] AllForContributor = { Woke, Delivered, Talked, CheckedNothing, Hiccup, Waiting };
}

/// <summary>
/// The watch view of ONE factory over a time window, finished on the Gateway (critical rule 7): where every station
/// stands, which character stands for which seat and in what colour, and what happened, in order. The player only
/// draws it. Positions are in floor tiles; the player chooses how a tile is drawn (the mockup draws them isometric).
/// </summary>
public sealed class FactoryWatchViewDto
{
    public string FactoryId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>One of <see cref="FactoryWatchLayout"/>.</summary>
    public string Layout { get; set; } = FactoryWatchLayout.Workshop;

    /// <summary>The floor's size in tiles.</summary>
    public double FloorWidth { get; set; }
    public double FloorHeight { get; set; }

    public List<FactoryWatchStationDto> Stations { get; set; } = new();

    /// <summary>The production-line halls; empty unless <see cref="Layout"/> is <see cref="FactoryWatchLayout.Lines"/>.</summary>
    public List<FactoryWatchLineDto> Lines { get; set; } = new();

    /// <summary>One character per seat.</summary>
    public List<FactoryWatchCharacterDto> Characters { get; set; } = new();

    /// <summary>What happened, oldest first.</summary>
    public List<FactoryWatchStepDto> Steps { get; set; } = new();

    public FactoryWindowDto Window { get; set; } = new();

    /// <summary>Set only when there are no steps: "Nothing happened in this factory in the last 24 hours."</summary>
    public string? EmptyText { get; set; }

    /// <summary>Set when the window held more rows than one read returns.</summary>
    public string? TruncatedText { get; set; }

    /// <summary>What the view rests on, in plain sentences.</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>One seat's station on the floor.</summary>
public sealed class FactoryWatchStationDto
{
    /// <summary>The seat's id: the key the characters and steps name. Not for display.</summary>
    public string Id { get; set; } = "";

    /// <summary>The seat's name, for example "Website Desk".</summary>
    public string Label { get; set; } = "";

    /// <summary>The seat's role in words, for example "Boss".</summary>
    public string Sub { get; set; } = "";

    public bool IsBoss { get; set; }

    /// <summary>The station's top-left corner and size, in floor tiles.</summary>
    public double Gx { get; set; }
    public double Gy { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>The production line the station stands in, or null.</summary>
    public string? Line { get; set; }
}

/// <summary>One production line's hall.</summary>
public sealed class FactoryWatchLineDto
{
    public string Name { get; set; } = "";

    /// <summary>The hall's place in the order, from 0; the player picks a hue from it.</summary>
    public int Index { get; set; }

    public double Gx { get; set; }
    public double Gy { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>The character that stands for one seat - never for an agent or a session.</summary>
public sealed class FactoryWatchCharacterDto
{
    /// <summary>The seat it stands for: a station id. Not for display.</summary>
    public string Seat { get; set; } = "";

    /// <summary>The seat's name, for example "Boss".</summary>
    public string Name { get; set; } = "";

    /// <summary>The character's colour, "#rrggbb". The same seat keeps the same colour from call to call.</summary>
    public string Colour { get; set; } = "";

    /// <summary>The station it works at.</summary>
    public string Station { get; set; } = "";

    /// <summary>Where it stands at its bench, in floor tiles.</summary>
    public double HomeGx { get; set; }
    public double HomeGy { get; set; }
}

/// <summary>One step on the watch timeline.</summary>
public sealed class FactoryWatchStepDto
{
    /// <summary>A stable key: the row's id, or the first row's id for a run of empty checks.</summary>
    public string Key { get; set; } = "";

    /// <summary>One of <see cref="FactoryWatchStepKind"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The seat that acted: a station id. Not for display.</summary>
    public string Seat { get; set; } = "";

    /// <summary>When it happened; for a run of empty checks, when the first ran.</summary>
    public DateTime Utc { get; set; }

    /// <summary>For a run of empty checks, when the last ran; otherwise null.</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>The time in the account's zone, as the Activity tab writes it: "02:00", or "02:05-04:05" for a run.</summary>
    public string Time { get; set; } = "";

    /// <summary>The sentence to show.</summary>
    public string Words { get; set; } = "";

    /// <summary>CONTRIBUTOR VIEW: true when the step is the contributor's own work (their crate); false for other work,
    /// which is a plain crate with no words. Always false in the owner view.</summary>
    public bool Mine { get; set; }

    /// <summary>The Gateway's verdict on whether the step is drawn as an alarm (the red tone): true only for a
    /// <see cref="FactoryWatchStepKind.Hiccup"/> in the OWNER view. False for every other step, and for every step in
    /// the contributor view, hiccups included. The player draws the tone from this and never decides it by audience.</summary>
    public bool Alarm { get; set; }

    /// <summary>How many empty checks the step stands for: at least 1 for <see cref="FactoryWatchStepKind.CheckedNothing"/>,
    /// 0 for every other kind.</summary>
    public int EmptyChecks { get; set; }
}
