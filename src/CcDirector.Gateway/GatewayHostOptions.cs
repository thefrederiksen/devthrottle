using CcDirector.Core.Storage;

namespace CcDirector.Gateway;

/// <summary>
/// Everything a <see cref="GatewayHost"/> used to read from the process environment while it ran, gathered into
/// one value that is read ONCE, when the host is built, and never again.
///
/// WHY THIS EXISTS. The Gateway's deployment mode (<see cref="Hosted"/>) was read from the environment variable
/// <c>CC_GATEWAY_HOSTED</c> on every call, in forty-seven files. Two consequences, both measured in the test review
/// of 10 October 2026 (Release Process Fix mission):
///
///  - Two hosts in one process could not hold different modes, because the mode was a property of the PROCESS.
///    So the whole Gateway test assembly ran one class at a time behind a machine-wide lock, and a full run took
///    fifteen to fifty minutes of test time that could have been divided by the number of cores.
///  - A test that failed to restore the variable poisoned every later test in the run. One such teardown on
///    7 October 2026 turned 692 unrelated tests red in a single run.
///
/// THE RULE. Production builds this record from the environment in exactly ONE place, <see cref="FromEnvironment"/>,
/// which the host constructor calls when it is given no options. Nothing else in the Gateway reads the mode from
/// the environment: every component takes it from the host, or as a REQUIRED constructor or method parameter the
/// host supplies. A test passes an explicit record and sets no environment variable. The mode a host runs in is
/// therefore the mode it was built with, for its whole life.
///
/// THE FAIL-CLOSED DISCIPLINE IS KEPT. The Gateway's hosted gates were deliberately written to read the deployment
/// signal "ITSELF, never an optional argument a caller could omit" (findings CR-7 and I1-01), because an optional
/// security argument that defaults to "not hosted" fails OPEN when forgotten. That property survives this change
/// because the mode travels as a non-nullable <c>bool</c> that cannot be omitted: a caller must say which mode it
/// means, and the compiler refuses one that does not. What changes is only WHERE the value comes from - the host
/// that owns the component, not the process that happens to contain it.
/// </summary>
public sealed record GatewayHostOptions
{
    /// <summary>
    /// Whether this Gateway is a hosted, multi-tenant deployment reached by its public URL (true) or a self-host
    /// Gateway serving one owner (false). Production takes it from <see cref="GatewayHostedMode.IsHosted"/>, the
    /// <c>CC_GATEWAY_HOSTED=1</c> toggle; a test states it. False is the self-host default, which keeps the desktop
    /// behaviour byte-identical.
    /// </summary>
    public bool Hosted { get; init; }

    /// <summary>
    /// The directory this host keeps its files under: the database, the key vault, the token file, the stores
    /// that used to compose their paths from <see cref="CcStorage.Root"/> at call time. Production takes it from
    /// <see cref="CcStorage.Root"/>, the <c>CC_DIRECTOR_ROOT</c> setting a Director points at its own home; a test
    /// states an isolated directory.
    ///
    /// WHY IT HAS AN INITIALISER. The initial value is this process's root, so an explicit record that omits it
    /// runs where the host ran before this property existed - the Gateway test assembly redirects that root once
    /// for the whole run. Every test that builds a host is being moved to state the root (gateway-tests.md section
    /// 3.3, step 1); when the last one has, this initialiser goes and the property becomes required, so that a
    /// forgotten root is a compile error rather than a shared directory.
    /// </summary>
    public string StorageRoot { get; init; } = CcStorage.Root();

    /// <summary>
    /// The ONE place production reads the host's options from the process environment. Called by the
    /// <see cref="GatewayHost"/> constructor when a caller passes no options, which is what the production entry
    /// points do. Everything a test used to set through <c>Environment.SetEnvironmentVariable</c> is a property on
    /// the record a test passes instead.
    /// </summary>
    public static GatewayHostOptions FromEnvironment() => new()
    {
        Hosted = GatewayHostedMode.IsHosted,
        StorageRoot = CcStorage.Root(),
    };
}
