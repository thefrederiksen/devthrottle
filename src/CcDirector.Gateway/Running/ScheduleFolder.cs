using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// What a schedule's save learned about its folder (issue #4421): a <see cref="Refusal"/> - the folder is not on the
/// machine, or is in another system's shape, and nothing may be stored - or a <see cref="Warning"/> - the folder could
/// not be checked, the schedule is saved, and its first run checks it - or neither, when the folder is there.
/// </summary>
public sealed record ScheduleFolderVerdict(string? Refusal, string? Warning)
{
    /// <summary>The folder is on the machine.</summary>
    public static readonly ScheduleFolderVerdict Fine = new(null, null);

    public static ScheduleFolderVerdict Refused(string sentence) => new(sentence, null);

    public static ScheduleFolderVerdict Unchecked(string sentence) => new(null, sentence);
}

/// <summary>
/// THE WORDS FOR A SCHEDULE'S FOLDER (issue #4421), in one place: what a save is refused with, what it is warned with,
/// what the Schedule form shows in an empty folder box, and how a run that did not start because its folder was missing
/// reads. Every sentence names the machine and, where the Gateway knows it, its system, so a person on a Mac who typed a
/// Windows path is told exactly that.
///
/// The SHAPE of a path is read here too. A drive letter (<c>C:</c>), or a backslash in a path that does not start with
/// <c>/</c> or <c>~</c>, is a Windows path: on a Mac or a Linux computer it cannot be a folder anyone meant, so it is
/// refused from the shape alone - which works while the machine is offline. A path that starts the Mac and Linux way
/// may have a backslash in a folder's name, so it is judged by the machine's own answer. The reverse is NOT refused from the shape: on Windows, <c>/Users/you</c> is read against the
/// current drive and may really exist, so a path like that on a Windows computer is judged by the machine's own answer,
/// and only a missing one is told it looks like a Mac or Linux path.
/// </summary>
public static class ScheduleFolderWords
{
    /// <summary>The folder name an example path ends in when the schedule's path gives none.</summary>
    public const string ExampleFolder = "my-project";

    /// <summary>The placeholder when the machine's system is not known.</summary>
    public const string UnknownPlaceholder = "the full path of the folder on that machine";

    /// <summary>The placeholder before a machine is chosen (issue #4421 review, finding 5: the Gateway's words, not the form's).</summary>
    public const string NoMachinePlaceholder = "Choose the machine first";

    private static readonly Regex DriveLetter = new(@"^[A-Za-z]:", RegexOptions.CultureInvariant);

    /// <summary>The words the Director's session create refuses a missing folder with ("repoPath does not exist: X").</summary>
    private const string DirectorMissing = "repoPath does not exist: ";

    /// <summary>
    /// True when the path is in the Windows shape: a drive letter, or a backslash in a path that does not start the Mac
    /// and Linux way. A path that starts with <c>/</c> or <c>~</c> is a Mac or Linux path even with a backslash in it -
    /// a folder there may have one in its name (issue #4421 review, finding 4) - so it is judged by the machine's own
    /// answer, never refused from its shape.
    /// </summary>
    public static bool IsWindowsShaped(string path) =>
        !string.IsNullOrEmpty(path)
        && (DriveLetter.IsMatch(path) || (path.Contains('\\') && path[0] != '/' && path[0] != '~'));

    /// <summary>True when the path is in the Mac or Linux shape: it starts with a forward slash or a tilde.</summary>
    public static bool IsUnixShaped(string path) =>
        !string.IsNullOrEmpty(path) && (path[0] == '/' || path[0] == '~');

    /// <summary>"a Mac", "a Windows computer", "a Linux computer"; null when the system is not known.</summary>
    public static string? AComputer(string? os) => os switch
    {
        DirectorOs.MacOs => "a Mac",
        DirectorOs.Windows => "a Windows computer",
        DirectorOs.Linux => "a Linux computer",
        _ => null,
    };

    /// <summary>An example folder in the system's shape, ending in <paramref name="folder"/>; null when the system is not known.</summary>
    public static string? Example(string? os, string folder = ExampleFolder) => os switch
    {
        DirectorOs.MacOs => $"/Users/<you>/Documents/{folder}",
        DirectorOs.Linux => $"/home/<you>/{folder}",
        DirectorOs.Windows => $@"C:\Users\<you>\Documents\{folder}",
        _ => null,
    };

    /// <summary>The empty folder box's placeholder for a machine of this system.</summary>
    public static string Placeholder(string? os) => Example(os) ?? UnknownPlaceholder;

    /// <summary>
    /// The refusal for a Windows path on a Mac or a Linux computer, or null when the path's shape fits the machine (or
    /// the machine's system is not known). The example keeps the folder's own last name, so it reads as the fix.
    /// </summary>
    public static string? WrongShape(string path, string machine, string? os)
    {
        if (os is not (DirectorOs.MacOs or DirectorOs.Linux) || !IsWindowsShaped(path))
            return null;
        return $"'{path}' is a Windows path, but {machine} is {AComputer(os)}. Use a path like {Example(os, LastName(path))}.";
    }

    /// <summary>The refusal for a folder the machine does not have.</summary>
    public static string Missing(string path, string machine, string? os)
    {
        if (WrongShape(path, machine, os) is { } wrongShape)
            return wrongShape;
        var on = AComputer(os) is { } computer ? $"{machine} ({computer})" : machine;
        var looks = os == DirectorOs.Windows && IsUnixShaped(path)
            ? $" It looks like a Mac or Linux path, but {machine} is a Windows computer. Use a path like {Example(os, LastName(path))}."
            : "";
        return $"The folder '{path}' does not exist on {on}.{looks} Choose a folder that exists on {machine}.";
    }

    /// <summary>The refusal for a path that is a file, not a folder.</summary>
    public static string NotAFolder(string path, string machine) =>
        $"'{path}' on {machine} is a file, not a folder. Choose the folder the session should open in.";

    /// <summary>The warning when no Director on the machine is connected.</summary>
    public static string Offline(string path, string machine) =>
        $"{machine} is offline, so the folder '{path}' could not be checked. It is checked again when the schedule first runs.";

    /// <summary>The warning when the machine's Director is older than the folder check.</summary>
    public static string TooOld(string path, string machine, string? version) =>
        (string.IsNullOrWhiteSpace(version)
            ? $"The Director on {machine} is too old to check the folder '{path}'"
            : $"The Director on {machine} is version {version}, which is too old to check the folder '{path}'")
        + ", so it was not checked. Update it. The folder is checked again when the schedule first runs.";

    /// <summary>The warning when the machine was asked and gave no usable answer.</summary>
    public static string NoAnswer(string path, string machine, string why) =>
        $"The folder '{path}' could not be checked on {machine}: {why}. It is checked again when the schedule first runs.";

    /// <summary>
    /// How a run that did not start because its folder was missing reads in the run table, or null when the reason is
    /// some other one. The Director refuses a missing folder with "repoPath does not exist: X"; this turns that into
    /// the same sentence a save would have refused it with.
    /// </summary>
    public static string? DidNotRunText(string? reason, string machine, string? os)
    {
        if (MissingFolderIn(reason) is not { } path) return null;
        var on = string.IsNullOrWhiteSpace(machine) ? "its machine" : machine;
        return $"did not start a session: {Missing(path, on, os)} Then edit the schedule.";
    }

    /// <summary>The folder a run's reason says its machine does not have ("repoPath does not exist: X"), or null when the
    /// reason is some other one.</summary>
    public static string? MissingFolderIn(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        var at = reason.IndexOf(DirectorMissing, StringComparison.Ordinal);
        if (at < 0) return null;
        var path = reason[(at + DirectorMissing.Length)..].Trim();
        return path.Length == 0 ? null : path;
    }

    /// <summary>The last name in a path of either shape, or <see cref="ExampleFolder"/> when it has none.</summary>
    internal static string LastName(string path)
    {
        var name = path.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault(p => p.Length > 0 && !p.EndsWith(':'));
        return string.IsNullOrWhiteSpace(name) || name == "~" ? ExampleFolder : name;
    }
}

/// <summary>
/// THE FOLDER CHECK A SCHEDULE'S SAVE MAKES (issue #4421). A schedule used to be saved with any folder at all, and a
/// folder its machine did not have failed every run with "director returned BadRequest: repoPath does not exist" -
/// which the owner of a Mac read as "schedules do not work on a Mac". Now the save asks:
///
///   1. THE SHAPE, from the system the machine last reported. A Windows path for a Mac or a Linux computer is refused
///      at once, even while the machine is offline.
///   2. THE MACHINE, through a connected Director there (<see cref="ScheduleFolderCheckContract.Verb"/>). A folder it
///      does not have is refused; a file is refused. The answer names the system, so the refusal can too.
///   3. NO ANSWER - the machine is offline, its Director is older than the check, or it did not answer in
///      <see cref="Timeout"/> - is a WARNING, never a refusal: the schedule is saved and its first run checks the
///      folder again, and a run that finds it missing says so in plain words (<see cref="ScheduleFolderWords.DidNotRunText"/>).
///
/// It never starts a Director: a save asks only one that is already connected.
/// </summary>
internal sealed class ScheduleFolderCheck
{
    /// <summary>
    /// How long a save waits for the machine: 10 seconds. A person is waiting on the save and the answer is one
    /// existence test on the machine's disk, so a machine that has not answered by then is treated as not answering.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<TenantId, IReadOnlyCollection<DirectorDto>> _directorsOf;
    private readonly Func<TenantId, string, bool> _isConnected;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _send;

    /// <param name="directorsOf">The account's registered Directors, connected or not: the system a machine last
    /// reported is read from them.</param>
    /// <param name="isConnected">Whether a Director's tunnel is connected now.</param>
    /// <param name="send">Sends a command down a Director's tunnel.</param>
    public ScheduleFolderCheck(Func<TenantId, IReadOnlyCollection<DirectorDto>> directorsOf,
        Func<TenantId, string, bool> isConnected, DirectorCommandRouter.SendDirectorCommandAsync send)
    {
        _directorsOf = directorsOf ?? throw new ArgumentNullException(nameof(directorsOf));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    /// <summary>The system the machine last reported (<see cref="DirectorOs"/>), or null when no Director there said.</summary>
    public string? OsOf(TenantId tenant, string machine) => OsOf(OnMachine(tenant, machine));

    /// <summary>True when a Director on the machine is running and connected now.</summary>
    public bool IsOnline(TenantId tenant, string machine) => Connected(tenant, OnMachine(tenant, machine)) is not null;

    /// <summary>Check one schedule's folder on its machine.</summary>
    public async Task<ScheduleFolderVerdict> CheckAsync(TenantId tenant, string machine, string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machine);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileLog.Write($"[ScheduleFolderCheck] CheckAsync: tenant={tenant.ToLogString()}, machine={machine}, path={path}");

        var onMachine = OnMachine(tenant, machine);
        var reportedOs = OsOf(onMachine);
        if (ScheduleFolderWords.WrongShape(path, machine, reportedOs) is { } wrongShape)
            return Refuse(machine, wrongShape);

        var director = Connected(tenant, onMachine);
        if (director is null)
            return Warn(machine, ScheduleFolderWords.Offline(path, machine));

        var result = await DirectorCommandRouter.TrySendAsync(_send, director.DirectorId, ScheduleFolderCheckContract.Verb, "",
            new FolderCheckRequest { Path = path }, ct, Timeout, machine);
        if (result is null)
            return Warn(machine, ScheduleFolderWords.Offline(path, machine));
        if (result.Status == DirectorCommandStatus.BadRequest
            && (result.Error ?? "").StartsWith("unknown verb", StringComparison.Ordinal))
            return Warn(machine, ScheduleFolderWords.TooOld(path, machine, director.Version));
        if (result.Status == DirectorCommandStatus.Timeout)
            return Warn(machine, ScheduleFolderWords.NoAnswer(path, machine, $"it did not answer within {Timeout.TotalSeconds:0} seconds"));
        if (!result.Ok)
            return Warn(machine, ScheduleFolderWords.NoAnswer(path, machine, result.Error ?? result.Status.ToString()));

        var answer = Read(result.BodyJson);
        if (answer is null)
            return Warn(machine, ScheduleFolderWords.NoAnswer(path, machine, "its answer was in a shape the Gateway does not read"));
        var os = DirectorOs.Normalize(answer.Os) ?? reportedOs;
        if (!answer.Exists)
            return Refuse(machine, ScheduleFolderWords.Missing(path, machine, os));
        if (!answer.IsDirectory)
            return Refuse(machine, ScheduleFolderWords.NotAFolder(path, machine));

        FileLog.Write($"[ScheduleFolderCheck] {machine}: the folder {path} is there (director={director.DirectorId})");
        return ScheduleFolderVerdict.Fine;
    }

    private IReadOnlyList<DirectorDto> OnMachine(TenantId tenant, string machine) =>
        _directorsOf(tenant).Where(d => string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The system a machine last reported, read from an account's registered Directors, or null when none there said:
    /// what a run that did not start because its folder was missing is worded with, wherever its runs are read.
    /// </summary>
    public static string? ReportedOs(IEnumerable<DirectorDto> directors, string machine)
    {
        ArgumentNullException.ThrowIfNull(directors);
        return OsOf(directors.Where(d => string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    // A running Director's report wins over one that said goodbye; the most recently seen wins among either.
    private static string? OsOf(IReadOnlyList<DirectorDto> onMachine) => onMachine
        .Where(d => DirectorOs.Normalize(d.Os) is not null)
        .OrderBy(d => d.StoppedAtUtc is null ? 0 : 1)
        .ThenByDescending(d => d.LastSeen ?? d.StartedAt)
        .Select(d => DirectorOs.Normalize(d.Os))
        .FirstOrDefault();

    private DirectorDto? Connected(TenantId tenant, IReadOnlyList<DirectorDto> onMachine) =>
        onMachine.FirstOrDefault(d => d.StoppedAtUtc is null && _isConnected(tenant, d.DirectorId));

    private static ScheduleFolderVerdict Refuse(string machine, string sentence)
    {
        FileLog.Write($"[ScheduleFolderCheck] {machine}: REFUSED - {sentence}");
        return ScheduleFolderVerdict.Refused(sentence);
    }

    private static ScheduleFolderVerdict Warn(string machine, string sentence)
    {
        FileLog.Write($"[ScheduleFolderCheck] {machine}: not checked - {sentence}");
        return ScheduleFolderVerdict.Unchecked(sentence);
    }

    private static FolderCheckAnswer? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<FolderCheckAnswer>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
