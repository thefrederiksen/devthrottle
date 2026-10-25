using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Data;

/// <summary>
/// The interceptors every Gateway context gets, wherever its options are built.
///
/// This exists because there are TWO places that build them - SQLite locally, Postgres hosted - and a
/// guard installed in one of two places is a guard that is simply absent on one provider, which is the
/// worst kind: it passes its tests on the machine where it was written. <c>OnConfiguring</c> would have
/// been one place, but a pooled context factory forbids it outright.
///
/// So: one method, called at both sites, and a structural test that counts the call sites and fails if the
/// two numbers ever differ.
/// </summary>
internal static class GatewayContextInterceptors
{
    /// <summary>One instance for the process. The interceptor holds no state of its own.</summary>
    private static readonly RuleTableWriteInterceptor RuleTableWrites = new();

    /// <summary>One instance for the process; it holds no state, the context it is handed does.</summary>
    private static readonly SettingsTransactionInterceptor SettingsTransactions = new();

    /// <summary>One instance for the process; it marks the context it is handed and keeps nothing itself.</summary>
    private static readonly CredentialTableWriteInterceptor CredentialTableWrites = new();

    /// <summary>Install them. Call this beside every provider configuration.</summary>
    internal static DbContextOptionsBuilder WithGatewayInterceptors(this DbContextOptionsBuilder options) =>
        options.AddInterceptors(RuleTableWrites, SettingsTransactions, CredentialTableWrites);
}
