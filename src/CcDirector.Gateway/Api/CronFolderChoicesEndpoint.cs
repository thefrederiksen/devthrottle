using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE SCHEDULE FORM'S FOLDER CHOICES (issue #4421).
///
///   GET /cron/folder-choices?machine=NAME -> CronFolderChoicesDto | 400 (no machine) | 403 (no account)
///
/// A schedule's folder used to be a free-text box with a Windows placeholder, whatever the machine, and a person on a
/// Mac typed a Windows path into it. The form now asks this route for the machine it runs on: the machine's known
/// repositories (the one repository list, in its order), an example folder in that machine's own shape, and whether a
/// save can check the folder now. The form shows these as they arrive; the save checks the folder either way
/// (<see cref="ScheduleFolderCheck"/>).
/// </summary>
internal static class CronFolderChoicesEndpoint
{
    public const string Route = "/cron/folder-choices";

    /// <summary>What the Gateway knows of one machine for the calling account.</summary>
    /// <param name="Os">The system it last reported, or null.</param>
    /// <param name="Online">Whether a Director there is connected now.</param>
    /// <param name="Repositories">Its known repositories, in the one repository list's order.</param>
    public sealed record MachineFolders(string? Os, bool Online, IReadOnlyList<KnownRepositoryDto> Repositories);

    /// <param name="machineFolders">What the Gateway knows of the named machine for the caller's account, or null when
    /// the caller has no account.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, string, MachineFolders?> machineFolders)
    {
        ArgumentNullException.ThrowIfNull(machineFolders);
        app.MapGet(Route, (HttpContext ctx, string? machine) =>
        {
            if (string.IsNullOrWhiteSpace(machine))
                return Results.BadRequest(new { error = "name the machine: ?machine=NAME" });
            var name = machine.Trim();
            var known = machineFolders(ctx, name);
            if (known is null)
            {
                FileLog.Write($"[CronFolderChoicesEndpoint] GET {Route}: REFUSED - no account for the caller");
                return Results.Json(new { error = "no account for this caller" }, statusCode: StatusCodes.Status403Forbidden);
            }
            var choices = Fold(name, known);
            FileLog.Write($"[CronFolderChoicesEndpoint] GET {Route}: machine={name}, os={choices.Os}, online={choices.Online}, repositories={choices.Repositories.Count}");
            return Results.Json(choices);
        });
    }

    internal static CronFolderChoicesDto Fold(string machine, MachineFolders known)
    {
        var os = DirectorOs.Normalize(known.Os);
        return new CronFolderChoicesDto
        {
            Machine = machine,
            Os = os ?? "",
            OsName = DirectorOs.DisplayName(os),
            Online = known.Online,
            Placeholder = ScheduleFolderWords.Placeholder(os),
            Note = NoteFor(machine, os, known),
            Repositories = known.Repositories.ToList(),
        };
    }

    private static string? NoteFor(string machine, string? os, MachineFolders known)
    {
        if (!known.Online)
            return $"{machine} is offline, so the folder cannot be checked when you save. It is checked when the schedule first runs.";
        if (known.Repositories.Count == 0)
            return ScheduleFolderWords.AComputer(os) is { } computer
                ? $"{machine} is {computer} and has no known repositories yet. Type the folder's full path."
                : $"{machine} has no known repositories yet. Type the folder's full path.";
        return null;
    }
}
