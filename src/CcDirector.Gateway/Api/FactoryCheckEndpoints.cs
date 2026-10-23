using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Records;
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
///
///   GET /directors/{directorId}/factory-check-inputs/{factory}/{version}  -> FactoryCheckInputsResponse | 404 | 413
///
/// THE CHECK INPUTS: the factory's working documents whose names begin <c>check-inputs/</c>, as they stand, which the
/// Director writes into the folder <c>CC_CHECK_INPUTS_DIR</c> names before the check runs. A check runs with no Gateway
/// key, so without this a check could only read what is on its machine's disk, and a factory whose records are on the
/// Gateway could not have a check at all. The same refusals as the checks themselves, from the same version: a Director
/// is only ever given the inputs of a factory whose published version places a seat on its machine. Inputs larger
/// together than <see cref="TriggerCheckLimits.MaxCheckInputsBytes"/> are refused whole, never cut.
/// </summary>
internal static class FactoryCheckEndpoints
{
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        Func<TenantId, string, string?> directorMachine, FactoryDefinitionStore definitions, FactoryDocumentStore documents)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(directorMachine);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(documents);

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

        app.MapGet("/directors/{directorId}/factory-check-inputs/{factory}/{version:int}",
            (string directorId, string factory, int version, HttpContext ctx) =>
            {
                FileLog.Write($"[FactoryCheckEndpoints] GET /directors/{directorId}/factory-check-inputs/{factory}/{version}");
                if (resolveTenant(ctx) is not { } tenant)
                    return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
                if (directorMachine(tenant, directorId) is not { } machine)
                    return Refuse(StatusCodes.Status404NotFound, "director_not_found", $"no Director '{directorId}' in this account");
                return AnswerInputs(definitions, documents, tenant, directorId, machine, factory, version);
            });

        FileLog.Write("[FactoryCheckEndpoints] mapped /directors/{directorId}/factory-checks/{factory}/{version} and /factory-check-inputs/{factory}/{version}");
    }

    private static IResult Answer(FactoryDefinitionStore definitions, TenantId tenant, string directorId, string machine,
        string factory, int version)
    {
        var (found, folded, refusal) = Place(definitions, tenant, directorId, machine, factory, version, out var seatsHere);
        if (refusal is not null) return refusal;

        var files = found!.Files
            .Where(kv => kv.Key.StartsWith("checks/", StringComparison.Ordinal) || kv.Key.StartsWith("files/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        FileLog.Write($"[FactoryCheckEndpoints] answered: factory={folded}, version={version}, director={directorId}, seats here={string.Join(",", seatsHere)}, files={files.Count}");
        return Results.Json(new FactoryCheckFilesResponse { Factory = folded, Version = version, Files = files });
    }

    private static IResult AnswerInputs(FactoryDefinitionStore definitions, FactoryDocumentStore documents, TenantId tenant,
        string directorId, string machine, string factory, int version)
    {
        var (_, folded, refusal) = Place(definitions, tenant, directorId, machine, factory, version, out _);
        if (refusal is not null) return refusal;

        var held = documents.NewestUnder(tenant, folded, TriggerCheckLimits.CheckInputsPrefix);
        var total = held.Sum(d => (long)d.TextBytes);
        if (total > TriggerCheckLimits.MaxCheckInputsBytes)
        {
            FileLog.Write($"[FactoryCheckEndpoints] REFUSED inputs: factory={folded}, inputs={held.Count}, bytes={total}, director={directorId}");
            return Refuse(StatusCodes.Status413PayloadTooLarge, "check_inputs_too_large",
                $"the check inputs of {folded} take {total} bytes together, and a check is handed at most " +
                $"{TriggerCheckLimits.MaxCheckInputsBytes}; make the documents under {TriggerCheckLimits.CheckInputsPrefix} smaller");
        }

        var inputs = held.ToDictionary(d => d.Name[TriggerCheckLimits.CheckInputsPrefix.Length..], d => d.Text, StringComparer.Ordinal);
        FileLog.Write($"[FactoryCheckEndpoints] answered inputs: factory={folded}, version={version}, director={directorId}, inputs={inputs.Count}, bytes={total}");
        return Results.Json(new FactoryCheckInputsResponse { Factory = folded, Inputs = inputs });
    }

    /// <summary>
    /// Whether this Director may be given anything of this version of the factory: the version exists, is PUBLISHED, can
    /// be read, and places a seat - or a backup of a seat's check - on the Director's machine. The refusal, or null.
    /// </summary>
    private static (FactoryDefinitionVersion? Found, string Folded, IResult? Refusal) Place(FactoryDefinitionStore definitions,
        TenantId tenant, string directorId, string machine, string factory, int version, out List<string> seatsHere)
    {
        seatsHere = new List<string>();
        if (!FactoryNames.TryFactory(factory, out var folded, out var notAFactory))
            return (null, factory, Refuse(StatusCodes.Status404NotFound, "factory_not_found", notAFactory!));

        if (definitions.Find(tenant, folded, version) is not { } found)
            return (null, folded, Refuse(StatusCodes.Status404NotFound, "version_not_found",
                $"version {version} of the definition of {folded} does not exist, so it has no checks to download"));

        if (found.Summary.State != FactoryDefinitionStates.Published)
        {
            FileLog.Write($"[FactoryCheckEndpoints] REFUSED a draft: factory={folded}, version={version}, director={directorId}");
            return (null, folded, Refuse(StatusCodes.Status404NotFound, "version_not_published",
                $"version {version} of the definition of {folded} is a draft, not published; a check is only ever " +
                "downloaded from a published version"));
        }

        var definition = FactoryDefinitionCheck.Read(found.Files).Definition;
        if (definition is null)
            return (null, folded, Refuse(StatusCodes.Status404NotFound, "version_unreadable",
                $"version {version} of the definition of {folded} cannot be read, so no seat in it can be placed on a machine"));

        // A seat's checks run on the seat's machine, and - Mail Desk, phase 5 - on every backup machine a start of it
        // names, which is where its check runs while the seat's machine is away.
        seatsHere = definition.Seats
            .Where(s => string.Equals(s.MachineOrDefault(definition.Defaults), machine, StringComparison.OrdinalIgnoreCase)
                        || s.Starts.Any(st => st.Check is not null
                                              && st.BackupMachines.Any(b => string.Equals(b, machine, StringComparison.OrdinalIgnoreCase))))
            .Select(s => s.Seat).ToList();
        if (seatsHere.Count == 0)
        {
            FileLog.Write($"[FactoryCheckEndpoints] REFUSED: factory={folded}, version={version} has no seat and no backup on {machine} (director={directorId})");
            return (null, folded, Refuse(StatusCodes.Status404NotFound, "no_seat_on_machine",
                $"version {version} of the definition of {folded} has no seat on {machine}, so Director '{directorId}' " +
                "is not given its checks"));
        }
        return (found, folded, null);
    }

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
