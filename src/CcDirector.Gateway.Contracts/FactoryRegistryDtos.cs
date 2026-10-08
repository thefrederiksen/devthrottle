using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One factory's registration, as <c>cc-devthrottle factory register --manifest</c> sends it (Factories screen
/// mission, phase A). The registry is an INDEX of a factory, not its definition: the briefs stay on disk and the
/// registry says where they are, which seat is the boss, which schedules run each seat, and what the owner's goal is.
/// Registering a factory that is already registered replaces its whole row, seats included.
/// </summary>
public sealed class RegisterFactoryRequest
{
    /// <summary>The factory id: lower-case letters and digits joined by hyphens (the record's own spelling).</summary>
    public string Factory { get; set; } = "";

    /// <summary>The factory's name as the owner reads it, for example <c>WarmForward</c>.</summary>
    public string Title { get; set; } = "";

    /// <summary>The factory's folder, an absolute path on <see cref="Computer"/>.</summary>
    public string Folder { get; set; } = "";

    /// <summary>The computer (machine name) the factory runs on.</summary>
    public string Computer { get; set; } = "";

    /// <summary>The id of the seat that is the factory's boss, or null when it has none. The boss seat's name is the
    /// word Boss - the boss has no name of its own - and its role is "Boss" or a distinct word the factory chose ("CFO").</summary>
    public string? BossSeat { get; set; }

    /// <summary>The goal's text, or null when the factory has no goal yet. The command reads it from the goal
    /// file the manifest names; the Gateway never reads a file.</summary>
    public string? GoalText { get; set; }

    /// <summary>The goal file the text came from, relative to <see cref="Folder"/>, kept so the page can say where
    /// the goal lives. Null when there is no goal.</summary>
    public string? GoalFile { get; set; }

    /// <summary>The day the owner approved the goal (<c>YYYY-MM-DD</c>), or null.</summary>
    public string? GoalApprovedOn { get; set; }

    /// <summary>One line on what the factory is for, as the owner's Factories cards show it under the name
    /// ("Builds and sells websites for local trades"). Trimmed; at most <c>FactoryRegistryStore.MaxPurposeChars</c>
    /// characters; null or empty when the factory has none yet.</summary>
    public string? Purpose { get; set; }

    /// <summary>Every seat of the factory. At least one.</summary>
    public List<FactorySeatManifest> Seats { get; set; } = new();

    /// <summary>Every key the body carried that this contract does not name. The registry refuses the whole
    /// registration when there is one, naming the key and the fix: the default binding drops an unknown member
    /// without a word, which is how a manifest still saying <c>ceoSeat</c> (the key's name until 8 October 2026)
    /// would have registered a factory with no boss and no error.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; set; }
}

/// <summary>One seat of a factory: one agent the boss hired, as registered.</summary>
public sealed class FactorySeatManifest
{
    /// <summary>The seat id - the same spelling the factory activity record uses for the factory agent.</summary>
    public string Id { get; set; } = "";

    /// <summary>The seat's display name, for example <c>Nora Hale</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>The seat's role, for example <c>Boss</c> or <c>Savings Engineer</c>.</summary>
    public string Role { get; set; } = "";

    /// <summary>The seat's brief, relative to the factory's folder.</summary>
    public string BriefFile { get; set; } = "";

    /// <summary>The Gateway schedules that run this seat. May be empty for a seat nothing runs yet.</summary>
    public List<string> Schedules { get; set; } = new();

    /// <summary>The computer this seat runs on. Left out, it is the factory's computer.</summary>
    public string? Computer { get; set; }
}

/// <summary>One registered factory, as the registry holds it.</summary>
public sealed class RegisteredFactoryDto
{
    public string Factory { get; set; } = "";
    public string Title { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Computer { get; set; } = "";
    public string? BossSeat { get; set; }
    public string? GoalText { get; set; }
    public string? GoalFile { get; set; }
    public string? GoalApprovedOn { get; set; }

    /// <summary>One line on what the factory is for, or null when none is set.</summary>
    public string? Purpose { get; set; }

    public List<RegisteredFactorySeatDto> Seats { get; set; } = new();

    /// <summary>Who registered it: <c>session &lt;id&gt;</c> or <c>the owner (&lt;credential&gt;)</c>.</summary>
    public string RegisteredBy { get; set; } = "";

    public DateTime RegisteredAtUtc { get; set; }

    /// <summary>When the owner archived it, or null while it is on the Factories list (round 2).</summary>
    public DateTime? ArchivedAtUtc { get; set; }

    /// <summary>Who archived it, or null.</summary>
    public string? ArchivedBy { get; set; }

    /// <summary>The schedule ids the archive switched off - what Restore switches back on. Empty while not archived.</summary>
    public List<string> ArchivedSchedules { get; set; } = new();
}

/// <summary>One registered seat. Its computer is always set: a seat registered without one runs on the
/// factory's computer, and that is written down at registration rather than worked out by every reader.</summary>
public sealed class RegisteredFactorySeatDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string BriefFile { get; set; } = "";
    public List<string> Schedules { get; set; } = new();
    public string Computer { get; set; } = "";
}

/// <summary>
/// <c>PUT /gateway/factory/registry/{factory}/purpose</c>: set or clear a registered factory's one-line purpose
/// without registering it again (<c>cc-devthrottle factory purpose</c>). Null or empty clears it.
/// </summary>
public sealed class SetFactoryPurposeRequest
{
    public string? Purpose { get; set; }
}

/// <summary>Every registered factory in the account, by title.</summary>
public sealed class FactoryRegistryListDto
{
    public int Count { get; set; }
    public List<RegisteredFactoryDto> Factories { get; set; } = new();
}

/// <summary>A factory's goal number, as its boss posts it on a run (Factories screen mission, phase A).</summary>
public sealed class PostGoalNumberRequest
{
    /// <summary>The registered factory the number is for.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The number, as text, so "not yet proven" or "1,240" is posted as it is meant.</summary>
    public string Value { get; set; } = "";

    /// <summary>What the number counts, for example <c>dollars saved this season</c>.</summary>
    public string Unit { get; set; } = "";

    /// <summary>The day the number is as of (<c>YYYY-MM-DD</c>).</summary>
    public string AsOf { get; set; } = "";

    /// <summary>A link (http or https) to how the number was measured.</summary>
    public string Link { get; set; } = "";

    /// <summary>The seat that posts it. Left out when the call comes from a factory agent's own session: the
    /// Gateway takes the seat from that session's start in the activity record.</summary>
    public string? PostedBy { get; set; }
}

/// <summary>One posted goal number. Every post is kept; the newest is the one the factory's page shows.</summary>
public sealed class GoalNumberDto
{
    public Guid Id { get; set; }
    public string Factory { get; set; } = "";
    public string Value { get; set; } = "";
    public string Unit { get; set; } = "";
    public string AsOf { get; set; } = "";
    public string Link { get; set; } = "";

    /// <summary>The seat id that posted it.</summary>
    public string PostedBy { get; set; } = "";

    /// <summary>The session that made the call, when it was a session.</summary>
    public string? PostedBySession { get; set; }

    public DateTime PostedAtUtc { get; set; }
}

/// <summary>A factory's goal numbers, newest first. <see cref="Latest"/> is null when none was ever posted.</summary>
public sealed class GoalNumbersDto
{
    public string Factory { get; set; } = "";
    public GoalNumberDto? Latest { get; set; }
    public int Count { get; set; }
    public List<GoalNumberDto> Posts { get; set; } = new();
}

/// <summary>
/// The answer of <c>POST /gateway/factory-agents/factories/{factory}/seats/{seat}/talk</c> (Factories screen mission,
/// phase C): the top-level session the owner's Talk button started, seated as that seat, and where the Cockpit opens it.
/// </summary>
public sealed class FactoryTalkStartedDto
{
    /// <summary>The new session's id.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Its name: <c>&lt;Factory title&gt; - &lt;Seat name&gt; - talk with the owner</c>.</summary>
    public string SessionName { get; set; } = "";

    /// <summary>The Cockpit address that opens it (<c>/session/&lt;id&gt;</c>).</summary>
    public string Href { get; set; } = "";

    public string Factory { get; set; } = "";
    public string Seat { get; set; } = "";

    /// <summary>The computer it was started on: the seat's.</summary>
    public string Computer { get; set; } = "";

    /// <summary>The Director on that computer that started it.</summary>
    public string DirectorId { get; set; } = "";
}
