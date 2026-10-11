using System.Text.Json;
using CcDirector.Core.Licenses;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The License Switching mission: the LICENSE area of the Director's tunnel command surface. One verb,
/// <c>license-switch</c>, addressed to no session (contract section 3).
///
/// This class is contract 3.3 step 1 and nothing else: a payload it cannot read, or a SwitchId that is not 32
/// lower-case hexadecimal characters, is <see cref="DirectorCommandStatus.BadRequest"/> with no body - the only
/// non-Ok status returned on purpose. Every later step is <see cref="LicenseSwitchCoordinator"/>'s, and every one of
/// its outcomes, a refusal included, is a SUCCESSFUL answer carrying a <see cref="LicenseSwitchResult"/>, as in
/// <see cref="SecretTransferExecutor"/>. A fault inside it propagates to the tunnel boundary, which answers Error.
///
/// It types nothing into any session (contract 6.3).
/// </summary>
internal sealed class LicenseSwitchExecutor : ISessionCommandArea
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<string> Verbs { get; } = new[] { LicenseContract.SwitchVerb };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        if (command.Verb != LicenseContract.SwitchVerb)
            return Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"verb '{command.Verb}' is not handled by the license area"));

        var licenses = context.Services?.Licenses
            ?? throw new InvalidOperationException("license switching is not wired on this Director's tunnel");
        return SwitchAsync(licenses.Coordinator, command, cancellationToken);
    }

    internal static async Task<DirectorCommandResult> SwitchAsync(LicenseSwitchCoordinator coordinator, DirectorCommand command, CancellationToken cancellationToken)
    {
        FileLog.Write($"[LicenseSwitchExecutor] {command.Verb}: cmdId={command.CommandId}");

        var request = ReadRequest(command.PayloadJson);
        if (request is null)
        {
            FileLog.Write($"[LicenseSwitchExecutor] {command.Verb}: cmdId={command.CommandId} refused: unreadable payload or a SwitchId of the wrong shape");
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                "the payload must be a license switch request whose switchId is 32 lower-case hexadecimal characters");
        }

        var result = await coordinator.SwitchAsync(request, cancellationToken);
        FileLog.Write($"[LicenseSwitchExecutor] {command.Verb}: cmdId={command.CommandId}, switchId={result.SwitchId}, outcome={result.Outcome}, reason={result.ReasonCode ?? "-"}, replayed={result.Replayed}");
        return DirectorCommandResult.Success(JsonSerializer.Serialize(result, JsonOptions));
    }

    /// <summary>The request when the payload reads and its SwitchId has the contract's shape; null otherwise. The id
    /// is checked before anything else because it is logged and used as a file name.</summary>
    internal static LicenseSwitchRequest? ReadRequest(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(payloadJson, JsonOptions);
            if (request is null || request.SwitchId is null || !LicenseContract.SwitchIdShape.IsMatch(request.SwitchId)) return null;
            if (string.IsNullOrWhiteSpace(request.TargetAccountEmail)) return null;
            return request;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
