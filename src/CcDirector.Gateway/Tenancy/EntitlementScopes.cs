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
    /// Hosted gateway capacity: a tenant on our infrastructure, a device key against the hosted Gateway, and
    /// everything that key unlocks (the tunnel, the cockpit, the mobile application). This is the scope that
    /// costs us money to serve, and the one a self-host plan must never obtain.
    /// </summary>
    public const string HostedGateway = "hosted_gateway";

    // The three artificial-intelligence scopes every paid plan includes. Named once so a plan's entry states
    // what is DIFFERENT about it rather than repeating the common part four times.
    private static readonly string[] AiScopes = { Dictation, Tts, Wingman };

    // THE TABLE. Ordinal comparison, because a tier is an exact string the payment side writes - not prose to
    // be interpreted - and case-insensitive matching would quietly accept a value the payment side never wrote.
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ByTier =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            // The hosted plan: everything, including the hosted capacity it is named for.
            [EntitlementRegistry.TierHosted] = Set(Dictation, Tts, Wingman, HostedGateway),

            // The pro plan: today this grants hosted capacity as well. That is EXISTING, TESTED behaviour
            // (Enrollment_grants_on_any_active_entitlement_regardless_of_tier) and this table preserves it
            // rather than changing it - narrowing "pro" is a separate decision with its own billing meaning.
            [EntitlementRegistry.TierPro] = Set(Dictation, Tts, Wingman, HostedGateway),

            // The pro SELF-HOST plan: the artificial-intelligence features and NOTHING ELSE. The absence of
            // HostedGateway on this line is the whole point of the plan and of this file.
            [EntitlementRegistry.TierProSelfHost] = Set(AiScopes),

            // The FREE plan: hosted capacity and NOTHING ELSE - the exact mirror of the self-host line above.
            // An account with no paid row and no running trial holds this, so this single line is what makes
            // the end of a trial a downgrade instead of a cutoff: the member keeps the tunnel, the cockpit, the
            // mobile application and every session on every machine, and loses dictation, spoken replies and
            // the wingman.
            //
            // THE THREE ABSENT SCOPES ARE THE PRICE OF THE PLAN. Free costs us hosting, which is small and
            // fixed; it must never cost us a model call, which is neither. If a future feature runs a model on
            // our meter, it belongs behind one of the artificial-intelligence scopes and therefore behind Pro -
            // adding it to this line would give away the thing the plans are drawn around.
            //
            // Note that this Gateway does not itself police the three artificial-intelligence scopes today -
            // they are enforced at the website proxy that actually calls the models, and a free account is
            // refused there because it has neither a subscription row nor a live trial. This line is still the
            // truthful statement of what the plan grants, and it is what any Gateway-side check must read the
            // day one is added.
            [EntitlementRegistry.TierFree] = Set(HostedGateway),

            // A TEAM seat (#2299): exactly what Pro grants. Stated as its own line rather than aliased to Pro so
            // the day a team seat and a personal Pro are priced or scoped differently, it is one edit here.
            [EntitlementRegistry.TierTeam] = Set(Dictation, Tts, Wingman, HostedGateway),
        };

    // The pre-column row. Carried separately from the table because its key is null and because its reason is
    // historical rather than commercial - see the class remarks.
    private static readonly IReadOnlySet<string> LegacyRowScopes = Set(Dictation, Tts, Wingman, HostedGateway);

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

    private static IReadOnlySet<string> Set(params string[] scopes)
        => new HashSet<string>(scopes, StringComparer.Ordinal);
}
