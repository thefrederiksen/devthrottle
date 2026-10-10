using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE FACTORY AND SEAT PICKER'S CHOICES (the owner, 2026-10-10: link a schedule to its factory seat from the page).
///
///   GET /cron/seat-choices -> CronSeatChoicesDto
///
/// A schedule's factory and seat are checked against the registry on every write (FactoryScheduleLink), so the picker
/// offers exactly what that check accepts: the account's registered factories and their registered seats. An archived
/// factory is left out, because its schedules stay off until its Restore and a write that switches one on is refused.
/// </summary>
internal static class CronSeatChoicesEndpoint
{
    public const string Route = "/cron/seat-choices";

    public const string NoneLabel = "No factory (Personal)";

    /// <param name="listFactories">The calling account's registered factories, or null when the caller has no account.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, IReadOnlyList<RegisteredFactoryDto>?> listFactories)
    {
        ArgumentNullException.ThrowIfNull(listFactories);
        app.MapGet(Route, (HttpContext ctx) =>
        {
            var factories = listFactories(ctx);
            if (factories is null)
            {
                FileLog.Write($"[CronSeatChoicesEndpoint] GET {Route}: REFUSED - no account for the caller");
                return Results.Json(new { error = "no account for this caller" }, statusCode: StatusCodes.Status403Forbidden);
            }
            var choices = Fold(factories);
            FileLog.Write($"[CronSeatChoicesEndpoint] GET {Route}: {choices.Factories.Count} factories");
            return Results.Json(choices);
        });
    }

    internal static CronSeatChoicesDto Fold(IReadOnlyList<RegisteredFactoryDto> factories) => new()
    {
        NoneLabel = NoneLabel,
        Factories = factories
            .Where(f => f.ArchivedAtUtc is null && f.Seats.Count > 0)
            .OrderBy(f => TitleOf(f), StringComparer.OrdinalIgnoreCase)
            .Select(f => new CronFactoryChoiceDto
            {
                Factory = f.Factory,
                Title = TitleOf(f),
                Seats = f.Seats.Select(s => new CronSeatChoiceDto
                {
                    Id = s.Id,
                    Label = string.IsNullOrWhiteSpace(s.Name) || string.Equals(s.Name.Trim(), s.Id, StringComparison.OrdinalIgnoreCase)
                        ? s.Id
                        : $"{s.Name.Trim()} ({s.Id})",
                }).ToList(),
            })
            .ToList(),
    };

    private static string TitleOf(RegisteredFactoryDto f) => string.IsNullOrWhiteSpace(f.Title) ? f.Factory : f.Title.Trim();
}
