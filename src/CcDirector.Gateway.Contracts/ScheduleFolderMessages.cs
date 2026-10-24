namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Issue #4421: the FOLDER CHECK a schedule's save asks the target machine's Director. One verb, addressed to no
/// session: does this path exist on your disk, and is it a folder. The Gateway asks it when a schedule is created or
/// changed, so a folder the machine does not have is refused at the save instead of failing every run.
/// </summary>
public static class ScheduleFolderCheckContract
{
    /// <summary>The verb. A Director older than it answers "unknown verb", which the Gateway reads as "too old to ask".</summary>
    public const string Verb = "folder-check";
}

/// <summary>The payload of <see cref="ScheduleFolderCheckContract.Verb"/>: the path, exactly as the schedule stores it.</summary>
public sealed class FolderCheckRequest
{
    public string Path { get; set; } = "";
}

/// <summary>
/// The Director's answer to <see cref="ScheduleFolderCheckContract.Verb"/>. The path is tested exactly as the session
/// create tests it (<c>Directory.Exists</c> on the raw path), so a folder the check accepts is one a run can start in.
/// </summary>
public sealed class FolderCheckAnswer
{
    /// <summary>True when something - a folder or a file - is at the path.</summary>
    public bool Exists { get; set; }

    /// <summary>True when what is at the path is a folder.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>The system the Director runs on (<see cref="DirectorOs"/>), so the refusal can name it.</summary>
    public string Os { get; set; } = "";
}

/// <summary>
/// Issue #4421: what the Schedule form offers for a schedule's folder on one machine (GET /cron/folder-choices).
/// Every word in it is the Gateway's: the form shows the placeholder and the note as they arrive, and lists the
/// repositories in the order they arrive (the one repository list's order).
/// </summary>
public sealed class CronFolderChoicesDto
{
    /// <summary>The machine asked about.</summary>
    public string Machine { get; set; } = "";

    /// <summary>The system the machine last reported (<see cref="DirectorOs"/>), or "" when the Gateway does not know it.</summary>
    public string Os { get; set; } = "";

    /// <summary>The system's display name: "Mac", "Windows", "Linux", or "Unknown".</summary>
    public string OsName { get; set; } = "";

    /// <summary>True when a Director on the machine is connected now, so a save can check the folder.</summary>
    public bool Online { get; set; }

    /// <summary>An example folder in this system's shape, for the empty folder box.</summary>
    public string Placeholder { get; set; } = "";

    /// <summary>One line under the folder box, or null when there is nothing to say (for example: the machine is offline).</summary>
    public string? Note { get; set; }

    /// <summary>The machine's known repositories, in the one repository list's order.</summary>
    public List<KnownRepositoryDto> Repositories { get; set; } = new();
}
