using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// WHERE A DIRECTOR GETS A CHECK FROM A DEFINITION (Factory Definition, phase 5b, design 2.2):
///
///   GET /directors/{directorId}/factory-checks/{factory}/{version}  -> FactoryCheckFilesResponse | 403 | 404
///
/// It answers the <c>checks/</c> and <c>files/</c> entries of one version of a factory's definition, path to text, and
/// nothing else of it. It refuses, each with its sentence:
///   - a Director this account does not have;
///   - a version that does not exist, and a version that is not PUBLISHED - so a check in a draft can never be
///     fetched, let alone run;
///   - a factory with no seat on that Director's machine in that version - a Director is only ever given the code of
///     the factories that run on its own machine.
///
/// The Director names itself in the path and its MACHINE is read from its own registration in the caller's account,
/// never taken from the request, exactly as for its triggers. Behind the factory agents switch, as the trigger routes.
/// </summary>
internal static class FactoryCheckEndpoints
{
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        Func<TenantId, string, string?> directorMachine, FactoryDefinitionStore definitions)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(directorMachine);
        ArgumentNullException.ThrowIfNull(definitions);

        app.MapGet("/directors/{directorId}/factory-checks/{factory}/{version:int}",
            (string directorId, string factory, int version, HttpContext ctx) =>
            {
                FileLog.Write($"[FactoryCheckEndpoints] GET /directors/{directorId}/factory-checks/{factory}/{version}");
                if (resolveTenant(ctx) is not { } tenant)
                    return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
                if (directorMachine(tenant, directorId) is not { } machine)
                    return Refuse(StatusCodes.Status404NotFound, "director_not_found", $"no Director '{directorId}' in this account");
                return Answer(definitions, tenant, directorId, machine, factory, version);
            });

        FileLog.Write("[FactoryCheckEndpoints] mapped /directors/{directorId}/factory-checks/{factory}/{version}");
    }

    private static IResult Answer(FactoryDefinitionStore definitions, TenantId tenant, string directorId, string machine,
        string factory, int version)
    {
        if (!FactoryNames.TryFactory(factory, out var folded, out var notAFactory))
            return Refuse(StatusCodes.Status404NotFound, "factory_not_found", notAFactory!);

        if (definitions.Find(tenant, folded, version) is not { } found)
            return Refuse(StatusCodes.Status404NotFound, "version_not_found",
                $"version {version} of the definition of {folded} does not exist, so it has no checks to download");

        if (found.Summary.State != FactoryDefinitionStates.Published)
        {
            FileLog.Write($"[FactoryCheckEndpoints] REFUSED a draft: factory={folded}, version={version}, director={directorId}");
            return Refuse(StatusCodes.Status404NotFound, "version_not_published",
                $"version {version} of the definition of {folded} is a draft, not published; a check is only ever " +
                "downloaded from a published version");
        }

        var definition = FactoryDefinitionCheck.Read(found.Files).Definition;
        if (definition is null)
            return Refuse(StatusCodes.Status404NotFound, "version_unreadable",
                $"version {version} of the definition of {folded} cannot be read, so no seat in it can be placed on a machine");

        var seatsHere = definition.Seats
            .Where(s => string.Equals(s.MachineOrDefault(definition.Defaults), machine, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Seat).ToList();
        if (seatsHere.Count == 0)
        {
            FileLog.Write($"[FactoryCheckEndpoints] REFUSED: factory={folded}, version={version} has no seat on {machine} (director={directorId})");
            return Refuse(StatusCodes.Status404NotFound, "no_seat_on_machine",
                $"version {version} of the definition of {folded} has no seat on {machine}, so Director '{directorId}' " +
                "is not given its checks");
        }

        var files = found.Files
            .Where(kv => kv.Key.StartsWith("checks/", StringComparison.Ordinal) || kv.Key.StartsWith("files/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        FileLog.Write($"[FactoryCheckEndpoints] answered: factory={folded}, version={version}, director={directorId}, seats here={string.Join(",", seatsHere)}, files={files.Count}");
        return Results.Json(new FactoryCheckFilesResponse { Factory = folded, Version = version, Files = files });
    }

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
