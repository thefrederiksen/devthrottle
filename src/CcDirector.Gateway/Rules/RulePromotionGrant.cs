using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE EVIDENCE THAT A PERSON ASKED FOR A RULE TO GO LIVE. Dry run is the owner's most important bound -
/// it is what puts a human between a standing instruction and its first real use - and bound 6 forbids a
/// rule promoting itself.
///
/// THIS TYPE HAS BEEN WRONG TWICE, AND BOTH VERSIONS ARE WORTH KEEPING WRITTEN DOWN, because each one read
/// as a bound while being a convention:
///
///  1. <c>Promote</c> first took a rule id and a timestamp and nothing else, so anything that could read
///     rules could move one to live. The independent inspection of landing A found it.
///  2. This type was then introduced - and its factory was PUBLIC and took the caller's identity and their
///     acknowledgement as STRINGS. It proved that two strings a caller made up were not blank. Any Gateway
///     code could invent both, and the comment sitting beside it said nothing automated could promote a
///     rule. The independent inspection of landing B found that.
///
/// WHAT IT ENFORCES NOW, STATED EXACTLY, because a security bound described more broadly than it holds is
/// worse than none:
///
///  - A grant cannot be constructed: the constructor is private and a structural test asserts that nothing
///    in the built assembly calls it.
///  - The only way to obtain one is <see cref="FromAuthenticatedRequest"/>, which is INTERNAL - so nothing
///    outside this assembly can reach it at all - and which takes THE INBOUND REQUEST ITSELF rather than a
///    description of one. The caller identity is read from what the request pipeline authenticated. Code
///    with no inbound request has nothing to pass, and cannot invent a value that would do instead.
///  - Inside this assembly, a structural test over the built code asserts that the ONLY type reaching the
///    factory, or reaching <c>SessionRuleStore.Promote</c>, is the promote endpoint. That guard has been
///    watched failing on exactly the shape it is there to catch: a probe was committed that minted a grant
///    from ordinary rules code, and the guard named it.
///  - A grant names ONE rule and is SINGLE USE. It is consumed by the promotion it was obtained for, so a
///    grant cannot be captured and replayed, and cannot be turned on a different instruction.
///
/// WHO MAY ASK, SINCE OWNER DECISION D7 (Session Rules part 2, 2026-10-18): "Any agent should be able to change
/// and set up the rules" - promotion included. So an agent SESSION, calling with its own key, may now obtain a
/// grant too; it is named "session:&lt;id&gt;" exactly as a device is named "device:&lt;id&gt;", and the
/// promotion is recorded in the rule's change history with that session and its agent. What has NOT changed is
/// the point of this type: a grant still comes only from an inbound request the Gateway authenticated, so the
/// evaluator - which has no inbound request - still cannot promote a rule, and a rule still cannot promote
/// itself.
///
/// WHAT IT DOES NOT ENFORCE. It is not proof that a human being was at a keyboard - nothing inside a
/// process can be. Within one assembly, access modifiers cannot make a capability physically unreachable
/// either: code in this assembly could fabricate an <see cref="HttpContext"/> and stamp an identity on it.
/// What stands against that is not a hope - it is a structural test that reads the built assembly and
/// fails on any type but the endpoint reaching this factory, so doing it would be a visible, reviewable act
/// and not a quiet one. An attacker already holding a device key is authentication's problem, not this
/// bound's.
/// </summary>
public sealed class RulePromotionGrant
{
    private int _used;

    private RulePromotionGrant(Guid ruleId, string actor, string acknowledgement, DateTime askedUtc)
    {
        RuleId = ruleId;
        Actor = actor;
        Acknowledgement = acknowledgement;
        AskedUtc = askedUtc;
    }

    /// <summary>The ONE rule this grant is evidence for.</summary>
    public Guid RuleId { get; }

    /// <summary>Who asked, as the request pipeline resolved them. Goes onto the rule, so a live rule can
    /// always say who made it live.</summary>
    public string Actor { get; }

    /// <summary>What they said when they asked. Kept verbatim.</summary>
    public string Acknowledgement { get; }

    /// <summary>When they asked (UTC).</summary>
    public DateTime AskedUtc { get; }

    /// <summary>
    /// Spend this grant. It answers true exactly once: a person agreed to ONE rule going live, on one
    /// occasion, and a piece of evidence that could be presented again is a piece of evidence that could be
    /// captured and replayed.
    /// </summary>
    internal bool TryConsume() => Interlocked.Exchange(ref _used, 1) == 0;

    /// <summary>
    /// The ONLY way to obtain a grant: from an inbound request THIS GATEWAY AUTHENTICATED. Internal, so
    /// nothing outside this assembly can reach it, and taking the request itself so that the caller cannot
    /// supply an identity of its own invention.
    /// </summary>
    /// <param name="ruleId">The rule the caller asked to promote.</param>
    /// <param name="http">The inbound request. Its caller identity is READ, never accepted as a parameter -
    /// that difference is the whole bound. A request the pipeline could not name is refused, which is
    /// exactly the case anything running on its own would present.</param>
    /// <param name="acknowledgement">What the person said when they asked. Required, so promoting is a
    /// deliberate sentence rather than an empty POST that could be replayed by anything.</param>
    /// <param name="askedUtc">When they asked.</param>
    /// <exception cref="RuleRejectedException">The request is not attributable, or said nothing.</exception>
    internal static RulePromotionGrant FromAuthenticatedRequest(
        Guid ruleId, HttpContext? http, string? acknowledgement, DateTime askedUtc)
    {
        var actor = CallerOf(http);
        if (actor is null)
            throw new RuleRejectedException(
                "a rule is moved out of dry run by a person or an agent session the Gateway can name, and this " +
                "request has no caller it could name. Nothing that runs on its own can promote a rule.");

        var said = (acknowledgement ?? "").Trim();
        if (said.Length == 0)
            throw new RuleRejectedException(
                "moving a rule out of dry run is the one act that lets it type into your sessions, so it " +
                "asks you to say what you are agreeing to. An empty request promotes nothing.");

        return new RulePromotionGrant(ruleId, actor, said, askedUtc.ToUniversalTime());
    }

    /// <summary>
    /// Who the request pipeline resolved this caller to be, or null when it resolved nobody. It READS what
    /// the pipeline already decided - the authenticated principal, the device the key middleware matched, or
    /// the session whose key it verified - and decides nothing itself: a second answer to a question the
    /// pipeline has answered is a second place for the two to disagree.
    ///
    /// THE DEVICE USED TO BE READ FROM AN ITEM NOTHING WROTE. This read <c>Items["DeviceKeyId"]</c>, which only
    /// the unit tests ever set; the middleware stamps the device under <see cref="AuthMiddleware.AuthenticatedDeviceItemKey"/>.
    /// So over real HTTP a device-key promotion named nobody and was refused, while the tests that built their
    /// own request passed. It now reads the identity the middleware actually records (Session Rules part 2,
    /// phase 2), and the tests build their requests through the same item.
    ///
    /// A SESSION NAMES ITSELF since owner decision D7 (2026-10-18): "Any agent should be able to change and set
    /// up the rules", promotion included. A session key's request is named "session:&lt;id&gt;", and the same
    /// name goes on the rule's PromotedBy and in its change history.
    /// </summary>
    internal static string? CallerOf(HttpContext? http)
    {
        if (http is null) return null;

        var identity = http.User?.Identity;
        if (identity is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(identity.Name))
            return identity.Name!.Trim();

        if (AuthMiddleware.AuthenticatedDevice(http) is { } device && !string.IsNullOrWhiteSpace(device.DeviceId))
            return DevicePrefix + device.DeviceId.Trim();

        if (AuthMiddleware.CallingSession(http) is { } session)
            return SessionPrefix + session.SessionId;

        return null;
    }

    /// <summary>How a device caller is written on a rule and in its history.</summary>
    internal const string DevicePrefix = "device:";

    /// <summary>How a session caller is written on a rule and in its history - the same form the factory activity
    /// record uses.</summary>
    internal const string SessionPrefix = "session:";
}
