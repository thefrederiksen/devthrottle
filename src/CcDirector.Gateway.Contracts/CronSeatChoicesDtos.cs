namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The factories and seats a schedule may be linked to, for the schedule editor's factory and seat picker (the owner,
/// 2026-10-10). Folded by the Gateway from the account's registry, so the Cockpit lists exactly what a save would
/// accept: <c>GET /cron/seat-choices</c>.
/// </summary>
public sealed class CronSeatChoicesDto
{
    /// <summary>"No factory (Personal)" - the choice for a schedule in no factory.</summary>
    public string NoneLabel { get; set; } = "";

    /// <summary>Every registered factory that is not archived, by title, each with its seats in its manifest's order.</summary>
    public List<CronFactoryChoiceDto> Factories { get; set; } = new();
}

/// <summary>One factory a schedule may be linked to.</summary>
public sealed class CronFactoryChoiceDto
{
    /// <summary>The factory id the schedule stores.</summary>
    public string Factory { get; set; } = "";

    /// <summary>"WarmForward Factory".</summary>
    public string Title { get; set; } = "";

    public List<CronSeatChoiceDto> Seats { get; set; } = new();
}

/// <summary>One seat of a factory.</summary>
public sealed class CronSeatChoiceDto
{
    /// <summary>The seat id the schedule stores.</summary>
    public string Id { get; set; } = "";

    /// <summary>"Nora Hale (ceo)" - the seat's name with its id, or the id alone when it has no name.</summary>
    public string Label { get; set; } = "";
}
