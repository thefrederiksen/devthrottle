using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #4421: the FOLDER CHECK area of the Director's tunnel command surface. One verb,
/// <see cref="ScheduleFolderCheckContract.Verb"/>, addressed to no session: the Gateway asks, while a schedule is saved,
/// whether its folder is on this machine. It answers with <see cref="FolderCheckAnswer"/>: whether something is at the
/// path, whether it is a folder, and the system this Director runs on.
///
/// The path is tested exactly as the session create tests a schedule's folder (<c>Directory.Exists</c> on the path as
/// given, with nothing expanded), so a folder this accepts is one a scheduled run can start in, and one it refuses is
/// one the run would refuse with "repoPath does not exist". It reads no file and lists no folder: the answer is only
/// whether the path is there, which the session create already tells any caller that names it.
///
/// A payload it cannot read, or one with no path, is <see cref="DirectorCommandStatus.BadRequest"/>.
/// </summary>
internal sealed class FolderCheckExecutor : ISessionCommandArea
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly string _os;

    public FolderCheckExecutor()
        : this(Directory.Exists, File.Exists, DirectorOs.Current())
    {
    }

    /// <param name="directoryExists">Test seam: whether a folder is at a path.</param>
    /// <param name="fileExists">Test seam: whether a file is at a path.</param>
    /// <param name="os">The system named in the answer.</param>
    internal FolderCheckExecutor(Func<string, bool> directoryExists, Func<string, bool> fileExists, string os)
    {
        _directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _os = os ?? throw new ArgumentNullException(nameof(os));
    }

    public IReadOnlyCollection<string> Verbs { get; } = new[] { ScheduleFolderCheckContract.Verb };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        if (command.Verb != ScheduleFolderCheckContract.Verb)
            return Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"verb '{command.Verb}' is not handled by the folder check area"));
        return Task.FromResult(Answer(command));
    }

    internal DirectorCommandResult Answer(DirectorCommand command)
    {
        var request = ReadRequest(command.PayloadJson);
        if (request is null)
        {
            FileLog.Write($"[FolderCheckExecutor] {command.Verb}: cmdId={command.CommandId} refused: unreadable payload or no path");
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "the payload must be a folder check with a path");
        }

        var isDirectory = _directoryExists(request.Path);
        var answer = new FolderCheckAnswer
        {
            IsDirectory = isDirectory,
            Exists = isDirectory || _fileExists(request.Path),
            Os = _os,
        };
        FileLog.Write($"[FolderCheckExecutor] {command.Verb}: cmdId={command.CommandId}, path={request.Path}, exists={answer.Exists}, isDirectory={answer.IsDirectory}");
        return DirectorCommandResult.Success(JsonSerializer.Serialize(answer, JsonOptions));
    }

    /// <summary>The request when the payload reads as one with a path; null otherwise.</summary>
    internal static FolderCheckRequest? ReadRequest(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var request = JsonSerializer.Deserialize<FolderCheckRequest>(payloadJson, JsonOptions);
            return string.IsNullOrWhiteSpace(request?.Path) ? null : request;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
