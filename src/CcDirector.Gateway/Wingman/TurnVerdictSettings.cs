namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What the Wingman's turn judging does for one account (the Wingman-on-every-turn mission), resolved per
/// tenant with the documented defaults below. The seat reads these ONCE per stop, so a change takes effect
/// on the next turn end rather than halfway through one.
///
/// BOTH SWITCHES DEFAULT OFF, which is the opposite call from the session supervisor beside it, and
/// deliberately so. The supervisor is on by default because surviving a network blip is the promise
/// somebody bought; judging is a new judgement about somebody's fleet, made by a model, that changes what
/// colour their screen is. Nobody has asked for that yet, and the calm verdicts are the ones that go wrong
/// quietly - a wrong calm colour is a session that needed a person and did not get one. So an account opts
/// IN, and the two switches are separate so it can opt into being judged without opting into being
/// coloured.
/// </summary>
public sealed record TurnVerdictSettings
{
    /// <summary>Whether this account's stops are judged at all. Default off: no model call, no verdict row.</summary>
    public bool JudgeEnabled { get; init; }

    /// <summary>Whether judged verdicts reach the screen. Default off - the shadow state, where verdicts are
    /// stored and gradeable and every row stays exactly the colour the detector made it.</summary>
    public bool ColourEnabled { get; init; }

    /// <summary>How many judgements may be in flight for one account at once. Default 8: a fleet that all
    /// stops at the same moment must not open one model call per session, and eight is the ceiling the
    /// design set. A stop over the ceiling is not judged - it stays exactly as the detector left it, which
    /// is red, because the alternative is a queue whose answers arrive about screens that have moved on.</summary>
    public int MaxInFlight { get; init; } = DefaultMaxInFlight;

    /// <summary>How long to let the screen settle after the boundary before reading it, in milliseconds.
    /// Default 600: an agent's last repaint can land after the state change, and judging the half-drawn
    /// screen would put the wrong evidence in the receipt.</summary>
    public int SettleMs { get; init; } = DefaultSettleMs;

    /// <summary>
    /// How long to wait for the judge, in seconds. Shorter on purpose than the hosted inference call's own
    /// default of 60: this runs at every stop, and an answer that arrives a minute later is an answer about a
    /// screen that has moved. A timeout is not a calm verdict - nothing is stored and the row stays red, which
    /// is exactly today's behaviour and costs nothing new.
    ///
    /// CONSUMED BY <c>TurnVerdictJudge.BuildBrain</c>, the one place the judge's brain is built. Every judge
    /// call <c>TurnVerdictService</c> makes goes through a <c>HostedInferenceBrain</c> constructed there with
    /// this number, not with the brain's own default of 60 seconds, and
    /// <c>TurnVerdictJudgeTimeoutTests</c> pins the constructed brain to it.
    ///
    /// THIRTY IS MEASURED, NOT CHOSEN. The plan carried 20 as a guess. Slice 0 graded the judges against the
    /// labelled corpus and the Architect's ruling fixed the rule rather than the number: 20 stands unless the
    /// chosen judge's NINETY-FIFTH percentile is over 20 seconds, in which case it becomes 30 - because a
    /// timed-out stop stays red, so headroom costs yellow time rather than safety. The chosen judge,
    /// <c>devthrottle/wingman-fast</c>, measured a ninety-fifth percentile of 21.7 seconds at the product's
    /// own cap of eight calls in flight (ninetieth 18.0), so the condition is met and the number is 30.
    ///
    /// THE SLOWEST ANSWERS ARE STILL LOST, and that is deliberate rather than an oversight. The slowest
    /// single answer in that grading was 33.5 seconds, so even at 30 the tail is cut off. Widening further
    /// than the ruling says would buy the last few answers at the price of holding a row yellow for longer on
    /// every stop that is going to fail anyway; the ruling weighed that and stopped at 30.
    /// </summary>
    public int JudgeTimeoutSeconds { get; init; } = DefaultJudgeTimeoutSeconds;

    public const int DefaultMaxInFlight = 8;
    public const int DefaultSettleMs = 600;

    /// <summary>Thirty seconds, set from slice 0's measured ninety-fifth percentile of 21.7 seconds for
    /// <c>devthrottle/wingman-fast</c> at eight calls in flight - see the note on
    /// <see cref="JudgeTimeoutSeconds"/> for the ruling this follows and why it is not wider.</summary>
    public const int DefaultJudgeTimeoutSeconds = 30;

    /// <summary>
    /// The deadline of the ONE re-attempt a stop somebody is listening to may get (slice I, owner ruling
    /// 2026-09-16): a voice session, or a person pressing explain, whose first call timed out or answered
    /// something that is not a JSON object. Sixty seconds is the old translator's deadline, which the owner named.
    /// Not a per-account setting: it binds only that one re-attempt, and every other call keeps
    /// <see cref="JudgeTimeoutSeconds"/>.
    /// </summary>
    public const int ListenedToReattemptTimeoutSeconds = 60;

    /// <summary>
    /// How long the narration call (slice J) may take: the old translator's sixty seconds, the same deadline a
    /// listened-to stop's judge re-attempt gets. The judge's spoken text is already playable while it runs, so a
    /// call that runs out only leaves that text in place.
    /// </summary>
    public const int NarrationCallTimeoutSeconds = 60;

    /// <summary>The shipped defaults, as one value.</summary>
    public static readonly TurnVerdictSettings Defaults = new();
}
