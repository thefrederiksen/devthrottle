namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// THE ONE PLACE that says what a paid plan tier grants. Nothing else in the Gateway may decide, infer or
/// re-derive a plan's capabilities: a caller asks this table and renders the answer, exactly as the client is
/// dumb about display state and the Gateway owns the verdict.
///
/// WHY THIS TABLE EXISTS AT ALL. The website's billing sells more than one shape of product. A hosted plan
/// pays for a tenant on our infrastructure; a SELF-HOST plan pays only for the artificial-intelligence
/// features - dictation, text-to-speech, and the wingman - and runs the Gateway on the customer's own
/// machine. Those two are priced differently precisely because one of them costs us hosting. If the Gateway
/// cannot tell them apart, a self-host subscriber obtains hosted capacity they never paid for, and the
/// difference is silent: a successful provisioning looks exactly like a correct one.
///
/// THE ENTITLEMENT READ DOES NOT POLICE THIS. <see cref="EntitlementRegistry"/> answers a different question -
/// "is there a live, paid row for this account" - and it passes whatever tier string that row carries straight
/// through, unexamined. That separation is deliberate and must stay: the read is about money, this table is
/// about capability. So the refusal belongs HERE and at the places that authorize a capability, never inside
/// the entitlement read.
///
/// THE DEFAULT IS DENY, AND IT IS AN ALLOWLIST. A tier string this code does not recognise grants NOTHING.
/// This is the same law <see cref="EntitlementRegistry"/> already states about subscription states - "a value
/// it does not recognise is NOT entitled, because an unknown state is not an entitled one" - applied to plans.
/// The failure directions are not symmetric: refusing a plan we have not taught the Gateway about is visible
/// and reversible in one edit, while granting hosted capacity to an unknown plan is invisible and is exactly
/// the bypass this table exists to prevent. A new plan therefore lands here as a deliberate line of code.
///
/// THE ONE CARVE-OUT IS THE NULL TIER, and it is not a guess. The tier column was added to the payment-side
/// table after rows already existed, so a row written before it arrives as null. A null is an OLD HOSTED ROW,
/// not an unknown plan, and it has always granted hosted access; refusing it would lock out established paying
/// customers over a column they predate. It is written as its own entry so the reasoning is visible rather
/// than hidden in a default.
/// </summary>
public static class EntitlementScopes
{
    /// <summary>Voice dictation - speech to text through the Gateway.</summary>
    public const string Dictation = "dictation";

    /// <summary>Text to speech - the spoken side of voice mode.</summary>
    public const string Tts = "tts";

    /// <summary>The wingman - the artificial-intelligence turn briefing and conversation.</summary>
    public const string Wingman = "wingman";

    /// <summary>
    /// Hosted gateway capacity for a DIRECTOR: a tenant on our infrastructure, a Director's device key against the
    /// hosted Gateway, the tunnel that key opens, and the recording of the account's usage. It is the scope a
    /// self-host plan must never obtain. Since the Pricing Change mission (B2) it no longer includes the Cockpit or
    /// the phone app: those are <see cref="RemoteAccess"/>.
    /// </summary>
    public const string HostedGateway = "hosted_gateway";

    /// <summary>
    /// Remote access (Pricing Change mission, B2): signing in to the Cockpit, enrolling the phone app, and every
    /// request either of them makes afterwards, the relay to a Director included. Asked in two places and answered
    /// by one rule, <see cref="RemoteAccessRule"/>: where a Cockpit or phone key is minted, and on every request such
    /// a key makes.
    /// </summary>
    public const string RemoteAccess = "remote_access";

    // The three artificial-intelligence scopes every paid plan includes. Named once so a plan's entry states
    // what is DIFFERENT about it rather than repeating the common part four times.
    private static readonly string[] AiScopes = { Dictation, Tts, Wingman };

    // THE TABLE. Ordinal comparison, because a tier is an exact string the payment side writes - not prose to
    // be interpreted - and case-insensitive matching would quietly accept a value the payment side never wrote.
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ByTier =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            // The hosted plan: everything, including the hosted capacity it is named for. Retired from sale and
            // still honoured, so it keeps the Cockpit and the phone app it was sold with.
            [EntitlementRegistry.TierHosted] = Set(Dictation, Tts, Wingman, HostedGateway, RemoteAccess),

            // The pro plan: everything.
            [EntitlementRegistry.TierPro] = Set(Dictation, Tts, Wingman, HostedGateway, RemoteAccess),

            // The pro SELF-HOST plan: the artificial-intelligence features and NOTHING ELSE. The absence of
            // HostedGateway on this line is the whole point of the plan and of this file, and it holds no
            // RemoteAccess either: there is no hosted Cockpit for a Gateway the customer runs themselves.
            [EntitlementRegistry.TierProSelfHost] = Set(AiScopes),

            // The FREE plan: a Director on the hosted Gateway and NOTHING ELSE (Pricing Change mission, decision
            // D2). An account with no paid row and no running trial holds this. Its Director signs in, tunnels and
            // has its usage recorded as much as the member likes. It has no Cockpit and no phone app - that is
            // RemoteAccess, which this line does not carry - and no dictation, spoken replies or wingman.
            //
            // THE ABSENT SCOPES ARE THE PRICE OF THE PLAN. Free costs us a tunnel, which is small and fixed; it
            // must never cost us a model call, which is neither. If a future feature runs a model on our meter, it
            // belongs behind one of the artificial-intelligence scopes and therefore behind Pro.
            //
            // An account created before the launch of these plans keeps the Cockpit and the phone app for a while,
            // and a member of a team with a running bill has them, without this line changing: both are added
            // beside the table by RemoteAccessRule. The hosted Gateway polices the three artificial-intelligence
            // scopes through TenantAiGate.
            [EntitlementRegistry.TierFree] = Set(HostedGateway),

            // A TEAM seat (#2299): exactly what Pro grants. Stated as its own line rather than aliased to Pro so
            // the day a team seat and a personal Pro are priced or scoped differently, it is one edit here.
            [EntitlementRegistry.TierTeam] = Set(Dictation, Tts, Wingman, HostedGateway, RemoteAccess),

            // The REMOTE plan (Pricing Change mission, decision D3): a Director on the hosted Gateway, the Cockpit
            // and the phone app, and NONE of the artificial-intelligence scopes. RemoteAccess is the one scope that
            // separates this line from the free line above, and it is what the plan is sold for.
            [EntitlementRegistry.TierRemote] = Set(HostedGateway, RemoteAccess),
        };

    // The pre-column row. Carried separately from the table because its key is null and because its reason is
    // historical rather than commercial - see the class remarks. An old hosted row had the Cockpit and the phone
    // app, so it keeps them.
    private static readonly IReadOnlySet<string> LegacyRowScopes = Set(Dictation, Tts, Wingman, HostedGateway, RemoteAccess);

    // Nothing. The answer for a tier string this code has never been taught.
    private static readonly IReadOnlySet<string> NoScopes = Set();

    /// <summary>
    /// What this tier grants. Never null - an unrecognised tier returns the EMPTY set, which is a refusal of
    /// everything, and a null tier returns the legacy hosted row's scopes (see the class remarks).
    /// </summary>
    /// <param name="tier">The tier exactly as the payment-side row records it, or null for a row that predates
    /// the tier column. Surrounding whitespace is ignored; a blank string is treated as null, because a column
    /// holding only spaces records no plan.</param>
    public static IReadOnlySet<string> ForTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
            return LegacyRowScopes;

        return ByTier.TryGetValue(tier.Trim(), out var scopes) ? scopes : NoScopes;
    }

    /// <summary>
    /// Does this tier grant the named scope? The single question every capability check should ask.
    /// </summary>
    /// <param name="tier">The tier as the payment-side row records it, or null for a pre-column row.</param>
    /// <param name="scope">One of the scope constants on this class.</param>
    public static bool Grants(string? tier, string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("A scope name is required", nameof(scope));

        return ForTier(tier).Contains(scope);
    }

    /// <summary>
    /// May an account on this tier be given HOSTED GATEWAY capacity - a tenant, a device key, and continued
    /// hosted service? Named separately from <see cref="Grants"/> because it is the expensive question and the
    /// one whose call sites must be findable in one search.
    /// </summary>
    /// <param name="tier">The tier as the payment-side row records it, or null for a pre-column row.</param>
    public static bool GrantsHostedGateway(string? tier) => Grants(tier, HostedGateway);

    /// <summary>
    /// Does this tier's PLAN include the Cockpit and the phone app? This is the plan's half of the answer only:
    /// the whole answer, with the pre-launch grace and a person's teams, is <see cref="RemoteAccessRule.Decide"/>, and
    /// a caller deciding whether to serve a person asks that rule and never this method alone.
    /// </summary>
    /// <param name="tier">The tier as the payment-side row records it, or null for a pre-column row.</param>
    public static bool GrantsRemoteAccess(string? tier) => Grants(tier, RemoteAccess);

    private static IReadOnlySet<string> Set(params string[] scopes)
        => new HashSet<string>(scopes, StringComparer.Ordinal);
}
