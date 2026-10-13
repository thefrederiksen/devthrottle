using System.Text.Json;
using CcDirector.Core.Licenses;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The License Switching mission, contract section 8: the SIGN-IN area of the Director's tunnel command surface.
/// Three verbs, none addressed to a session:
///
///   - <c>license-sign-in</c> goes to the TARGET machine's Director: sign this account in here - a new spare, or a
///     renewal in place (8.4). Answer: a <see cref="LicenseSignInResult"/>.
///   - <c>license-approve</c> goes to the APPROVING machine's Director: approve another machine's sign-in in a
///     browser profile here, and hand the answer back sealed to that machine (8.3). Answer: a
///     <see cref="LicenseApprovalResult"/>.
///   - <c>license-approval-result</c> brings that answer to the target Director, which is waiting for it by SignInId.
///
/// A payload that cannot be read, or a SignInId that is not 32 lower-case hexadecimal characters, is
/// <see cref="DirectorCommandStatus.BadRequest"/> with no body. Every other outcome, a refusal included, is a
/// SUCCESSFUL answer carrying its result, as in <see cref="LicenseSwitchExecutor"/>.
///
/// NOTHING THAT COULD COMPLETE A SIGN-IN IS LOGGED: not the authorize URL, not the sealed approval. The lines name the
/// verb, the command id, the SignInId, the account and the outcome. It types nothing into any session (contract 6.3).
/// </summary>
internal sealed class LicenseSignInExecutor : ISessionCommandArea
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        LicenseContract.SignInVerb, LicenseContract.ApproveVerb, LicenseContract.ApprovalResultVerb,
    };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var licenses = context.Services?.Licenses
            ?? throw new InvalidOperationException("license switching is not wired on this Director's tunnel");
        return command.Verb switch
        {
            LicenseContract.SignInVerb => SignInAsync(licenses.SignIns, command, cancellationToken),
            LicenseContract.ApproveVerb => ApproveAsync(licenses.Approver, command, cancellationToken),
            LicenseContract.ApprovalResultVerb => Task.FromResult(DeliverApproval(licenses.Approvals, command)),
            _ => Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"verb '{command.Verb}' is not handled by the license sign-in area")),
        };
    }

    internal static async Task<DirectorCommandResult> SignInAsync(LicenseSignInCoordinator signIns, DirectorCommand command, CancellationToken cancellationToken)
    {
        FileLog.Write($"[LicenseSignInExecutor] {command.Verb}: cmdId={command.CommandId}");
        var request = Read<LicenseSignInRequest>(command.PayloadJson, r => r.SignInId);
        if (request is null || string.IsNullOrWhiteSpace(request.AccountEmail))
            return BadPayload(command, "a license sign-in request with an account");

        var result = await signIns.SignInAsync(request, cancellationToken);
        FileLog.Write($"[LicenseSignInExecutor] {command.Verb}: cmdId={command.CommandId}, signInId={result.SignInId}, outcome={result.Outcome}, reason={result.ReasonCode ?? "-"}, replayed={result.Replayed}");
        return DirectorCommandResult.Success(JsonSerializer.Serialize(result, JsonOptions));
    }

    internal static async Task<DirectorCommandResult> ApproveAsync(LicenseApprover approver, DirectorCommand command, CancellationToken cancellationToken)
    {
        FileLog.Write($"[LicenseSignInExecutor] {command.Verb}: cmdId={command.CommandId}");
        var request = Read<LicenseApproveRequest>(command.PayloadJson, r => r.SignInId);
        if (request is null || string.IsNullOrWhiteSpace(request.AccountEmail) || string.IsNullOrWhiteSpace(request.AuthorizeUrl))
            return BadPayload(command, "a license approve request with an account and an authorize URL");

        var result = await approver.ApproveAsync(request, cancellationToken);
        FileLog.Write($"[LicenseSignInExecutor] {command.Verb}: cmdId={command.CommandId}, signInId={result.SignInId}, outcome={result.Outcome}, reason={result.ReasonCode ?? "-"}");
        return DirectorCommandResult.Success(JsonSerializer.Serialize(result, JsonOptions));
    }

    /// <summary>Hands the answer to the sign-in waiting for it. One that arrives after the sign-in gave up, or for a
    /// SignInId this Director is not waiting on, is logged by id and dropped (contract 8.3) - still a successful
    /// answer, because the Gateway did its part.</summary>
    internal static DirectorCommandResult DeliverApproval(LicenseApprovalExchange approvals, DirectorCommand command)
    {
        var result = Read<LicenseApprovalResult>(command.PayloadJson, r => r.SignInId);
        if (result is null || result.Outcome is not (LicenseApprovalOutcomes.Approved or LicenseApprovalOutcomes.Refused))
            return BadPayload(command, "a license approval result whose outcome is approved or refused");

        var delivered = approvals.Deliver(result);
        FileLog.Write($"[LicenseSignInExecutor] {command.Verb}: cmdId={command.CommandId}, signInId={result.SignInId}, outcome={result.Outcome}, {(delivered ? "delivered to the waiting sign-in" : "no sign-in is waiting for it, dropped")}");
        return DirectorCommandResult.Success(JsonSerializer.Serialize(new { signInId = result.SignInId, delivered }, JsonOptions));
    }

    private static DirectorCommandResult BadPayload(DirectorCommand command, string expected)
    {
        FileLog.Write($"[LicenseSignInExecutor] {command.Verb}: cmdId={command.CommandId} refused: unreadable payload or a SignInId of the wrong shape");
        return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
            $"the payload must be {expected}, whose signInId is 32 lower-case hexadecimal characters");
    }

    /// <summary>The payload when it reads and its SignInId has the contract's shape; null otherwise. The id is checked
    /// before anything else because it is logged, used as a file name and passed to a command line.</summary>
    internal static T? Read<T>(string? payloadJson, Func<T, string?> signInId) where T : class
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<T>(payloadJson, JsonOptions);
            if (payload is null) return null;
            var id = signInId(payload);
            return id is not null && LicenseContract.SignInIdShape.IsMatch(id) ? payload : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
