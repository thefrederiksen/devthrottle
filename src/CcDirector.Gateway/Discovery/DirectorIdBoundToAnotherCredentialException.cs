using System;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// A stream Hello named a Director id this account has already bound to a DIFFERENT credential, and the
/// registration was refused (the Message Load mission, inspection 12, finding 1).
///
/// A Director id belongs to the credential that FIRST registered it. The binding is what an HTTP route asks
/// when it needs to know whether its caller really is that Director - the restore marks are the case that
/// found this - so a binding any later Hello could rewrite was no check at all: a second device key of the
/// same account could say Hello under the real Director's id and then write every restore mark under its
/// name. The registry refuses that Hello instead, and <see cref="Streaming.DirectorHub"/> closes the
/// connection with the reason in its log.
///
/// The binding is NOT permanent: it is cleared when the registry entry is removed, which is a goodbye, the
/// instance file going away, or the stale sweep once the old Director has stopped refreshing it. So a
/// Director whose device key was legitimately re-enrolled takes its id back once the old registration is
/// gone - see the refusal sentence the hub logs, which names the command that shows whether it still is.
/// </summary>
public sealed class DirectorIdBoundToAnotherCredentialException : InvalidOperationException
{
    public DirectorIdBoundToAnotherCredentialException(TenantId tenant, string directorId)
        : base($"the Director id '{directorId}' is already registered on another device key of this account, "
               + "so this key may not register it; the id stays with the key that first registered it until that registration is gone")
    {
        Tenant = tenant;
        DirectorId = directorId;
    }

    /// <summary>The account the refused registration was for.</summary>
    public TenantId Tenant { get; }

    /// <summary>The Director id the Hello named.</summary>
    public string DirectorId { get; }
}
