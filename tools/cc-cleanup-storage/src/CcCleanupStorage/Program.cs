using System.Text.Json;
using CcCleanupStorage;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Indexing;

// The entry point, and the only place in this tool that catches anything. Everything below it lets a
// failure travel: a helper that swallowed one would turn a disk this tool could not read into a disk
// this tool said was empty, which is the single worst answer it could give.
//
// This tool moves nothing without the explicit apply flag, and what it moves goes into a holding
// folder that can put it back. Nothing here ever deletes outright, elevates itself, or runs
// unattended.

FileLog.UseUniqueInstanceId(CcStorage.ToolLogs("cc-cleanup-storage"));
FileLog.Start();

try
{
    var outcome = CommandLine.Parse(args, ScanIndexStore.DefaultIndexDirectory());

    if (outcome.UsageError is not null)
    {
        return Write(Runner.Failure(
            CommandWordIn(args),
            "usage",
            outcome.UsageError + ". Run: cc-cleanup-storage --help",
            ExitCodes.Usage),
            WantsJson(args));
    }

    var request = outcome.Request
        ?? throw new InvalidOperationException("The command line was read and produced neither a request nor an error.");

    return Write(Runner.Run(request), request.Json);
}
catch (DirectoryNotFoundException ex)
{
    return Write(Runner.Failure(CommandWordIn(args), "folder-not-found", ex.Message, ExitCodes.Failed), WantsJson(args));
}
catch (FileNotFoundException ex)
{
    return Write(Runner.Failure(CommandWordIn(args), "no-saved-scan", ex.Message, ExitCodes.Failed), WantsJson(args));
}
catch (InvalidDataException ex)
{
    return Write(Runner.Failure(CommandWordIn(args), "unreadable-saved-scan", ex.Message, ExitCodes.Failed), WantsJson(args));
}
catch (UnauthorizedAccessException ex)
{
    return Write(Runner.Failure(CommandWordIn(args), "access-denied", ex.Message, ExitCodes.Failed), WantsJson(args));
}
catch (IOException ex)
{
    return Write(Runner.Failure(CommandWordIn(args), "read-or-write-failed", ex.Message, ExitCodes.Failed), WantsJson(args));
}
catch (Exception ex)
{
    FileLog.Write($"[Program] Main FAILED: {ex}");
    return Write(Runner.Failure(CommandWordIn(args), "failed", ex.Message, ExitCodes.Failed), WantsJson(args));
}
finally
{
    FileLog.Stop();
}

static int Write(Answer answer, bool json)
{
    if (json) Console.Out.WriteLine(JsonSerializer.Serialize(answer.JsonPayload, JsonShape.Options));
    else foreach (var line in answer.TextLines) Console.Out.WriteLine(line);

    return answer.ExitCode;
}

// Read straight off the command line, because these two are needed on the path where the command line
// could not be read at all. They ask only what shape the answer takes and what to call the command
// that failed; nothing is done on the strength of them.
static bool WantsJson(string[] arguments) => arguments.Contains("--json", StringComparer.Ordinal);

static string CommandWordIn(string[] arguments) =>
    arguments.Length > 0 && !arguments[0].StartsWith('-') ? arguments[0] : string.Empty;
