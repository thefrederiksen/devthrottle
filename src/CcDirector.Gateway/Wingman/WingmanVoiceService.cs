using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Keeps a ready-to-play spoken summary for each "voice session" (issue #531 follow-up). Once the
/// phone uses voice on a session it becomes a voice session; from then on, every time a turn
/// finishes and the session is waiting for the user, the gateway automatically re-runs the wingman
/// translation and hosted text-to-speech and stores the result here - so the phone's session
/// list can show "voice ready" with a play button, play it without entering, and entering is
/// instant (the voice is already made). Since issue #549 the turn-end trigger is the always-running
/// TurnEndWatcher, which calls <see cref="GenerateAsync"/> directly for voice sessions on turn-end
/// (the retired turn-brief pipeline no longer mediates it).
///
/// PARTITIONED BY TENANT (Hosted Multi-Tenancy, VOICE V1). Everything this service holds is customer
/// content: the spoken narration text, the reply text it was made from, and the audio clip itself. On the
/// hosted Gateway one account's narration must never be reachable by another, so the partition is
/// STRUCTURAL, not a predicate on the read:
///  - In memory, each tenant gets its own bucket of per-session dictionaries. A read for tenant A is
///    handed tenant A's bucket and physically cannot name a session id in tenant B's bucket.
///  - On disk, each tenant gets its own DIRECTORY (<c>tenants/&lt;id&gt;/</c>) holding its own
///    voice-sessions.json and its own voice-audio folder, so a read cannot open another tenant's clip.
/// The tenant id is canonicalized before it becomes a bucket key or a path component, and a shape this
/// system does not mint is REFUSED rather than coerced into something that looks valid - the same rule,
/// and the same reasoning, as <see cref="Prompts.GatewayPromptLog"/>.
///
/// Every public read and mutate takes the tenant as a REQUIRED parameter. There is deliberately no
/// bare-session-id overload: a bare-id method is a cross-tenant read waiting for its next caller.
/// </summary>
public sealed class WingmanVoiceService
{
    public sealed record VoiceReady(string Spoken, string Reply, byte[] Audio, DateTime AtUtc, string ContentType = "audio/mpeg", bool ServedViaFallback = false, string? SourceIdentity = null);

    /// <summary>The outcome of one text-to-speech synthesis (issue #939): the audio bytes on success,
    /// or the shared <see cref="HostedAiState"/> when hosted AI is unavailable (out of credits, cap
    /// reached, or account setup is incomplete) so the caller can surface it instead of a silent
    /// null. Both null means a generic provider error (logged, no shared state).</summary>
    private sealed record TtsResult(byte[]? Audio, string? ContentType, HostedAiState? Unavailable, bool ServedViaFallback = false, SpeechFailure Failure = SpeechFailure.None, TimeSpan? RetryAfter = null);

    /// <summary>
    /// What the SPEECH leg did, in the only terms the narration path needs: is this worth another attempt,
    /// and did the provider name a delay?
    ///
    /// It exists because the recorded <see cref="HostedAiState"/> cannot answer that. Two failures that are
    /// completely different to a retry - a 429 the provider will lift in thirty seconds, and a 5xx it has no
    /// deadline for - are both an ANSWERED failure and both record <see cref="HostedAiState.ServiceDown"/>,
    /// so a caller reading the state alone can only guess. Guessing is how the speech leg came to log "this
    /// session retries on its own" about three different failures while scheduling nothing for any of them.
    /// </summary>
    private enum SpeechFailure
    {
        /// <summary>Audio was produced, or the failure is the account's (no key, out of credits, cap).</summary>
        None,
        /// <summary>The provider refused this call with a 429. Transient by definition, and it may have named
        /// how long to wait - see <see cref="TtsResult.RetryAfter"/>.</summary>
        RateLimited,
        /// <summary>The provider ANSWERED with a failure (a 5xx). It told us it is failing, which is a claim
        /// about the service the screen is right to make - and not something a re-attempt seconds later has
        /// any reason to fix, so the narration path does not book one.</summary>
        AnsweredFailure,
        /// <summary>The call did not answer at all - the bounded deadline fired, or the transport failed. The
        /// absence of evidence, and the one speech failure whose screen used to promise audio nobody was
        /// making.</summary>
        DidNotAnswer,
    }

    /// <summary>
    /// What one call to <see cref="StoreSpokenAsync"/> did, handed back so the narration path can decide
    /// whether to book a re-attempt. Callers that do not book - the on-demand explain path - ignore it, and
    /// the state <see cref="StoreSpokenAsync"/> records is unchanged for them.
    /// </summary>
    public readonly record struct SpeechAttempt(bool ProducedAudio, TimeSpan? RetryAfter, bool WorthAnotherAttempt);

    /// <summary>
    /// ONE tenant's voice state. Every dictionary here is keyed by session id, and the ONLY way to reach one
    /// is through <see cref="StateFor"/> with a validated tenant - so a session id alone can never name a
    /// row. This is the in-memory twin of the per-tenant directory on disk: the isolation is the container,
    /// not a check performed at the point of read.
    /// </summary>
    /// <summary>
    /// What the last transcript read recorded, and WHEN IT STOPS BEING TAKEN ON TRUST.
    ///
    /// The deadline exists because a terminal read verdict is a claim about the world that can go stale, and
    /// the sweep acts on it by SKIPPING the session. Found in review: the first version of that skip was an
    /// unbounded `continue` whose only escape was <see cref="OnSessionWorking"/> - and on the hosted push
    /// path that clear is driven by TurnEndWatcher's 15-second sampler, which the codebase already documents
    /// as a racy sampled edge (see the note in GenerateOnceAsync about a Working transition falling between
    /// two samples). A session whose turn was quick enough to be missed would have kept a stale terminal
    /// marker forever, and - unlike before the skip existed - no later sweep could have discovered the
    /// recovery. A Director update that ADDS a history provider is exactly when that would bite.
    ///
    /// So the skip is time-bounded rather than permanent: it saves the repeated work that starves the
    /// sweep's small per-cycle budget, and it still re-reads on its own. Recovery no longer depends on
    /// catching an edge.
    /// </summary>
    private readonly record struct ReadFailure(HostedAiState State, DateTime RevalidateAfterUtc);

    /// <summary>How long a TERMINAL read verdict ("this agent exposes no conversation history") is taken on
    /// trust before the sweep reads once more to check it is still true. Long enough that a handful of such
    /// sessions cannot dominate the three generations a cycle allows; short enough that an agent which gains
    /// a history provider is picked up without anyone intervening.</summary>
    private static readonly TimeSpan TerminalReadRevalidateAfter = TimeSpan.FromMinutes(10);

    private sealed class TenantVoiceState
    {
        public readonly ConcurrentDictionary<string, byte> VoiceSessions = new();          // sid -> marker
        public readonly ConcurrentDictionary<string, VoiceReady> Ready = new();            // sid -> spoken+audio
        public readonly ConcurrentDictionary<string, byte> Generating = new();             // sid -> wingman is running now
        public readonly ConcurrentDictionary<string, HostedAiState> Unavailable = new();   // sid -> why voice is off (issue #939)
        public readonly ConcurrentDictionary<string, byte> NothingToNarrate = new();       // sid -> the last turn has no text reply to read aloud (waiting on a prompt)
        public readonly ConcurrentDictionary<string, byte> DirectorCannotSend = new();     // sid -> the owning Director's build cannot send conversations, so none will ever be stored
        public readonly ConcurrentDictionary<string, DateTime> PreferBackupUntil = new();  // sid -> UTC deadline while this session routes past a silent primary (issue devthrottle_internal#405)
        public readonly ConcurrentDictionary<string, byte> InFlight = new();               // sid -> a generation is running now

        /// <summary>
        /// Why the TRANSCRIPT READ for this session did not produce a conversation, or absent when the last
        /// read answered (issue #2561). Its OWN dictionary, deliberately not a value written into
        /// <see cref="Unavailable"/>, because a retry state has to carry its provenance.
        ///
        /// Writing it into Unavailable was wrong in both directions, and both were found in review. Setting
        /// it OVERWROTE a standing NeedsCredits / CapReached / NeedsKey - an actionable account condition
        /// replaced by a weaker "on its way", on the evidence of a failed read that says nothing about the
        /// account. And clearing it on a successful read ERASED a Retrying that the MODEL or SPEECH leg had
        /// set, which a transcript read has equally no evidence about; the row would flip to "no narration
        /// yet" for the length of another slow attempt and back again.
        ///
        /// Separate storage makes both correct by construction: the read writes and clears only its own
        /// fact, the account and provider states keep theirs, and <see cref="VoiceUnavailableFor"/> decides
        /// precedence in ONE place.
        /// </summary>
        public readonly ConcurrentDictionary<string, ReadFailure> ReadFailed = new();

        /// <summary>
        /// The model-leg retry ledger for ONE session's current unnarrated turn (issue #2676).
        ///
        /// KEYED ON THE REPLY, not on the session and not on a transition. The budget belongs to the turn
        /// that lost its voice, and the obvious way to reset it - the Working transition - is the one signal
        /// this file already knows it cannot trust: it is observed on a racy sampled edge and a quick turn
        /// can be missed entirely (see the note above GenerateAsync, and issue #1322, which is the same
        /// lesson learned about the narration cache). Resetting on the edge alone would let a turn whose
        /// edge was missed inherit the previous turn's exhausted budget and be denied its first re-attempt.
        /// Comparing the reply the failure was FOR removes that dependency, exactly as ShouldRegenerate did.
        ///
        /// <paramref name="Pending"/> is what keeps the abandoned verdict honest. A concurrent attempt - the
        /// sweep, or a person pressing Generate - can fail while a booked re-attempt is still waiting out its
        /// backoff. Counting that failure against the ladder would declare "nothing further is scheduled"
        /// while a task existed, which is the same false sentence this whole change removes, only inverted.
        /// </summary>
        /// <param name="ReplyKey">Identity of the reply this budget belongs to.</param>
        /// <param name="Booked">How many re-attempts of the ladder have been spent on that reply.</param>
        /// <param name="Pending">1 while a booked re-attempt is waiting out its backoff, else 0.</param>
        /// <param name="Reservation">
        /// Identifies THIS reservation, uniquely and for ever. Found in review, and the reply key alone is
        /// genuinely not enough: a ledger can be cleared by a new turn and then re-created for the SAME reply
        /// text, at which point an older attempt's rollback - or an older timer waking up - matches on reply
        /// key, believes the entry is its own, and rewinds or steals a reservation somebody else is relying
        /// on. The screen then promises audio backed by a task that will stand down when it fires. A counter
        /// makes ownership provable rather than probable.
        /// </param>
        /// <param name="NotBefore">
        /// The earliest this session's turn may call the MODEL again, or default when there is no such
        /// restriction. Set only when a provider's Retry-After was longer than this service will hold a
        /// promise open: refusing to book a re-attempt does not entitle us to keep calling, and the sweep
        /// comes past every 45 seconds. Honouring the number we were given is the whole point of reading it.
        ///
        /// PER SESSION AND PER TURN, never shared. The fleet-wide cooldowns that used to live in this file
        /// were removed in July because one session's 429 muted every other session; this reaches nothing
        /// outside the turn whose own call was refused.
        /// </param>
        public sealed record ModelRetryLedger(string ReplyKey, int Booked, int Pending, long Reservation, DateTime NotBefore = default);

        /// <summary>Guards <see cref="ModelRetries"/>. A plain lock rather than a concurrent dictionary
        /// because the decision - book the next rung, stand aside for one already booked, or abandon - reads
        /// and writes the entry as ONE step, and a compare-and-swap loop around a three-way decision is
        /// harder to read than the two lines it replaces.</summary>
        public readonly object ModelRetryGate = new();

        /// <summary>The retry ledger, one entry per session with a turn still owed a narration.</summary>
        public readonly Dictionary<string, ModelRetryLedger> ModelRetries = new(StringComparer.Ordinal);

        /// <summary>Hands out the reservation ids above. Read and incremented only under
        /// <see cref="ModelRetryGate"/>, so a plain field is enough.</summary>
        public long NextReservation = 1;

        /// <summary>
        /// Bumped every time a session's turn-scoped voice facts are reset - a new turn, a successful
        /// narration, voice switched off. An attempt captures it before it calls the model and checks it
        /// before it writes anything, so a slow attempt cannot report a verdict about a turn that has
        /// already been superseded.
        ///
        /// It exists because "is this ledger mine?" cannot be answered from the ledger alone. A cleared
        /// entry and a never-created one look identical, so an attempt arriving late after a clear would
        /// create a fresh entry, find nothing pending against it, and stamp "not narrated" onto a turn that
        /// had just started (found in review). The reply key does not settle it either - the next turn can
        /// carry the same reply text. A counter that only ever goes up does.
        /// </summary>
        public readonly ConcurrentDictionary<string, long> TurnEpochs = new(StringComparer.Ordinal);

        /// <summary>
        /// The model leg did not answer, every re-attempt in the budget has been spent, and NOTHING further
        /// is scheduled for this turn (issue #2676). The one fact that separates "still coming" from "not
        /// coming", and the reason it has to be stored rather than inferred: every other no-audio fact this
        /// service holds is either a wait or an actionable condition, so a turn nobody is working on any
        /// more matched the calm "voice on its way" sentence forever.
        /// </summary>
        public readonly ConcurrentDictionary<string, byte> NarrationAbandoned = new();
    }

    private readonly WingmanTranslator _translator;

    /// <summary>The wingman brain this service narrates with, exposed so the host can hand the SAME judge
    /// to the prompt menu guard (issue devthrottle_internal#1195) - one translator, one verdict cache, no
    /// second warm-brain wiring.</summary>
    public WingmanTranslator Translator => _translator;
    private readonly KeyVault _vault;
    private readonly TenantSettingsResolver _tenantSettings;
    /// <summary>Tenant partition key (see <see cref="CanonicalTenantKey"/>) -> that tenant's whole voice
    /// state. Ordinal, because the key is already canonical and two spellings must NEVER meet here.</summary>
    private readonly ConcurrentDictionary<string, TenantVoiceState> _tenants = new(StringComparer.Ordinal);
    /// <summary>The pre-partition file this Gateway used to keep the voice-session set in. Kept only so the
    /// one-time legacy migration in the constructor can find it; nothing reads or writes it afterwards.</summary>
    private readonly string _legacyPersistPath;
    /// <summary>The directory the per-tenant partitions live under.</summary>
    private readonly string _baseDir;
    private readonly HttpClient? _ttsHttp;   // test seam for TtsAsync (issue #939); the shared static when null
    private readonly Func<TenantId, string, string?>? _sessionTitleResolver;   // tenant + sid -> session title, spoken first
    /// <summary>Reads one session's stored conversation as the widget list the wingman already understands
    /// (turn-push mission, phase 3). Null answers "nothing stored for this session yet" - which is not a
    /// failure. Supplied by the host, which owns entering the tenant's scope before touching the store; this
    /// service is handed the finished answer so an asynchronous continuation can never read it under the
    /// wrong account. Null delegate leaves narration with nothing to read, which is what a Gateway with no
    /// store would honestly be.</summary>
    private readonly Func<TenantId, string, History.StoredConversation?>? _conversationReader;

    /// <summary>
    /// Whether the computer that OWNS this session is connected but running a build that cannot send its
    /// conversation - the one reason an empty store never fills. Supplied by the host because answering it
    /// needs two things this service does not hold: the pushed-session roster (to name the owning Director,
    /// and to know it is connected at all) and the turn-push capability registry (what that Director said
    /// about itself on Hello). Null in tests and self-host, which reads as "no such claim" - the safe
    /// direction, because it leaves the pre-existing "the push is simply late" behaviour untouched.
    /// </summary>
    private readonly Func<TenantId, string, bool>? _directorCannotSendConversation;

    /// <summary>
    /// True only for the EXACT form <see cref="Tenancy.TenantRegistry"/> mints: a canonical lowercase GUID.
    ///
    /// A tenant id becomes a DIRECTORY NAME and a dictionary key here, so it must be a shape this system
    /// actually produces - not merely "characters that look harmless". Two structural aliases have already
    /// been found on this exact boundary in <see cref="Prompts.GatewayPromptLog"/>, and this is the same
    /// guard for the same reason: <c>".."</c> is built entirely from harmless characters and canonicalizes
    /// to the PARENT partition, and an id differing only in letter case is a different identity to the
    /// case-sensitive tenants table while naming the SAME directory on Windows and Azure Files. So this
    /// accepts ONE spelling: parse strictly, then require the value to equal its own canonical round-trip.
    /// Anything else is refused rather than normalised - normalising is how two identities quietly share a
    /// folder, and this folder holds narration audio and reply TEXT.
    /// </summary>
    private static bool IsMintedAccountTenant(string value)
        => Guid.TryParseExact(value, "D", out var parsed)
           && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="tenant"/> can name a voice-state partition at all: self-host's single
    /// <see cref="TenantId.Local"/>, or a minted account tenant. This is the SAME decision
    /// <see cref="CanonicalTenantKey"/> makes, surfaced WITHOUT throwing, so a hot read path can ask "does
    /// this tenant even have a voice partition?" and get a plain false for one that has none - the reserved
    /// <see cref="TenantId.System"/> identity, an unminted id, an unresolved id - instead of an exception.
    ///
    /// The persisting/generating paths keep throwing for such a tenant (writing into a partition that cannot
    /// be named IS a bug); but a READ on the fleet-wide display-push seam must degrade to "no voice", never
    /// take the whole snapshot push down with it. MTR-10 Gap D reads the AMBIENT per-tenant of the display
    /// pass, and that ambient tenant is whatever tenant a Director is bound to - which need NOT be a minted
    /// voice partition (a self-host-shaped tenant on a hosted Gateway, a test tenant). The original Gap D
    /// change called the voice read unguarded and an unminted ambient tenant threw
    /// <see cref="ArgumentException"/> straight out of the DirectorHub <c>PushSnapshot</c> as a
    /// <c>HubException</c>, taking the whole fleet's display push down - the regression that reverted #1986.
    /// Guarding the read with this predicate answers the design-documented "no voice state at all" instead.
    /// </summary>
    public static bool CanNameVoicePartition(TenantId tenant)
        => tenant.IsValid && (tenant.IsLocal || IsMintedAccountTenant(tenant.Value));

    /// <summary>
    /// The canonical partition key for a tenant - the single string used BOTH as the in-memory bucket key and
    /// as the on-disk directory name, so the two can never disagree about which tenant a session belongs to.
    ///
    /// <see cref="TenantId.Local"/> is the fixed literal "local" (self-host's one tenant). Every other
    /// partition must be a minted account tenant; the reserved <see cref="TenantId.System"/> identity is
    /// deliberately REFUSED rather than given a partition, because no narration belongs to it - the safe
    /// answer is that it has no voice state at all.
    /// </summary>
    private static string CanonicalTenantKey(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("Voice state needs a valid tenant; an unresolved tenant is denied, never defaulted.", nameof(tenant));
        if (tenant.IsLocal)
            return TenantId.Local.Value;
        if (!IsMintedAccountTenant(tenant.Value))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' is not a minted account tenant and cannot name a voice-state partition.",
                nameof(tenant));
        return tenant.Value;
    }

    /// <summary>This tenant's in-memory bucket, created on first touch. The validation runs FIRST, so an
    /// unminted or unresolved tenant never gets a bucket at all.</summary>
    private TenantVoiceState StateFor(TenantId tenant)
        => _tenants.GetOrAdd(CanonicalTenantKey(tenant), _ => new TenantVoiceState());

    /// <summary>
    /// The directory holding ONE tenant's voice state (its voice-sessions.json and its voice-audio folder).
    /// The tenant id is a PATH COMPONENT, which is the whole point: a read for tenant A builds a path under
    /// tenant A's folder and cannot name a file in tenant B's.
    /// </summary>
    public string PartitionDirectoryFor(TenantId tenant)
    {
        var key = CanonicalTenantKey(tenant);
        var combined = Path.Combine(_baseDir, "tenants", key);

        // Belt and braces, because the cost of being wrong here is one tenant playing another's narration:
        // the result must actually LIE INSIDE the partition root. CanonicalTenantKey already excludes
        // traversal, so this can only fire if that guard is ever loosened - which is exactly when it is wanted.
        var expectedRoot = Path.GetFullPath(Path.Combine(_baseDir, "tenants")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(combined).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' resolves outside the voice-state partition root.", nameof(tenant));

        return combined;
    }

    /// <summary>The file holding one tenant's set of voice sessions.</summary>
    private string PersistPathFor(TenantId tenant) => Path.Combine(PartitionDirectoryFor(tenant), "voice-sessions.json");

    /// <summary>The folder holding one tenant's cached narration clips and their metadata.</summary>
    private string AudioDirFor(TenantId tenant) => Path.Combine(PartitionDirectoryFor(tenant), "voice-audio");

    /// <summary>Upper bound on an accepted session id (see <see cref="SafeClipPath"/>). A real session id is a
    /// GUID or short token - well under this - so a longer one is a malformed/hostile input, refused before a
    /// path is built. Also keeps the clip file name comfortably inside any filesystem's component limit.</summary>
    private const int MaxSidLength = 128;

    /// <summary>
    /// Resolve the on-disk path of one clip file (the <c>.mp3</c> or the <c>.json</c>) inside a tenant's
    /// voice-audio directory, REFUSING a session id that is not a single safe path segment - returning null,
    /// on which the caller writes or deletes NOTHING.
    ///
    /// The session id becomes a FILE NAME here, exactly as the tenant id becomes a DIRECTORY NAME in
    /// <see cref="CanonicalTenantKey"/> - so it gets the same treatment for the same reason. The tenant is a
    /// shape this system mints and is validated; the session id, on the PERSISTING path, is NOT. A hostile
    /// Director can advertise any non-empty string as a pushed session id, and the persisting callers - the
    /// <c>/sessions/voice-mode/all</c> fan-out and the turn-end narration sweep - carry whatever id they are
    /// handed with no <c>Guid.TryParse</c> gate (the interactive request endpoints that DO gate are a
    /// different door). So <c>"../../&lt;other-tenant&gt;/voice-audio/&lt;victim&gt;"</c> is a real input, and
    /// concatenated raw it walks the write out of this tenant's partition and into another's. Two independent
    /// checks, BOTH required - a shape that slips one is caught by the other:
    ///  - the id must be a single safe file-name atom: non-empty, not <c>"."</c> or <c>".."</c>, no longer
    ///    than <see cref="MaxSidLength"/> characters, and every character drawn from a strict ALLOW-LIST
    ///    (<c>[A-Za-z0-9._-]</c>). An allow-list is used deliberately rather than a separator/invalid-char
    ///    denylist: a denylist that only bans <c>/ \ :</c> and the platform's invalid chars still ACCEPTS a
    ///    percent-encoded traversal such as <c>"%2e%2e%2f%2e%2e%2fescape"</c> (no separator, all "legal"
    ///    file-name chars) and an unbounded, over-long segment (a 300-char id). The allow-list rejects both
    ///    - the <c>'%'</c> is not on it and the length is bounded - BEFORE any path is built; and
    ///  - the fully-resolved path must still lie INSIDE this tenant's voice-audio directory (canonical
    ///    containment), so even a shape this list did not anticipate cannot escape the partition.
    /// A legitimate session id (a GUID) is entirely allow-list characters and well under the length bound, so
    /// it passes untouched; anything else is refused rather than coerced, the same rule the tenant partition
    /// already applies one level up.
    /// </summary>
    private string? SafeClipPath(TenantId tenant, string sid, string extension)
    {
        if (string.IsNullOrWhiteSpace(sid)) return null;
        if (sid is "." or "..") return null;
        if (sid.Length > MaxSidLength) return null;
        foreach (var c in sid)
        {
            var allowed = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '.' or '_' or '-';
            if (!allowed) return null;
        }

        var audioDir = AudioDirFor(tenant);
        var combined = Path.Combine(audioDir, sid + extension);
        var root = Path.GetFullPath(audioDir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(combined);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }

    /// <summary>
    /// The one HTTP client for the narration speech leg, used whenever no test client is injected.
    ///
    /// Static and never disposed, matching the hosted-call pattern used across this codebase
    /// (AiModelsEndpoint, CarModeChat, HostedInferenceBrain, ...) and the /wingman/tts endpoint's own
    /// SharedTtsHttp. Safe to share because the credential goes on each REQUEST inside TtsSynthesis,
    /// never on this client's default headers.
    ///
    /// Timeout is INFINITE deliberately: TtsSynthesis owns the deadline (a 15-second per-attempt cap on
    /// a linked CancellationTokenSource, plus one retry). A second, slower client-level timeout racing
    /// it would only make a stall harder to read. One timeout, one owner.
    /// </summary>
    private static readonly HttpClient SharedTtsHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    // THERE IS DELIBERATELY NO FLEET-WIDE GATE OR CONCURRENCY CAP HERE (owner's call, 2026-07-17).
    //
    // Every voice session calls the hosted relay on its own and discovers an outage for itself. There
    // used to be two shared cooldown gates (a _rateGate on the model leg and a _ttsGate on the speech
    // leg): a single 429 or 5xx from one session's call armed a fleet-wide cooldown, and every OTHER
    // session was then SKIPPED for up to 120 seconds while one "probe" call tested recovery. That is the
    // coupling that turned a partial or flaky outage into total fleet silence - one call's bad luck
    // muted every session on the machine. It was removed on 2026-07-17.
    //
    // The principle: a service outage may be FLAKY (some calls fail, some succeed), so the honest thing
    // is to let every session try - the ones that can get through, do; the ones that fail, fail on their
    // own and retry on their own next cadence (turn-end, or the 45s idle sweep). We are a RELAY of the
    // hosted service and do not model its health on its behalf: no breaker, no shared cooldown, no
    // invented ceiling. This matches the "the hosted proxy is a thin pass-through" law. A 429 or a 5xx
    // is recorded as this ONE session's unavailable-state so its UI can say so, and nothing more - it
    // never reaches across to another session. Do NOT reintroduce a shared gate or a constant cap here.
    //
    // The per-session coalescing marker (TenantVoiceState.InFlight) stays: it is NOT a fleet gate. It
    // coalesces a single session so two generations for the SAME session never run at once (a slow turn
    // overlapping the idle sweep would otherwise double the spend). It never makes one session wait on
    // another, and it is per tenant like everything else here.

    /// <summary>On-disk shape of one ready session's metadata (the audio bytes live next to it as
    /// an .mp3). Persisted so the play triangle / playability survives a gateway restart (issue #553).</summary>
    private sealed record PersistedVoice(string Spoken, string Reply, DateTime AtUtc, string? ContentType = null, bool ServedViaFallback = false, string? SourceIdentity = null);

    /// <param name="ttsHttpClient">Optional HTTP client for the text-to-speech call (tests inject a stub
    /// over a fake handler, issue #939). A per-call 60-second client is created when null.</param>
    /// <param name="sessionTitleResolver">Resolves a session id to its title, which the wingman speaks
    /// first so a listener knows which session is talking. The host wires this to the pushed-session
    /// store; a null resolver (or one returning null for an unknown session) simply means no title is
    /// spoken, which is the correct degrade - a narration with no title is worth far more than none.</param>
    public WingmanVoiceService(
        Func<TenantId, Core.Configuration.WingmanModelRole, CancellationToken, Task<IAgentBrain>> brainProvider,
        KeyVault vault,
        TenantSettingsResolver tenantSettings,
        string? persistPath = null,
        Func<string>? instructionsProvider = null,
        HttpClient? ttsHttpClient = null,
        Func<TenantId, string, string?>? sessionTitleResolver = null,
        Func<TenantId, string, History.StoredConversation?>? conversationReader = null,
        Func<TenantId, string, bool>? directorCannotSendConversation = null)
    {
        _conversationReader = conversationReader;
        _directorCannotSendConversation = directorCannotSendConversation;
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _tenantSettings = tenantSettings ?? throw new ArgumentNullException(nameof(tenantSettings));
        // The account's spoken language comes from the same per-tenant resolver this service already
        // uses for the voice and the model, read at CALL time so a change on the Language tab applies
        // to the next narration (issue #1008).
        _translator = new WingmanTranslator(
            brainProvider, _tenantSettings.SpokenLanguage, instructionsProvider: instructionsProvider);
        _sessionTitleResolver = sessionTitleResolver;
        // Post-cut: the owning Director is reached through the tunnel-only SessionVerbClient the callers pass
        // into GenerateAsync, so this service holds no Director client.
        _ttsHttp = ttsHttpClient;
        // Which sessions are voice sessions survives a gateway restart. Issue #553: the per-session
        // audio cache is now ALSO durable - it is persisted next to voice-sessions.json under a
        // "voice-audio" folder so the triangle does not vanish-then-reappear-empty across a restart
        // and a tap after restart plays. Tests pass an isolated path so the two never collide.
        //
        // VOICE V1: both now live inside a per-tenant partition under _baseDir/tenants/<id>/. The
        // constructor argument still names the LEGACY (pre-partition) file, because that is what the
        // one-time migration below has to find, and because it is how every existing caller and test
        // points this service at an isolated directory.
        _legacyPersistPath = persistPath ?? Path.Combine(CcStorage.Root(), "voice-sessions.json");
        var baseDir = Path.GetDirectoryName(_legacyPersistPath);
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = CcStorage.Root();
        _baseDir = baseDir;
        MigrateLegacyUnpartitionedState();
        LoadAllPartitions();
    }

    /// <summary>
    /// Deal, once, with the voice state that was written BEFORE this store was partitioned: a single
    /// voice-sessions.json and a single voice-audio folder shared by whoever happened to be using the
    /// Gateway. It has no tenant recorded anywhere, so the two deployment modes get OPPOSITE treatment, and
    /// the mode is read from <see cref="GatewayHostedMode.IsHosted"/> DIRECTLY - never from an argument a
    /// caller could omit, because an omitted argument would fail open into "keep it".
    ///
    ///  - HOSTED: DELETE it. The clip cannot be attributed to an account - the Director that made it may be
    ///    long gone - and guessing an owner would hand one customer another customer's narration. A cached
    ///    narration clip is regenerable; a mis-attributed one is a disclosure. Losing it is the cheap outcome.
    ///  - SELF-HOST: MOVE it into the Local partition. Self-host has exactly one tenant, so attribution is
    ///    unambiguous and the user keeps every ready clip across the upgrade.
    ///
    /// Best-effort and idempotent: a failure is logged and boot continues (a cached narration must never
    /// stop the Gateway starting), and a second run finds nothing left to do.
    /// </summary>
    private void MigrateLegacyUnpartitionedState()
    {
        var legacyAudioDir = Path.Combine(_baseDir, "voice-audio");
        var hasLegacy = File.Exists(_legacyPersistPath) || Directory.Exists(legacyAudioDir);
        if (!hasLegacy) return;

        if (GatewayHostedMode.IsHosted)
        {
            try
            {
                if (File.Exists(_legacyPersistPath)) File.Delete(_legacyPersistPath);
                if (Directory.Exists(legacyAudioDir)) Directory.Delete(legacyAudioDir, recursive: true);
                FileLog.Write("[WingmanVoiceService] hosted: deleted the pre-partition voice state - it carries no tenant, and a clip whose owner cannot be established is deleted rather than guessed");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WingmanVoiceService] hosted: deleting the pre-partition voice state FAILED: {ex.Message}");
            }
            return;
        }

        try
        {
            var localDir = PartitionDirectoryFor(TenantId.Local);
            Directory.CreateDirectory(localDir);

            var targetPersist = PersistPathFor(TenantId.Local);
            if (File.Exists(_legacyPersistPath) && !File.Exists(targetPersist))
                File.Move(_legacyPersistPath, targetPersist);
            else if (File.Exists(_legacyPersistPath))
                File.Delete(_legacyPersistPath);   // the partition already has its own; the legacy copy is superseded

            var targetAudioDir = AudioDirFor(TenantId.Local);
            if (Directory.Exists(legacyAudioDir))
            {
                Directory.CreateDirectory(targetAudioDir);
                foreach (var file in Directory.EnumerateFiles(legacyAudioDir))
                {
                    var target = Path.Combine(targetAudioDir, Path.GetFileName(file));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(file, target);
                }
                Directory.Delete(legacyAudioDir, recursive: true);
            }
            FileLog.Write("[WingmanVoiceService] self-host: moved the pre-partition voice state into the local tenant partition (one tenant, so attribution is unambiguous)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanVoiceService] self-host: moving the pre-partition voice state FAILED: {ex.Message}");
        }
    }

    /// <summary>Load every tenant partition present on disk. A directory whose name is not a partition key
    /// this system mints is SKIPPED loudly rather than loaded under some coerced name - the same refusal the
    /// write path applies, so a hand-made or half-renamed folder can never become a tenant.
    ///
    /// The two loads are deliberately NOT equal (issue #2203). Which sessions are voice sessions is a
    /// correctness fact - the turn-end watcher consults it the moment it starts - and it costs one small
    /// file per tenant, so it is read here and now. The ready-AUDIO cache is a warm-start convenience that
    /// costs one full mp3 read per cached session (measured: 106 files, 11.6 seconds off the hosted SMB
    /// mount), and every byte of it sat in front of the port bind. A cache that is not loaded yet behaves
    /// exactly like a cache miss, which the voice path already handles by regenerating - whereas a port that
    /// is not open yet is an outage. So the audio warms in the background, after the bind.</summary>
    private void LoadAllPartitions()
    {
        var tenantsRoot = Path.Combine(_baseDir, "tenants");
        if (!Directory.Exists(tenantsRoot)) return;
        var tenants = new List<TenantId>();
        foreach (var dir in Directory.EnumerateDirectories(tenantsRoot))
        {
            var name = Path.GetFileName(dir);
            TenantId tenant;
            try
            {
                tenant = new TenantId(name);
                _ = CanonicalTenantKey(tenant);
            }
            catch (ArgumentException)
            {
                FileLog.Write($"[WingmanVoiceService] skipping voice partition directory '{name}' - not a tenant this system mints");
                continue;
            }
            LoadVoiceSessions(tenant);
            tenants.Add(tenant);
        }
        WarmReadyAudio(tenants);
    }

    /// <summary>
    /// Read the cached voice audio for each tenant off the background thread pool, so the cost never lands
    /// in front of the port bind. The work is published as <see cref="ReadyAudioWarmup"/> so a caller that
    /// genuinely needs the cache populated can wait for it deterministically instead of racing it.
    /// </summary>
    private void WarmReadyAudio(List<TenantId> tenants)
    {
        if (tenants.Count == 0) return;

        ReadyAudioWarmup = Task.Run(() =>
        {
            foreach (var tenant in tenants) LoadReadyAudio(tenant);
            FileLog.Write($"[WingmanVoiceService] ready-audio warm load finished for {tenants.Count} tenant partition(s) (background; the port was already open)");
        });
    }

    /// <summary>
    /// Completes when the ready-audio cache has finished loading from disk. Nothing in the serving path
    /// waits on it - a cache that is still loading behaves as a cache miss and regenerates, which is the
    /// whole reason it was moved off the startup path (issue #2203). Tests that assert on the reloaded
    /// cache await this rather than racing the background read.
    /// </summary>
    internal Task ReadyAudioWarmup { get; private set; } = Task.CompletedTask;

    private void LoadVoiceSessions(TenantId tenant)
    {
        try
        {
            var path = PersistPathFor(tenant);
            if (!File.Exists(path)) return;
            var state = StateFor(tenant);
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
            if (ids is not null) foreach (var id in ids) if (!string.IsNullOrWhiteSpace(id)) state.VoiceSessions[id] = 1;
            FileLog.Write($"[WingmanVoiceService] loaded {state.VoiceSessions.Count} voice session(s) from disk for tenant={tenant.ToLogString()}");
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] load voice sessions FAILED for tenant={tenant.ToLogString()}: {Redact(ex.Message, tenant)}"); }
    }

    private void SaveVoiceSessions(TenantId tenant)
    {
        try
        {
            var path = PersistPathFor(tenant);
            Directory.CreateDirectory(PartitionDirectoryFor(tenant));
            File.WriteAllText(path, JsonSerializer.Serialize(StateFor(tenant).VoiceSessions.Keys.ToArray()));
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] save voice sessions FAILED for tenant={tenant.ToLogString()}: {Redact(ex.Message, tenant)}"); }
    }

    /// <summary>
    /// Replace a tenant's raw account id with its hashed log form anywhere it appears in text bound for the
    /// log - in practice a file path inside an exception message, since the partition directory IS the
    /// tenant id. A single exact substitution of a value we hold, not a general-purpose scrub: it is
    /// complete for the one way the id can get in here, and it keeps the failure LOUD rather than swallowing
    /// the message to be safe. Same rule as <see cref="Prompts.GatewayPromptLog"/>.
    /// </summary>
    private static string Redact(string text, TenantId tenant)
        => string.IsNullOrEmpty(text) || !tenant.IsValid
            ? text
            : text.Replace(tenant.Value, tenant.ToLogString(), StringComparison.Ordinal);

    /// <summary>Restore the per-session ready audio cache from disk on startup (issue #553) so
    /// HasVoice / ReadySessionIds survive a gateway restart. A session is only loaded ready when BOTH
    /// its metadata (.json) and its audio (.mp3, non-empty) are present - the "if anything fails,
    /// remove the triangle" rule extends to a half-written or missing cache.</summary>
    private void LoadReadyAudio(TenantId tenant)
    {
        try
        {
            var audioDir = AudioDirFor(tenant);
            if (!Directory.Exists(audioDir)) return;
            var state = StateFor(tenant);
            var loaded = 0;
            foreach (var metaPath in Directory.EnumerateFiles(audioDir, "*.json"))
            {
                var sid = Path.GetFileNameWithoutExtension(metaPath);
                var audioPath = Path.Combine(audioDir, sid + ".mp3");
                if (!File.Exists(audioPath)) continue;
                var audio = File.ReadAllBytes(audioPath);
                if (audio.Length == 0) continue;
                var meta = JsonSerializer.Deserialize<PersistedVoice>(File.ReadAllText(metaPath));
                if (meta is null) continue;
                var contentType = NormalizeContentType(meta.ContentType) ?? DetectAudioContentType(audio);
                state.Ready[sid] = new VoiceReady(meta.Spoken, meta.Reply, audio, meta.AtUtc, contentType, meta.ServedViaFallback, meta.SourceIdentity);
                loaded++;
            }
            FileLog.Write($"[WingmanVoiceService] loaded {loaded} ready voice audio cache(s) from disk for tenant={tenant.ToLogString()}");
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] load ready audio FAILED for tenant={tenant.ToLogString()}: {Redact(ex.Message, tenant)}"); }
    }

    private void SaveReadyAudio(TenantId tenant, string sid, VoiceReady ready)
    {
        try
        {
            // The session id is a file name here, and on the persisting path it is caller-controlled (see
            // SafeClipPath): refuse a traversal / separator-bearing / non-canonical id rather than let it
            // write outside this tenant's partition. Refused -> write NOTHING (both files or neither).
            var mp3Path = SafeClipPath(tenant, sid, ".mp3");
            var metaPath = SafeClipPath(tenant, sid, ".json");
            if (mp3Path is null || metaPath is null)
            {
                FileLog.Write($"[WingmanVoiceService] save ready audio REFUSED (session id is not a safe path segment) tenant={tenant.ToLogString()} sid={sid}");
                return;
            }
            Directory.CreateDirectory(AudioDirFor(tenant));
            // Write the audio first, then the metadata, so a startup load (which requires BOTH the
            // .mp3 and the .json) never sees a session ready before its bytes are on disk.
            File.WriteAllBytes(mp3Path, ready.Audio);
            File.WriteAllText(metaPath,
                JsonSerializer.Serialize(new PersistedVoice(ready.Spoken, ready.Reply, ready.AtUtc, ready.ContentType, ready.ServedViaFallback, ready.SourceIdentity)));
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] save ready audio FAILED tenant={tenant.ToLogString()} sid={sid}: {Redact(ex.Message, tenant)}"); }
    }

    private void DeleteReadyAudio(TenantId tenant, string sid)
    {
        try
        {
            // Same guard as the save sink: the session id is a caller-controlled file name, so a traversal
            // id must not let a delete reach another tenant's clip. Refused -> delete NOTHING. A safe id
            // could only ever have written its own files, so there is nothing legitimate to miss here.
            var meta = SafeClipPath(tenant, sid, ".json");
            var audio = SafeClipPath(tenant, sid, ".mp3");
            if (meta is null || audio is null)
            {
                FileLog.Write($"[WingmanVoiceService] delete ready audio REFUSED (session id is not a safe path segment) tenant={tenant.ToLogString()} sid={sid}");
                return;
            }
            if (File.Exists(meta)) File.Delete(meta);
            if (File.Exists(audio)) File.Delete(audio);
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] delete ready audio FAILED tenant={tenant.ToLogString()} sid={sid}: {Redact(ex.Message, tenant)}"); }
    }

    /// <summary>This session has had voice used on it at least once, within this tenant.</summary>
    public bool IsVoiceSession(TenantId tenant, string sid) => StateFor(tenant).VoiceSessions.ContainsKey(sid);

    /// <summary>Every session the gateway is keeping voice for, within ONE tenant (the persisted set).</summary>
    public IReadOnlyCollection<string> VoiceSessionIds(TenantId tenant) => StateFor(tenant).VoiceSessions.Keys.ToArray();

    /// <summary>
    /// Every tenant this service currently holds voice state for. The ONLY method here that is not scoped to
    /// a single tenant, and it deliberately returns tenants rather than sessions: a caller that must visit
    /// all of them (the background narration sweep) iterates tenants and then asks each one for its own
    /// sessions, so it still never sees a session id outside the tenant it is asking about.
    /// </summary>
    public IReadOnlyCollection<TenantId> PartitionedTenants()
        => _tenants.Keys.Select(k => new TenantId(k)).ToArray();

    /// <summary>True when this session currently has a fresh, playable cached summary, within this tenant.</summary>
    public bool HasVoice(TenantId tenant, string sid) => StateFor(tenant).Ready.ContainsKey(sid);

    /// <summary>The response header the cloud speech proxy sets when it quietly failed the primary
    /// provider over to the backup (Phase 1). Its mere PRESENCE means "served via backup". We key on
    /// presence ONLY and never read the value: the cloud sends a generic opaque marker ("1"), NOT the
    /// provider name, so which provider served stays invisible even to a direct API caller who inspects
    /// headers. Reading presence is how the Gateway learns to show the generic backup-voice notice (a
    /// success-with-a-note), and it is deliberately out-of-band so it never touches the audio.</summary>
    private const string FallbackHeaderName = "X-DevThrottle-TTS-Fallback";

    /// <summary>True when this session's current ready clip was made by the BACKUP voice provider (the
    /// primary was overloaded and the cloud proxy failed over). Fed to <see cref="VoiceDisplayFold"/> so
    /// the Voice screen shows the generic backup-voice notice. False when there is no ready clip, or the
    /// ready clip was served normally. Never an outage signal - a fallback IS a successful narration.</summary>
    public bool ServedViaFallbackFor(TenantId tenant, string sid)
        => StateFor(tenant).Ready.TryGetValue(sid, out var v) && v.ServedViaFallback;

    /// <summary>
    /// Whether a turn-end should (re)generate the spoken narration for this session. True when there is
    /// something to say (<paramref name="currentSourceIdentity"/> non-empty) and it is NOT already the exact
    /// source we hold cached audio for. This replaces the old bare "does any narration exist" guard
    /// (issue #1322): comparing the reply TEXT means a genuinely new or changed reply always regenerates
    /// even when the Working transition that would have cleared the cache was never observed (a racy
    /// sampled edge, missed on multi-part turns), while a redundant re-hit of the SAME turn still stays
    /// quiet so a client already playing this turn's clip is never disturbed (no re-mint, no yellow flip).
    /// Reply text is compared trimmed + ordinal - the two sources (cache vs the live /turns widget) are
    /// the same JSONL text block, so an unchanged turn matches exactly.
    /// </summary>
    internal bool ShouldRegenerate(TenantId tenant, string sid, string? currentSourceIdentity)
    {
        if (string.IsNullOrWhiteSpace(currentSourceIdentity)) return false;   // nothing to narrate yet
        if (!StateFor(tenant).Ready.TryGetValue(sid, out var cached)) return true;   // never narrated -> make it
        return !string.Equals(
            (cached.SourceIdentity ?? cached.Reply)?.Trim(),
            currentSourceIdentity.Trim(),
            StringComparison.Ordinal);
    }

    /// <summary>The sessions within ONE tenant that currently have a ready, playable spoken summary.</summary>
    public IReadOnlyCollection<string> ReadySessionIds(TenantId tenant) => StateFor(tenant).Ready.Keys.ToArray();

    /// <summary>
    /// True while the wingman is actively producing this session's spoken summary (issue #531
    /// voice mode). This is the window the session must show YELLOW - "kind of not ready yet" -
    /// before flipping back to red when it needs the user again. The gateway surfaces it through
    /// the "Briefing" yellow path in the /sessions aggregation (see GatewayEndpoints voiceGeneratingFor).
    /// </summary>
    public bool IsGenerating(TenantId tenant, string sid) => StateFor(tenant).Generating.ContainsKey(sid);

    /// <summary>Mark the wingman as running for this session (turns the session yellow).</summary>
    public void BeginGenerating(TenantId tenant, string sid) => StateFor(tenant).Generating[sid] = 1;

    /// <summary>The wingman finished running for this session (back to red / its raw color).</summary>
    public void EndGenerating(TenantId tenant, string sid) => StateFor(tenant).Generating.TryRemove(sid, out _);

    /// <summary>
    /// Why the Gateway could not keep this session's voice, or null when voice is fine (issue #939).
    /// Set when a turn-end generation hit an out-of-credits / cap / no-key condition instead of being
    /// swallowed silently; cleared on the next successful generation and when voice is turned off. The
    /// <c>/sessions</c> aggregation stamps the shared message onto <c>SessionDto.VoiceUnavailable</c>
    /// from this so the owning UI shows the consistent state.
    /// </summary>
    /// <remarks>
    /// THE ONE PLACE the two sources are ranked (issue #2561). An account / provider condition
    /// (<c>Unavailable</c>) OUTRANKS a transcript-read failure (<c>ReadFailed</c>), because it is the more
    /// actionable of the two and the more certain: "add credit" tells the owner something to do, while a
    /// failed read is a condition the sweep is already chasing on its own. Ranking here rather than by
    /// overwriting one dictionary with the other is what lets each writer clear only what it established.
    /// </remarks>
    public HostedAiState? VoiceUnavailableFor(TenantId tenant, string sid)
    {
        var state = StateFor(tenant);
        var read = state.ReadFailed.TryGetValue(sid, out var r) ? r.State : (HostedAiState?)null;
        // A TERMINAL read verdict outranks EVERYTHING. Found in review: ranking the shared map first meant a
        // stale NeedsCredits - or a stale model-leg Retrying - could sit in front of "this agent has no
        // conversation to read", and for such a session nothing clears the shared value, because it can never
        // reach a successful synthesis. The reader would be told to add credit to fix a problem credit cannot
        // fix. No account or provider action makes an unreadable transcript readable, so this fact wins.
        if (read == HostedAiState.Unavailable) return HostedAiState.Unavailable;
        if (state.Unavailable.TryGetValue(sid, out var s)) return s;
        return read;
    }

    /// <summary>
    /// Record why this session's TRANSCRIPT READ did not produce a conversation - <see cref="HostedAiState.Retrying"/>
    /// for a condition that can become readable (the tunnel did not answer, the transcript has not appeared
    /// yet, a parse failed), or <see cref="HostedAiState.Unavailable"/> for one that cannot without a change
    /// of agent (this agent exposes no conversation history at all). Kept apart from the account / provider
    /// state so neither overwrites the other - see <c>TenantVoiceState.ReadFailed</c>.
    /// </summary>
    public void NoteReadFailed(TenantId tenant, string sid, HostedAiState state)
        => StateFor(tenant).ReadFailed[sid] = new ReadFailure(
            state,
            // Only a TERMINAL verdict is taken on trust for a while; a retryable one is re-read next cycle,
            // so its deadline is already past.
            state == HostedAiState.Unavailable ? DateTime.UtcNow + TerminalReadRevalidateAfter : DateTime.MinValue);

    /// <summary>The transcript read answered, so whatever the last failed read recorded is over. Clears ONLY
    /// the read's own fact: a model timeout, a rate limit, or a standing account condition are all things a
    /// successful transcript read has no evidence about.</summary>
    public void ClearReadFailed(TenantId tenant, string sid) => StateFor(tenant).ReadFailed.TryRemove(sid, out _);

    /// <summary>What the last transcript read recorded, or null when it answered. Exposed so the voice sweep
    /// can leave a session whose agent will NEVER have a conversation out of its per-cycle budget.</summary>
    public HostedAiState? ReadFailedFor(TenantId tenant, string sid)
        => StateFor(tenant).ReadFailed.TryGetValue(sid, out var s) ? s.State : (HostedAiState?)null;

    /// <summary>
    /// True when the voice sweep should leave this session alone THIS CYCLE: the last read said the agent
    /// exposes no conversation history at all, and that verdict has not yet come up for revalidation. It is a
    /// bounded skip, never a permanent one - see <see cref="ReadFailure"/> for why an unbounded one wedged.
    /// </summary>
    public bool ShouldSkipSweep(TenantId tenant, string sid)
        => StateFor(tenant).ReadFailed.TryGetValue(sid, out var s)
           && s.State == HostedAiState.Unavailable
           && DateTime.UtcNow < s.RevalidateAfterUtc;

    /// <summary>
    /// True when this session's latest turn has NO assistant text reply to read aloud - it is waiting for
    /// the user on a prompt / menu, not a text answer. A NON-failure: there is genuinely nothing to
    /// narrate, distinct from "the audio has not been made yet". Recorded when a generation attempt (auto
    /// or on-demand) finds an empty last reply; cleared the moment a text reply exists, on a new turn, or
    /// when voice is turned off. The <c>/sessions</c> aggregation feeds this to <see cref="VoiceDisplayFold"/>
    /// so the screen shows an honest "nothing to read aloud" instead of a Generate button that cannot work.
    /// </summary>
    public bool NothingToNarrateFor(TenantId tenant, string sid) => StateFor(tenant).NothingToNarrate.ContainsKey(sid);

    /// <summary>
    /// True when the computer that owns this session is connected but running a build that cannot send its
    /// conversation, so no narration can ever be produced for it until that computer is updated. A TERMINAL
    /// condition with a plain remedy, not a wait: it feeds <see cref="VoiceDisplayFold"/> so the screen says
    /// "Update DevThrottle" rather than counting minutes at a session whose voice is never coming.
    ///
    /// Recorded only from an ACTUAL generation attempt that found an empty store, never guessed from the
    /// capability registry alone - so a session that has simply not been swept yet makes no claim about
    /// anybody's build.
    /// </summary>
    public bool DirectorCannotSendConversationFor(TenantId tenant, string sid) => StateFor(tenant).DirectorCannotSend.ContainsKey(sid);

    /// <summary>Set or clear the "nothing to narrate" fact from a caller that has already read the turn
    /// (the on-demand explain path): true when the last reply is empty (waiting on a prompt), false the
    /// moment a text reply exists. The auto path sets/clears it inline in <c>GenerateOnceAsync</c>.</summary>
    public void SetNothingToNarrate(TenantId tenant, string sid, bool nothing)
    {
        var state = StateFor(tenant);
        if (nothing)
        {
            state.NothingToNarrate[sid] = 1;
            ClearModelRetryState(state, sid);   // fresher evidence than an old turn's model timeout (issue #2676)
        }
        else state.NothingToNarrate.TryRemove(sid, out _);
    }

    /// <summary>
    /// Record that a model/translation call did not answer in time (a bounded timeout or a transport
    /// failure), so this session shows the calm "voice on its way" retrying state rather than a silent
    /// failure or a false "this session's computer is offline". The on-demand explain path uses this
    /// when its translation times out; the auto path sets the same state inline. Cleared on the next
    /// successful generation, on the Working transition, and when voice is turned off - exactly like the
    /// speech-leg unavailable state.
    /// </summary>
    public void NoteRetrying(TenantId tenant, string sid) => StateFor(tenant).Unavailable[sid] = HostedAiState.Retrying;

    /// <summary>
    /// THE RETRY THE VOICE PATH OWNS (issue #2676). One entry per re-attempt, in order, giving the delay
    /// before it; the length of the array IS the cap.
    ///
    /// It exists because the sentence "the session retries on its own" was not true of anything. A model
    /// non-answer recorded <see cref="HostedAiState.Retrying"/> and returned, and the only thing that would
    /// ever come back to that session was the background voice sweep - a shared, three-per-cycle budget that
    /// belongs to no session in particular and that another account's sessions were consuming whole (issue
    /// #2675). On 2026-09-04 four model timeouts produced three sessions sitting yellow for eleven, eighteen
    /// and unlimited minutes, each showing "retrying automatically", with nothing scheduled anywhere.
    ///
    /// So the leg that failed schedules its own re-attempt. Backed off because a model that just failed to
    /// answer within its deadline is the last thing to hammer, and capped because a retry loop with no end
    /// is how one stalled session quietly spends an account's credit all night. Past the cap the promise
    /// STOPS - see <see cref="NarrationAbandonedFor"/>; the sweep may still find the session later, but
    /// nothing tells the reader an arrival is on its way when no attempt is booked.
    ///
    /// The three delays cover the shape the evidence showed: a stall that clears in seconds, one that clears
    /// in a minute, and one that does not clear at all.
    ///
    /// HOW LONG THE WHOLE LADDER TAKES, stated properly because an earlier version of this comment claimed
    /// it finished inside the three minutes <see cref="VoiceDisplayFold.GaveUpAfter"/> allows a wait to run,
    /// and that was arithmetic with the attempts left out (found in review). The model's own deadline is 60
    /// seconds, so four attempts plus 10 + 30 + 90 seconds of backoff is about six minutes and ten seconds
    /// to the abandoned verdict, and longer if an attempt queues behind the serialized brain. That is
    /// deliberate and the two numbers do different jobs: at three minutes the fold already stops saying the
    /// audio is coming SOON, and at about six the service stops claiming anything is coming at all. Against
    /// the eleven and eighteen minutes of silence this replaces, and set against the cost of abandoning a
    /// narration that a fourth attempt would have produced, six minutes is the right side of that trade.
    /// </summary>
    private static readonly TimeSpan[] DefaultModelRetryBackoff =
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(90),
    };

    private TimeSpan[] _modelRetryBackoff = DefaultModelRetryBackoff;

    /// <summary>
    /// The longest this service will hold one booked re-attempt open, and therefore the longest it will
    /// promise a reader that audio is coming.
    ///
    /// It exists because of the ONE thing a rate limit can do that a timeout cannot: name its own delay. A
    /// 429 carries the provider's Retry-After, and waiting exactly as long as asked is better than guessing
    /// - but a provider is free to ask for an hour. Booking that would leave "Voice on its way" on a screen
    /// for an hour with the service perfectly content that it was telling the truth, which is the sentence
    /// this whole area of the code exists to stop. Past this ceiling the re-attempt is NOT booked: the turn
    /// is reported as not narrated, and the log says what was asked for and that we declined to wait it out.
    ///
    /// Set just above the ladder's own top rung, so honouring a provider's request is the normal case and
    /// only an unusually long one is refused. The background sweep still comes back to the session later, so
    /// refusing to book is a refusal to PROMISE, never a refusal to try again.
    /// </summary>
    private static readonly TimeSpan DefaultMaxSingleRetryWait = TimeSpan.FromMinutes(2);

    private TimeSpan _maxSingleRetryWait = DefaultMaxSingleRetryWait;

    /// <summary>Test seam: move the ceiling so a test can exercise the refusal - and the hold it arms -
    /// without asking a test to wait two minutes. Same code path, same meaning, smaller number.</summary>
    internal void UseMaxSingleRetryWaitForTest(TimeSpan ceiling) => _maxSingleRetryWait = ceiling;

    /// <summary>Test seam: the delay of the most recently BOOKED re-attempt, or null when none has been
    /// booked. Exposed so a test can pin that a provider's Retry-After was honoured exactly, rather than
    /// inferring it from how long a test slept - a timing assertion that is either flaky or slow.</summary>
    internal TimeSpan? LastBookedRetryDelayForTest { get; private set; }

    /// <summary>Test seam: run the bounded model-leg retry on a schedule a test can wait for. Same code
    /// path, same cap semantics (the number of delays IS the cap) - only the delays differ.</summary>
    internal void UseModelRetryBackoffForTest(params TimeSpan[] delays)
        => _modelRetryBackoff = delays.Length == 0 ? Array.Empty<TimeSpan>() : delays;

    /// <summary>
    /// True when this turn's narration was ABANDONED: the model leg did not answer, the bounded retry budget
    /// is spent, and nothing further is scheduled for it. Fed to <see cref="VoiceDisplayFold"/> so the screen
    /// stops saying the audio is on its way - an absence must not be dressed as progress.
    ///
    /// Distinct from the fold's elapsed-time give-up, which says "nothing has arrived in three minutes" while
    /// work continues. This says the work has STOPPED. Cleared by a new turn, a successful narration, and by
    /// switching voice off - the same three events that clear every other per-turn voice fact.
    /// </summary>
    public bool NarrationAbandonedFor(TenantId tenant, string sid) => StateFor(tenant).NarrationAbandoned.ContainsKey(sid);

    /// <summary>This turn's narration is being attempted again from the start, so neither the spent-retry
    /// ledger nor the abandoned verdict describes it any more. Called wherever a turn's voice facts are
    /// reset. Dropping the ledger entry also RETIRES any re-attempt already booked against it: the delayed
    /// task re-reads the ledger when it wakes and stands down when its own entry is gone.</summary>
    private static void ClearModelRetryState(TenantVoiceState state, string sid)
    {
        lock (state.ModelRetryGate) state.ModelRetries.Remove(sid);
        state.NarrationAbandoned.TryRemove(sid, out _);
        // Everything in flight for the turn just cleared is now speaking about the past. Bumping the epoch
        // is what tells those attempts so - see TenantVoiceState.TurnEpochs.
        state.TurnEpochs.AddOrUpdate(sid, 1, (_, n) => n + 1);
    }

    /// <summary>The turn epoch an attempt must still be inside for anything it says to be about the current
    /// turn. Captured before the model call and checked before any write.</summary>
    private static long CurrentTurnEpoch(TenantVoiceState state, string sid)
        => state.TurnEpochs.TryGetValue(sid, out var e) ? e : 0;

    /// <summary>
    /// Identity of the narration source a retry budget belongs to. Hashed rather than stored whole: the
    /// ledger is held for every session with a turn still owed a narration, and a reply or terminal window
    /// can be large. Trimmed and ordinal, matching <see cref="ShouldRegenerate"/>, so the two agree about
    /// whether the source changed.
    /// </summary>
    private static string ReplyKey(string? sourceIdentity)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes((sourceIdentity ?? string.Empty).Trim())));

    /// <summary>Test seam: seed a standing ACCOUNT / provider condition (the state a failed synthesis
    /// records) so a test can prove a later read failure does not overwrite it.</summary>
    internal void NoteUnavailableForTest(TenantId tenant, string sid, HostedAiState state)
        => StateFor(tenant).Unavailable[sid] = state;

    /// <summary>
    /// True when an exception from the model (translation) leg means it DID NOT ANSWER - a bounded
    /// <see cref="TimeoutException"/> from <see cref="HostedInferenceBrain"/>, or an
    /// <see cref="HttpRequestException"/> transport failure - as opposed to an answered failure (402 and
    /// 5xx surface as InvalidOperationException and are handled elsewhere). A caller-cancel (shutdown) is
    /// excluded: that is not the model's non-answer, it is us stopping. This is the model leg's mirror of
    /// the "absence of evidence -> Retrying" rule the speech leg already applies.
    ///
    /// A 429 is deliberately still excluded, but it is no longer handled somewhere ELSE: it has its own
    /// catch immediately beside this one, and both funnel into the same bounded ladder. They are kept apart
    /// because the two differ in exactly one respect that matters - a 429 can name its own delay - and
    /// collapsing them would throw that number away.
    /// </summary>
    private static bool IsModelDidNotAnswer(Exception ex, CancellationToken ct)
        => ex is not WingmanModelRateLimitedException
           && (ex is TimeoutException
               || ex is HttpRequestException
               || (ex is OperationCanceledException && !ct.IsCancellationRequested));

    public VoiceReady? Get(TenantId tenant, string sid) => StateFor(tenant).Ready.TryGetValue(sid, out var v) ? v : null;
    public byte[]? GetAudio(TenantId tenant, string sid) => StateFor(tenant).Ready.TryGetValue(sid, out var v) ? v.Audio : null;
    public string? GetAudioContentType(TenantId tenant, string sid) => StateFor(tenant).Ready.TryGetValue(sid, out var v) ? v.ContentType : null;

    /// <summary>Mark the session as a voice session (persisted, so the gateway keeps its voice fresh
    /// across restarts via the background sweep + turn-end).</summary>
    public void Mark(TenantId tenant, string sid) { if (StateFor(tenant).VoiceSessions.TryAdd(sid, 1)) SaveVoiceSessions(tenant); }

    /// <summary>
    /// Stop keeping voice for this session - it is no longer a voice session (issue #859). The user
    /// turned voice off, so the gateway must stop spending the per-turn Opus translation + hosted
    /// text-to-speech on it. Removes it from the persisted voice-session set (so the turn-end watcher
    /// and the background sweep skip it - both gate on <see cref="IsVoiceSession"/> /
    /// <see cref="VoiceSessionIds"/>), drops any cached spoken summary + audio (so the list stops
    /// showing it "voice ready" and nothing stale is served), and clears the transient generating
    /// marker. The removal is persisted, so a gateway restart does not bring the session back as a
    /// voice session. Read-only: this changes only gateway-side voice marking; it sends nothing into
    /// the session. Re-entering voice (the explain path calls <see cref="Mark"/>) starts it again.
    /// </summary>
    public void Unmark(TenantId tenant, string sid)
    {
        var state = StateFor(tenant);
        var wasVoice = state.VoiceSessions.TryRemove(sid, out _);
        if (wasVoice) SaveVoiceSessions(tenant);
        state.Generating.TryRemove(sid, out _);
        state.Unavailable.TryRemove(sid, out _);        // voice is off, so its unavailable-state is moot (issue #939)
        state.ReadFailed.TryRemove(sid, out _);         // ...and so is whatever the last transcript read recorded
        state.NothingToNarrate.TryRemove(sid, out _);   // voice is off, so "nothing to narrate" is moot too
        state.DirectorCannotSend.TryRemove(sid, out _); // ...and so is "update that computer to hear this session"
        state.PreferBackupUntil.TryRemove(sid, out _);  // voice is off, so the backup-routing window is moot too (issue devthrottle_internal#405)
        ClearModelRetryState(state, sid);               // ...and nobody is owed a re-attempt for a turn nobody wants narrated (issue #2676)
        if (state.Ready.TryRemove(sid, out _))
            DeleteReadyAudio(tenant, sid);   // keep the durable cache in step so a stale tap can't 404
        if (wasVoice)
            FileLog.Write($"[WingmanVoiceService] voice unmarked (turned off): tenant={tenant.ToLogString()} sid={sid}");
    }

    /// <summary>
    /// A new turn just started on this session, so the cached spoken summary + audio are now stale.
    /// Drop them immediately - the list stops showing it "voice ready", and nothing stale gets
    /// served or played. The session stays a voice session, so when the turn finishes the turn-end
    /// hook regenerates a fresh summary. Called on the Working transition.
    /// </summary>
    public void OnSessionWorking(TenantId tenant, string sid)
    {
        var state = StateFor(tenant);
        // A new turn (blue) supersedes any in-flight generation for the old turn, so drop the
        // yellow "wingman running" marker too - raw activity wins while the agent works.
        state.Generating.TryRemove(sid, out _);
        state.Unavailable.TryRemove(sid, out _);        // a fresh turn clears the old unavailable-state (dismissible, issue #939)
        state.ReadFailed.TryRemove(sid, out _);         // ...and re-opens the read: a new turn is a new transcript to try
        state.NothingToNarrate.TryRemove(sid, out _);   // a fresh turn supersedes "nothing to narrate" - re-evaluated on its turn-end
        // A NEW TURN GETS A NEW RETRY BUDGET (issue #2676). The count and the abandoned verdict are facts
        // about the turn that lost its voice, not about the session: carrying them forward would let one
        // stalled turn spend the next turn's re-attempts, and would leave "not narrated" on a screen whose
        // narration has not even been tried yet.
        ClearModelRetryState(state, sid);
        if (state.Ready.TryRemove(sid, out _))
        {
            DeleteReadyAudio(tenant, sid);   // issue #553: keep the durable cache in step so a stale tap can't 404
            FileLog.Write($"[WingmanVoiceService] voice + text cache cleared (session working): tenant={tenant.ToLogString()} sid={sid}");
        }
    }

    /// <summary>
    /// Store a spoken summary that a caller already produced (the on-demand explain / voice-turn
    /// paths), synthesize its audio, and mark the session as a voice session. Best-effort: if the
    /// audio can't be made (no key / outage) the session is still marked, so turn-end retries.
    /// </summary>
    /// <returns>
    /// What the speech leg did, so a caller that CAN book a re-attempt knows whether to. Every existing
    /// caller ignores it and is unaffected: the state this method records is exactly what it recorded
    /// before, and a caller that does not book leaves the screen saying what it said before.
    /// </returns>
    public async Task<SpeechAttempt> StoreSpokenAsync(
        TenantId tenant,
        string sid,
        string spoken,
        string reply,
        CancellationToken ct = default,
        string? sourceIdentity = null)
    {
        Mark(tenant, sid);
        if (string.IsNullOrWhiteSpace(spoken)) return new SpeechAttempt(false, null, false);
        var tts = await TtsAsync(tenant, sid, spoken, ct);
        // The "if anything fails, remove the triangle" rule: when synthesis returns null/empty we
        // leave this tenant's Ready map WITHOUT this session, so HasVoice stays false and no triangle shows. Only a
        // real, playable summary becomes ready - and is persisted (issue #553) so it survives a restart.
        if (tts.Audio is { Length: > 0 })
        {
            StateFor(tenant).Unavailable.TryRemove(sid, out _);   // success clears any prior unavailable-state (dismissible)
            StoreReady(tenant, sid, spoken, reply ?? "", tts.Audio, tts.ContentType, tts.ServedViaFallback, sourceIdentity);
        }
        else if (tts.Unavailable is HostedAiState unavailable)
        {
            // Issue #939: no longer swallowed. Record WHY voice is unavailable so the /sessions
            // aggregation can show the consistent add-credit / add-key state instead of a silently
            // missing triangle. Left as-is until the next successful turn-end generation clears it.
            StateFor(tenant).Unavailable[sid] = unavailable;
            FileLog.Write($"[WingmanVoiceService] voice unavailable tenant={tenant.ToLogString()} sid={sid}: {unavailable}");
        }
        // WORTH ANOTHER ATTEMPT means transient, and only two of the four speech outcomes are: a refusal
        // that will be lifted, and a call that never answered. An account condition is the member's to fix
        // and an answered 5xx is the service telling us it is failing - neither is improved by calling again
        // in ten seconds, and both already put a specific, actionable sentence on the screen.
        return new SpeechAttempt(
            ProducedAudio: tts.Audio is { Length: > 0 },
            RetryAfter: tts.RetryAfter,
            WorthAnotherAttempt: tts.Failure is SpeechFailure.RateLimited or SpeechFailure.DidNotAnswer);
    }

    /// <summary>Mark a session ready with already-synthesized audio: update the in-memory cache and
    /// persist it to disk so it survives a gateway restart (issue #553). The single place the success
    /// branch lives - the test seam (<see cref="StoreReadyAudioForTest"/>) reuses it so persistence is
    /// exercised without a live provider call.</summary>
    private void StoreReady(TenantId tenant, string sid, string spoken, string reply, byte[] audio, string? contentType, bool servedViaFallback = false, string? sourceIdentity = null)
    {
        var ready = new VoiceReady(spoken, reply, audio, DateTime.UtcNow,
            NormalizeContentType(contentType) ?? DetectAudioContentType(audio), servedViaFallback, sourceIdentity ?? reply);
        var state = StateFor(tenant);
        state.Ready[sid] = ready;
        state.NothingToNarrate.TryRemove(sid, out _);   // audio exists, so there was something to narrate after all
        state.DirectorCannotSend.TryRemove(sid, out _); // ...and a narration proves that computer CAN send its conversation
        ClearModelRetryState(state, sid);               // ...and the audio ARRIVED, so no re-attempt is owed and nothing was abandoned (issue #2676)
        SaveReadyAudio(tenant, sid, ready);
    }

    /// <summary>Test seam: store ready audio exactly as a successful synthesis would (in-memory +
    /// durable), so the persistence round-trip can be tested without calling a provider. The optional
    /// <paramref name="servedViaFallback"/> lets a test store a backup-served clip and assert the notice.</summary>
    internal void StoreReadyAudioForTest(TenantId tenant, string sid, string spoken, string reply, byte[] audio, string? contentType = null, bool servedViaFallback = false)
        => StoreReady(tenant, sid, spoken, reply, audio, contentType, servedViaFallback);

    /// <summary>
    /// Regenerate the voice for a session from its latest turn: read the last reply, translate it,
    /// synthesize audio, store. Called on every turn-end for voice sessions (background, best-effort
    /// - it swallows its own failures so a turn is never blocked on voice).
    ///
    /// Issue #1322: a re-narration must never interrupt a client that is already listening to this
    /// turn's narration. Two guards make it "prepare quietly": it does nothing when the current turn
    /// already has a fresh, playable narration (regenerating it would only mint a new clip and pull
    /// the rug on an active listener), and it shows the yellow "wingman reading" window ONLY for a
    /// brand-new turn (<paramref name="showReadingWindow"/>). A background refresh / catch-up of an
    /// already-ended turn stays quiet, because a phone may be playing this turn from its own local
    /// cache and flipping the session yellow would drop it out of the speaking screen mid-play.
    /// </summary>
    /// <param name="showReadingWindow">True for a genuinely new turn (a live Working -> Waiting
    /// boundary): show the yellow "wingman reading" hold until the summary lands. False for a
    /// background refresh, a startup catch-up, or an idle pre-build sweep: generate silently so a
    /// session a client is already listening to is never flipped yellow.</param>
    /// <param name="onProviderReached">
    /// Invoked at the moment this attempt COMMITS to the shared model and speech legs - after every arm that
    /// returns having produced nothing and spent nothing (no conversation stored, that computer cannot send
    /// one, no text reply to read aloud, this reply is already narrated). Optional; only the background sweep
    /// passes it, to spend its per-cycle budget on attempts that actually cost the serialized brain.
    ///
    /// GUARANTEED SYNCHRONOUS, on the caller's thread, before this method's task is handed back: nothing
    /// before the commit point awaits. That is what lets a caller counting slots in a plain loop see the
    /// answer in time to decide about the NEXT session, and it is a contract on this method - a new await
    /// added ahead of the commit point would break it.
    /// </param>
    /// <param name="markAsVoiceSession">
    /// Make this a voice session as a side effect of narrating it - true for every caller that is acting on
    /// a person's intent (entering voice, a turn ending on a session already in voice mode, the sweep, which
    /// only ever visits sessions already marked).
    ///
    /// FALSE FOR THE MODEL-LEG RETRY, and it is not a nicety. A retry wakes up to a minute and a half after
    /// it was booked, and voice may have been switched off in between. Marking would turn it back ON - the
    /// Gateway resuming a narration for a session whose owner had just stopped one, which is a worse outcome
    /// than the silence the retry exists to prevent. The retry checks <see cref="IsVoiceSession"/> for
    /// itself and does nothing when the answer is no.
    /// </param>
    internal async Task GenerateAsync(TenantId tenant, string sid, SessionVerbClient route, CancellationToken ct = default, bool showReadingWindow = true, Action? onProviderReached = null, bool markAsVoiceSession = true)
    {
        if (markAsVoiceSession) Mark(tenant, sid);

        // The "already narrated" skip is now IDENTITY-AWARE and lives in GenerateOnceAsync, after the
        // current reply has actually been read (issue #1322 done right). The old guard here was a bare
        // "does ANY narration exist" check (HasVoice), which assumed every genuinely new turn cleared
        // the cache first via OnSessionWorking on the Working transition. That assumption breaks: the
        // Working transition is observed on a racy sampled edge and, on a multi-part turn (an interim
        // reply, then a sub-agent, then the real answer), the intermediate Working can fall between two
        // samples that both read "waiting" - so the cache is never cleared and the bare guard wrongly
        // suppressed narration of the final answer forever (the phone replayed the stale interim clip).
        // Comparing the reply TEXT instead removes the dependency on catching that edge: a changed reply
        // always regenerates, the same reply still stays quiet so a client mid-play is never disturbed.
        // The idle sweep still guards on HasVoice at its own call site, so it never reaches here for a
        // cached session; only the turn-end path does, and it pays one cheap /turns read to compare.

        // NO fleet-wide gate. Every session calls the hosted relay on its own and discovers an outage
        // for itself (see the field comment above). There used to be two shared cooldown gates here that
        // SKIPPED every session but one probe while another session's 429 held a cooldown - the coupling
        // that muted the whole fleet on one bad call. Removed 2026-07-17. The only guard left is
        // per-session coalescing, which never makes one session wait on another.
        //
        // Coalesce: never run two generations for the SAME session at once (a slow turn overlapping the
        // idle sweep would otherwise double the spend). First caller wins.
        var state = StateFor(tenant);
        if (!state.InFlight.TryAdd(sid, 1))
            return;
        try
        {
            await GenerateOnceAsync(tenant, sid, route, ct, showReadingWindow, onProviderReached);
        }
        catch (WingmanModelRateLimitedException rl)
        {
            // A BACKSTOP, AND IT SAYS SO. The narration's own rate-limit arm sits inside GenerateOnceAsync,
            // beside the reply the ledger is keyed on, and books a re-attempt there; a 429 reaching this far
            // therefore came from somewhere that arm does not cover, and NOTHING is booked for it.
            //
            // This handler used to be where every 429 landed, and it logged "this session retries on its own
            // next turn-end / idle sweep". Nothing did. That is the same defect the model-leg timeout had
            // (issue #2676) and it is why the wording here now reports an absence instead of inventing a
            // retry: a log that claims work nobody is doing is how a stalled session goes unnoticed.
            //
            // Nothing fleet-wide happens either way - the shared cooldown that used to mute every session on
            // one bad call was removed 2026-07-17.
            state.Unavailable[sid] = HostedAiState.Retrying;
            // Retrying WITHOUT the abandoned fact would put "voice on its way" back on the screen with
            // nothing behind it - the exact defect this change removes, reintroduced through the one door
            // left open (found in review). The guarded form refuses to speak for a turn that already has a
            // re-attempt booked, so an honest backstop cannot contradict a live promise.
            MarkAbandonedIfNothingPending(state, sid);
            FileLog.Write($"[WingmanVoiceService] GenerateAsync sid={sid} rate limited (429) OUTSIDE the narration attempt{(rl.RetryAfter is { } ra ? $" (Retry-After {ra.TotalSeconds:F0}s)" : "")}; no audio this cycle and NOTHING is booked here - the next turn-end or sweep is what comes back");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanVoiceService] GenerateAsync sid={sid} FAILED: {ex.Message}");
        }
        finally { state.InFlight.TryRemove(sid, out _); }
    }

    /// <summary>
    /// One generation attempt: read the latest reply, translate it, synthesize and store the audio.
    /// Split out of <see cref="GenerateAsync"/> so the per-session coalescing wrapper stays readable and
    /// this stays the pure "make the voice" step. A rate-limit surfaces as
    /// <see cref="WingmanModelRateLimitedException"/> for the wrapper to catch and record on this session.
    ///
    /// Returns TRUE only when the model leg actually RAN - i.e. we reached the provider and it answered;
    /// FALSE means there was nothing to do (no reply yet, or this reply is already narrated). The caller
    /// no longer needs the distinction now that the shared rate-limit gate is gone, but it is kept because
    /// it honestly reports whether the provider was reached.
    /// </summary>
    private async Task<bool> GenerateOnceAsync(TenantId tenant, string sid, SessionVerbClient route, CancellationToken ct, bool showReadingWindow, Action? onProviderReached = null)
    {
        var state = StateFor(tenant);

        // THE CONVERSATION COMES FROM THE GATEWAY'S OWN STORE (turn-push mission, phase 3), not from a
        // command sent down the tunnel asking the owning Director to re-read the user's transcript.
        //
        // What that removes is an entire class of failure that this method used to have to reason about.
        // A tunnel read answered null when the Director was not connected, and otherwise carried a status -
        // "no_jsonl", "no_transcript", "parse_error", "unsupported" - each arriving as a SUCCESSFUL command
        // with an empty widget list, because the transport worked even though the read did not. Telling
        // those apart from "this session is waiting on a prompt" was issue #2561, and getting it wrong is
        // what left a session silent for forty-eight minutes with nothing raised anywhere. It is also what
        // left session 111 silent for hours on 1 September: the Director was looking for a transcript that
        // had moved, and answered no_jsonl to every attempt.
        //
        // None of that can happen here. A read of the store answers one of exactly two things: rows, or
        // nothing stored yet. There is no transport to fail, no file to be missing, no parse to go wrong.
        // The narration path therefore has ONE remaining reason to retry - the model or the speech service
        // did not answer - which is the whole promise of this mission.
        //
        // NOTHING STORED IS NOT A FAILURE AND NOT AN ATTEMPT. The words have not reached the Gateway yet;
        // the Director will push them, and the Chat screen already says so in its own words. Recording a
        // failure here would burn this turn's retry budget on a wait that has nothing to do with voice.
        var stored = _conversationReader?.Invoke(tenant, sid);
        if (stored is null)
        {
            // Clear any standing "nothing to read aloud" first. That sentence means the session is parked on
            // a prompt with no reply to narrate; it does NOT mean "we do not have the words yet". Leaving it
            // up while the push is in flight tells the reader the conversation is empty when it is merely
            // late - a small version of the exact confusion this mission exists to remove (found in review).
            // Nothing here stands the sweep down: ShouldSkipSweep keys on a terminal read verdict only.
            SetNothingToNarrate(tenant, sid, false);

            // BUT NOT EVERY EMPTY STORE IS A WAIT, and the sentence above is a promise this arm cannot keep
            // for the one Director that will never push. "The Director pushes it and the sweep comes back"
            // assumes a Director that HAS the pusher; a build older than the turn-push mission does not, so
            // the store stays empty for as long as that computer keeps running, and every calm "on its way"
            // said about it is false.
            //
            // WHAT THAT COST, on 2026-09-02, is why this branch exists. Phases 3a/3b went live on the hosted
            // Gateway while the newest RELEASED Director (v2.0.4, 16 August) predated the pusher by a
            // fortnight. Every voice session on every account fell into the arm above: 297 "no conversation
            // stored yet" lines in forty minutes, ZERO narrations, and - because the colour fold holds yellow
            // until voice arrives - eleven of the owner's twenty-one sessions showed "Voice did not arrive
            // after 3m..22m" instead of the red "Needs you" they had actually earned. He could not see which
            // sessions wanted him. The Gateway KNEW the cause the whole time: SessionConversationFold was
            // logging director-too-old for the same sessions, and the Chat screen was saying so in plain
            // English. The fact simply never reached the voice path.
            //
            // NOT RECORDED AS A READ FAILURE, deliberately. NoteReadFailed(Unavailable) would render through
            // the hosted-AI arm of VoiceDisplayFold and say "Voice unavailable" - a symptom, in the shared
            // account-condition voice, for something that is neither the account's fault nor about hosted AI.
            // The fold gets its own arm and its own sentence instead, which is the one the reader can act on.
            //
            // NOR DOES IT STAND THE SWEEP DOWN. Re-checking costs a dictionary lookup and a store read that
            // returns null before any model or speech call, so there is no spend to protect - and paying that
            // nothing buys the recovery for free: the moment that computer is updated, its next Hello flips
            // the registry, the marker clears on the following pass, and narration resumes with nobody
            // touching the Gateway.
            if (_directorCannotSendConversation?.Invoke(tenant, sid) == true)
            {
                state.DirectorCannotSend[sid] = 1;
                FileLog.Write($"[WingmanVoiceService] sid={sid}: the computer that owns this session is connected but runs a build that cannot send its conversation, so nothing will ever be stored to narrate - the screen says to update it instead of counting a wait that would not end");
                return false;
            }
            state.DirectorCannotSend.TryRemove(sid, out _);
            FileLog.Write($"[WingmanVoiceService] no conversation stored yet for sid={sid} - nothing to narrate from, and not counted as an attempt; the Director pushes it and the sweep comes back");
            return false;
        }
        // A conversation ARRIVED, so that computer plainly can send one - whatever it said about itself, and
        // whoever owns the session now. Cleared here rather than only where it is set, for the same reason
        // ClearReadFailed sits on this path: a marker that outlives the condition it describes is the next
        // stale sentence, and ownership of a session can move to a different Director mid-life.
        state.DirectorCannotSend.TryRemove(sid, out _);
        // THE ONE TERMINAL ANSWER LEFT. This agent keeps no readable conversation at all, so no amount of
        // waiting or retrying produces words to narrate. It is recorded as the terminal verdict so the voice
        // screen says "Voice unavailable" instead of an endless quiet wait, and so the sweep stops spending
        // its small per-cycle budget on a session that can never answer. Losing the tunnel read all but lost
        // this - see StoredConversation for why the flag is carried.
        if (!stored.Value.IsSupported)
        {
            NoteReadFailed(tenant, sid, HostedAiState.Unavailable);
            FileLog.Write($"[WingmanVoiceService] sid={sid}: this agent keeps no conversation that can be read - TERMINAL, so the screen says so and the sweep stands down until the verdict is revalidated");
            return false;
        }
        var widgets = stored.Value.Widgets;
        // The read answered, so whatever the last failed read recorded is over. Cleared HERE, before the
        // reply check, because VoiceDisplayFold consults the unavailable state BEFORE nothingToNarrate - a
        // stale read failure would mask the honest "nothing to read aloud" verdict on the very next pass.
        // This clears ONLY the read's own fact: a model timeout, a rate limit, and a standing NeedsCredits /
        // CapReached / NeedsKey are all things a successful transcript read has no evidence about, and each
        // still clears where it was set (StoreSpokenAsync on a successful synthesis, OnSessionWorking on a
        // new turn). An earlier version of this line cleared any Retrying in the shared Unavailable map and
        // therefore erased model-leg and speech-leg states it had not established.
        ClearReadFailed(tenant, sid);
        // A later user message makes an older agent reply stale. On turn-end and direct generation paths,
        // read the live screen and use a positively classified terminal failure when the missing reply died
        // there. The idle sweep passes the synchronous budget callback; it deliberately does not perform
        // this pre-commit tunnel read, preserving that callback's synchronous contract. The on-demand
        // explain path performs the same selection independently, so an already-ended session is covered.
        ScreenGridResponse? screenGrid = null;
        if (WingmanNarrationSource.NeedsLiveScreen(widgets) && onProviderReached is null)
        {
            try { screenGrid = await route.GetScreenGridAsync(sid, ct); }
            catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] terminal screen read failed for sid={sid}: {ex.Message}"); }
        }
        var source = WingmanNarrationSource.Select(
            widgets,
            screenGrid is { HasGrid: true } ? screenGrid.Rows : null);
        if (source is null)
        {
            // The stored conversation has no current agent reply and the live screen supplied no positive
            // failure evidence. This is an ordinary wait on a prompt or menu, not an error to invent.
            state.NothingToNarrate[sid] = 1;
            // ...and it SUPERSEDES an abandoned narration, so the two can never be in the reader's way at
            // once. This read succeeded and found no reply, which is fresher evidence than a model timeout
            // recorded against a reply that is no longer the last one. Cleared here rather than relying on
            // the fold's ordering, because a defence that lives in another file's precedence list stops
            // working the day somebody reorders it for a good reason (found in review).
            ClearModelRetryState(state, sid);
            return false;  // nothing to say yet - the provider was not called
        }
        state.NothingToNarrate.TryRemove(sid, out _);   // there is a current source now
        // Identity-aware skip (issue #1322 done right): only skip when the CURRENT last reply is the
        // exact one already narrated. Unlike the old bare HasVoice guard this does not depend on having
        // observed the Working transition, so a genuinely new/changed reply is never suppressed by a
        // stale cache the missed edge left behind - while the same reply stays quiet (no re-mint, no
        // yellow flip), preserving the "never disturb a listener mid-play" guarantee.
        if (!ShouldRegenerate(tenant, sid, source.Identity))
        {
            FileLog.Write($"[WingmanVoiceService] GenerateOnce skip (same source already narrated): sid={sid}");
            return false;   // nothing to do - the provider was not called, so we know nothing new about it
        }
        // THE PROVIDER'S OWN DEADLINE, HONOURED. When a 429 named a wait longer than this service will hold
        // a promise across, the re-attempt was refused rather than booked - and refusing to promise does not
        // entitle us to keep calling. Without this the idle sweep would come past every 45 seconds and call
        // the model again long before the provider's deadline, which is precisely what Retry-After exists to
        // prevent and how a rate limit becomes a storm (found in review).
        //
        // Checked HERE, after the reply is known, because the hold belongs to a TURN: it is keyed on the
        // same reply as the retry ledger, so a new turn is never held back by the previous turn's refusal.
        // It reaches no other session - the fleet-wide cooldowns this file used to have were removed in July
        // because one session's 429 muted every other session, and nothing here can do that.
        if (ModelCallHeldOffFor(state, sid, ReplyKey(source.Identity)) is { } heldOff)
        {
            FileLog.Write($"[WingmanVoiceService] sid={sid}: not calling the model for {heldOff.TotalSeconds:F0}s more - the provider asked us to wait that long and this turn has no re-attempt booked; the screen already reports it as not narrated");
            return false;   // nothing produced, and deliberately nothing attempted
        }

        // THE COMMIT POINT (issue #2675). Everything above this line is a no-op arm: it reads what is already
        // in memory or in the Gateway's own store, decides there is nothing to make, and returns having spent
        // nothing on the model or the speech provider. From here on the attempt costs the shared, serialized
        // resources - so this, and not the dispatch of the attempt, is where a caller rationing them should
        // count a slot. Announced BEFORE the first await, so a caller counting in a loop has the answer in
        // time (see the parameter's contract on GenerateAsync).
        onProviderReached?.Invoke();
        // Recent conversation is useful only when translating an agent reply. A terminal failure has its own
        // dedicated, untrusted-evidence contract and must not be presented as part of an answer.
        var recentContext = source.Kind == WingmanNarrationSourceKind.AgentReply
            ? WingmanTranslator.BuildRecentContext(widgets)
            : "";
        // The LIVE screen goes into the same call (issue devthrottle_internal#1195): the model that writes
        // the summary also judges what the screen needs - menu, an answer, or nothing - because the pure
        // regex classifier convicted a finished summary of being a menu (session 115). One tunnel read per
        // narration, exactly the read the old post-translate menu check made anyway. A failed read just
        // means no verdict this turn: the narration itself never depends on it.
        if (source.Kind == WingmanNarrationSourceKind.AgentReply)
        {
            try { screenGrid = await route.GetScreenGridAsync(sid, ct); }
            catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] screen-grid read failed for sid={sid}: {ex.Message} - narrating without a screen verdict"); }
        }
        var liveScreen = source.Kind == WingmanNarrationSourceKind.AgentReply
                         && screenGrid is { HasGrid: true, Rows.Count: > 0 }
            ? string.Join("\n", screenGrid.Rows)
            : null;
        // The wingman is now running for this session - show it yellow until the summary lands, but
        // only for a brand-new turn. A background refresh / catch-up stays quiet so a session a phone
        // may be listening to is never flipped yellow mid-play (issue #1322).
        if (showReadingWindow) BeginGenerating(tenant, sid);
        // CAPTURED BEFORE THE CALL, checked before anything is written about its outcome. A narration takes
        // as long as the model takes, and a turn can be superseded in that window.
        var turnEpoch = CurrentTurnEpoch(state, sid);
        try
        {
            WingmanTranslation t;
            try
            {
                t = source.Kind == WingmanNarrationSourceKind.TerminalFailure
                    ? await _translator.TranslateTerminalFailureAsync(
                        tenant, source.Content, _sessionTitleResolver?.Invoke(tenant, sid), ct)
                    : await _translator.TranslateAsync(
                        tenant, recentContext, source.Content, _sessionTitleResolver?.Invoke(tenant, sid), liveScreen, ct);
            }
            catch (Exception ex) when (IsModelDidNotAnswer(ex, ct))
            {
                // The MODEL leg (the translation) did not answer - a bounded timeout or a transport
                // failure - so we have no evidence about the service. This is the exact story the speech
                // leg already tells (see TtsAsync): a non-answer is Retrying, NOT a silent FAILED and NOT
                // ServiceDown. Recording Retrying makes the phone show the calm "voice on its way" state
                // and the voice sweep retries on its own, instead of the session sitting red with no audio
                // and no reason (the wedge the owner hit: half the fleet stuck, "generate" doing nothing).
                // AND THE RETRY IS BOOKED HERE, BY THIS LEG (issue #2676). Recording Retrying and returning
                // is what this arm used to do, and its log line claimed "the session retries on its own" -
                // which was true of nothing. Nothing on this path scheduled another attempt; the only thing
                // that would come back was the shared voice sweep, whose whole per-cycle budget another
                // account's unnarratable sessions were consuming (issue #2675). The leg that failed now owns
                // the re-attempt, so the promise on the screen is backed by a booked piece of work rather
                // than by a loop nobody here controls.
                NoteNarrationAttemptFailed(tenant, state, sid, route, source.Identity, ex.Message, retryAfter: null,
                    cause: "model did not answer", epoch: turnEpoch);
                return false;   // nothing produced; the provider was not usefully reached
            }
            catch (WingmanModelRateLimitedException rl)
            {
                // THE PROVIDER ANSWERED "NOT NOW". Caught HERE, beside the non-answer, because the two need
                // the same thing from us and used to get opposite treatment: this one propagated out to
                // GenerateAsync, which recorded the same calm Retrying state and logged "this session
                // retries on its own next turn-end / idle sweep" - a sentence with nothing behind it, for
                // exactly the reason the timeout's version of it had nothing behind it. One shared ladder
                // now answers both, so a turn gets three re-attempts in total however it failed, rather
                // than a fresh three each time the failure changes shape.
                //
                // The one thing a rate limit knows that a timeout does not is HOW LONG to wait, so the
                // provider's Retry-After is carried in and honoured - up to the point where honouring it
                // would mean promising an arrival nobody can see the end of. See DefaultMaxSingleRetryWait.
                //
                // It is caught here rather than in the wrapper for a second reason: the ledger is keyed on
                // the reply being narrated, and this is the innermost place that reply is known.
                NoteNarrationAttemptFailed(tenant, state, sid, route, source.Identity, rl.Message, rl.RetryAfter,
                    cause: $"model rate limited (429){(rl.RetryAfter is { } ra ? $", provider asked for {ra.TotalSeconds:F0}s" : ", no Retry-After sent")}",
                    epoch: turnEpoch);
                return false;   // nothing produced; the provider refused this call
            }
            // Announce a waiting menu AS THE TURN IS READ (issue #2193). A turn that ends on a picker is the
            // one case where the narration alone is misleading: the agent's words are read out, the person
            // answers by voice, and the answer goes nowhere - because voice cannot pick an option yet.
            // The verdict is the MODEL's, from the same call that wrote the summary (issue
            // devthrottle_internal#1195) - the pure classifier declared a finished summary to be a menu
            // (session 115), so it no longer announces on its own. The verdict is cached against the screen
            // fingerprint it judged, which is what lets the send-time guards answer an unchanged screen
            // without a second model call. No verdict (no readable screen, or a garbled SCREEN line) means
            // no announcement - the fail-safe direction for a spoken claim.
            var spoken = t.Spoken;
            if (t.Screen is not null && screenGrid?.Rows is { Count: > 0 } judgedRows)
                WingmanScreenVerdictCache.Store($"{tenant}/{sid}", WingmanScreenVerdictCache.HashRows(judgedRows), t.Screen.Needs);
            if (t.Screen?.Needs == "menu")
            {
                spoken += Speech.SpokenPhrases.WaitingScreenMenuNarrationSuffix.In(_tenantSettings.SpokenLanguage(tenant));
                FileLog.Write($"[WingmanVoiceService] narration announces a waiting menu (model verdict): sid={sid}");
            }
            var speech = await StoreSpokenAsync(
                tenant, sid, spoken, source.Content, ct, sourceIdentity: source.Identity);
            // Log the TRUE outcome: StoreSpokenAsync only makes the session playable when the
            // text-to-speech synthesis actually returned audio. Logging "voice ready"
            // unconditionally (the old behavior) hid every failed synthesis behind a success
            // line, which made a text-to-speech outage look like it was working in the log.
            if (HasVoice(tenant, sid))
                FileLog.Write($"[WingmanVoiceService] voice ready: sid={sid}, spokenLen={spoken.Length}");
            else
                FileLog.Write($"[WingmanVoiceService] voice NOT ready (text-to-speech produced no audio): sid={sid}, spokenLen={spoken.Length}");
            // THE SPEECH LEG GETS THE SAME LADDER AS THE MODEL LEG, and for the same reason: it was the
            // third place in this file that said "this session retries on its own" while scheduling
            // nothing. Its non-answer arm is the one that mattered most - that arm records Retrying, so
            // unlike the two answered arms beside it, its SCREEN said "voice on its way" too.
            //
            // ONE ladder for the turn, shared with the model leg. A turn that loses its narration text to
            // a stalled model and then its audio to a stalled speech provider has failed twice, not
            // started again, and giving each leg its own three re-attempts would spend six on the turn
            // already costing the most.
            //
            // The turn has WORDS at this point - the model answered - so a re-attempt here is cheap in the
            // way that matters: it re-runs the narration from the same reply, and the identity-aware skip
            // means a turn that has since been narrated is left alone.
            if (!speech.ProducedAudio)
            {
                if (speech.WorthAnotherAttempt)
                    NoteNarrationAttemptFailed(tenant, state, sid, route, source.Identity,
                        "the speech provider produced no audio", speech.RetryAfter,
                        cause: $"speech leg failed{(speech.RetryAfter is { } sra ? $", provider asked for {sra.TotalSeconds:F0}s" : "")}",
                        epoch: turnEpoch);
                else if (CurrentTurnEpoch(state, sid) == turnEpoch)
                    // NO AUDIO AND NOTHING BOOKED IS THE ABANDONED CASE, whatever produced it - and this
                    // arm is the one that catches the outcomes nobody enumerated. Found in review: the
                    // speech leg can return no audio and record NO state at all (a 200 carrying an empty
                    // body, or an empty spoken text), and if an earlier attempt had left Retrying standing
                    // then the screen went back to promising audio with nothing behind it - the very defect
                    // this change exists to remove, arriving through the one door the classification did
                    // not name.
                    //
                    // Guarded twice over, and by the two different questions that matter. The EPOCH check
                    // above is what proves this attempt is still speaking about the current turn - which is
                    // why the ledger-free overload is the right one here: with the turn known to be current,
                    // an absent ledger entry means no failure has ever been recorded for it, not that the
                    // turn moved on, and "nothing is pending" is then simply true. The reply-keyed overload
                    // reads that same absence as "somebody else owns this now" and stays silent, which on
                    // this path would leave the stale promise exactly where it was.
                    //
                    // Harmless where a more specific verdict already applies: an account condition or an
                    // answered ServiceDown is ranked ahead of this fact by VoiceDisplayFold, so the reader
                    // still gets the sentence they can act on.
                    MarkAbandonedIfNothingPending(state, sid);
            }
            // The model leg ran and answered - TranslateAsync returned rather than throwing
            // WingmanModelRateLimitedException. Reported as true purely so the return honestly says the
            // provider was reached (no caller acts on it now that the shared gate is gone).
            return true;
        }
        finally { if (showReadingWindow) EndGenerating(tenant, sid); }
    }

    /// <summary>
    /// The model leg did not answer for this session's current turn. Three outcomes, decided in one place
    /// because they are one decision (issue #2676): book the next re-attempt, stand aside for one already
    /// booked, or - when the ladder in <see cref="DefaultModelRetryBackoff"/> is spent - stop promising one.
    ///
    /// The defect this fixes was a codebase that had the optimistic outcome and neither of the others: the
    /// screen kept saying "retrying automatically. It should come through shortly" about a turn no loop was
    /// going to touch again. Writing only the pessimistic half would have been the same mistake mirrored -
    /// hence the middle case, which is the one that keeps "nothing further is scheduled" true.
    /// </summary>
    /// <param name="sourceIdentity">The source this attempt was trying to narrate. It IDENTIFIES the budget - see
    /// <see cref="TenantVoiceState.ModelRetryLedger"/> for why a turn cannot be identified by a transition.</param>
    /// <param name="retryAfter">
    /// How long the provider ASKED us to wait, from a 429's Retry-After, or null for a failure that named no
    /// delay (a timeout, a transport failure, or a 429 that sent no header).
    ///
    /// When it is present the booked delay is the LONGER of it and this rung's own backoff. The longer of
    /// the two rather than the provider's number outright: a provider that answers "retry in one second" is
    /// describing its own window, not licensing us to re-enter a ladder we back off on purpose, and calling
    /// again a second after a 429 is how a rate limit becomes a storm.
    /// </param>
    /// <param name="cause">Plain words for the log, so a reader can tell a silence from a refusal.</param>
    /// <param name="epoch">
    /// The turn epoch this attempt started in. If the turn has been superseded since - a new turn, a
    /// successful narration, voice switched off - this method writes NOTHING, because every sentence it
    /// could write would be about a turn that is over.
    ///
    /// WHAT THAT DOES NOT COVER, stated because an earlier version of this comment said a superseded
    /// attempt "says nothing" full stop, and that was an overclaim (found in review). The guard binds THIS
    /// method. A slow attempt still passes through <see cref="StoreSpokenAsync"/> first, which records its
    /// own state and can store audio, and those writes happen before anything here is reached. So a
    /// narration that was superseded while the speech provider was working can still leave the previous
    /// turn's clip or its recorded state behind. That window predates this ladder and is bounded by the
    /// identity-aware regeneration check, which sees a cached reply that is not the current one and
    /// narrates again; closing it properly means making the store itself epoch-aware, which is a change to
    /// a method three other callers share and is not attempted here.
    /// </param>
    private void NoteNarrationAttemptFailed(TenantId tenant, TenantVoiceState state, string sid, SessionVerbClient route, string sourceIdentity, string detail, TimeSpan? retryAfter, string cause, long epoch)
    {
        if (CurrentTurnEpoch(state, sid) != epoch)
        {
            FileLog.Write($"[WingmanVoiceService] {cause} for sid={sid}: {detail} - the turn it was narrating has been superseded, so nothing is recorded and nothing is booked");
            return;
        }
        var schedule = _modelRetryBackoff;
        var replyKey = ReplyKey(sourceIdentity);
        bool standAside, abandon;
        int rung;
        long reservation;
        lock (state.ModelRetryGate)
        {
            // A ledger for a DIFFERENT reply is a different turn's budget and says nothing about this one,
            // so it is replaced rather than continued. This is the reset that does not depend on anybody
            // having observed a Working transition.
            if (!state.ModelRetries.TryGetValue(sid, out var ledger)
                || !string.Equals(ledger.ReplyKey, replyKey, StringComparison.Ordinal))
                ledger = new TenantVoiceState.ModelRetryLedger(replyKey, 0, 0, 0);

            standAside = ledger.Pending > 0;
            abandon = !standAside && ledger.Booked >= schedule.Length;
            rung = standAside || abandon ? ledger.Booked : ledger.Booked + 1;
            reservation = standAside || abandon ? ledger.Reservation : state.NextReservation++;
            state.ModelRetries[sid] = standAside || abandon
                ? ledger
                : ledger with { Booked = rung, Pending = 1, Reservation = reservation };
        }

        // Retrying is the true CAUSE in all three cases - the model leg failed, and nothing here is evidence
        // of an outage or of an account problem. Only the PROMISE differs, and that is carried by the
        // abandoned fact beside it, which VoiceDisplayFold reads as a pair.
        state.Unavailable[sid] = HostedAiState.Retrying;

        if (standAside)
        {
            // A concurrent attempt failed while a booked re-attempt is still waiting out its backoff - the
            // sweep, or a person pressing Generate. It must not spend a rung of a ladder it does not own,
            // and above all it must not declare the turn abandoned while that task exists.
            state.NarrationAbandoned.TryRemove(sid, out _);
            FileLog.Write($"[WingmanVoiceService] {cause} for sid={sid}: {detail} - Retrying; re-attempt {rung} of {schedule.Length} is already booked, so this attempt spends nothing");
            return;
        }
        if (!abandon)
        {
            // The rung's own backoff, or the provider's Retry-After when it asked for longer.
            var delay = schedule[rung - 1];
            if (retryAfter is { } asked && asked > delay) delay = asked;

            // A DELAY WE WILL NOT WAIT OUT IS NOT A RETRY. Booking it would put "Voice on its way" on the
            // screen for as long as the provider felt like naming, and a promise nobody can see the end of
            // is the thing this code exists to stop. So the ladder stops here instead, honestly.
            if (delay > _maxSingleRetryWait)
            {
                ReleaseRefusedReservation(state, sid, replyKey, reservation, rung,
                    notBefore: DateTime.UtcNow + (retryAfter ?? delay));
                MarkAbandonedIfNothingPending(state, sid, replyKey);
                FileLog.Write($"[WingmanVoiceService] {cause} for sid={sid}: {detail} - the provider asked us to wait {delay.TotalSeconds:F0}s, longer than the {_maxSingleRetryWait.TotalSeconds:F0}s this turn will hold a promise open, so NOTHING IS BOOKED, the model is not called again for this turn until the provider's own deadline passes, and the screen reports a turn that was not narrated rather than an arrival with no end in sight");
                return;
            }

            state.NarrationAbandoned.TryRemove(sid, out _);
            LastBookedRetryDelayForTest = delay;
            ScheduleModelRetry(tenant, sid, route, delay, replyKey, reservation, rung, schedule.Length);
            FileLog.Write($"[WingmanVoiceService] {cause} for sid={sid}: {detail} - Retrying (audio on its way); re-attempt {rung} of {schedule.Length} is booked for {delay.TotalSeconds:F0}s from now{(retryAfter is { } ra ? $" (provider asked for {ra.TotalSeconds:F0}s)" : "")}");
            return;
        }
        // THE LADDER IS SPENT. Recorded as its own fact rather than as a new HostedAiState because it is not
        // a condition of the hosted service - the service's answer is unchanged, it is OUR attempts that have
        // run out. Published through the same guarded helper as every other abandonment, so it cannot be
        // stamped onto a turn that has moved on while this attempt was failing.
        MarkAbandonedIfNothingPending(state, sid, replyKey);
        FileLog.Write($"[WingmanVoiceService] {cause} for sid={sid}: {detail} - all {schedule.Length} re-attempt(s) spent and none pending, so THIS TURN WAS NOT NARRATED and nothing further is scheduled; the screen says so instead of promising audio");
    }

    /// <summary>
    /// Give back a reservation the ceiling refused to book, and record how long the provider asked us to
    /// wait before calling it again for this turn.
    ///
    /// THE RUNG GOES BACK because the refusal is about this MOMENT, not about the turn's budget: the
    /// provider named a wait we will not hold a promise across, which says nothing about whether a later
    /// attempt deserves a re-attempt. And <paramref name="notBefore"/> is what stops that generosity turning
    /// into a stream of calls the provider explicitly asked us not to make - without it the sweep would come
    /// past every 45 seconds, take the rung, be refused, and put it back, calling the model each time (found
    /// in review).
    ///
    /// Ownership is proven by the RESERVATION, not by the reply key. A ledger cleared and re-created for the
    /// same reply text is a different reservation, and rewinding it would silently un-book somebody else's
    /// live re-attempt.
    /// </summary>
    private static void ReleaseRefusedReservation(TenantVoiceState state, string sid, string replyKey, long reservation, int rung, DateTime notBefore)
    {
        lock (state.ModelRetryGate)
        {
            if (!state.ModelRetries.TryGetValue(sid, out var held)
                || held.Reservation != reservation
                || !string.Equals(held.ReplyKey, replyKey, StringComparison.Ordinal))
                return;   // somebody else owns this entry now - leave it entirely alone
            state.ModelRetries[sid] = held with
            {
                Booked = rung - 1,
                Pending = 0,
                NotBefore = notBefore > held.NotBefore ? notBefore : held.NotBefore,
            };
        }
    }

    /// <summary>
    /// Record that this turn's narration was abandoned - BUT ONLY IF the ledger still belongs to this turn
    /// and nothing is booked against it.
    ///
    /// The guard is the invariant this whole area exists to protect, and writing the fact unguarded broke it
    /// in the one direction nobody would notice (found in review). An attempt that fails slowly can finish
    /// after a NEW turn has replaced the ledger and booked its own re-attempt; stamping "not narrated" then
    /// puts the terminal sentence on a turn that is actively being worked on. Absent or mismatched ledger
    /// means the turn moved on and this attempt has no standing to say anything about it.
    /// </summary>
    private static void MarkAbandonedIfNothingPending(TenantVoiceState state, string sid, string replyKey)
    {
        lock (state.ModelRetryGate)
        {
            if (!state.ModelRetries.TryGetValue(sid, out var held)
                || !string.Equals(held.ReplyKey, replyKey, StringComparison.Ordinal)
                || held.Pending > 0)
                return;
            state.NarrationAbandoned[sid] = 1;
        }
    }

    /// <summary>
    /// The variant for a caller that does not know which reply it failed on - the wrapper's rate-limit
    /// backstop. It refuses to speak for any turn with work booked against it, which is the same guarantee
    /// stated with less information.
    /// </summary>
    private static void MarkAbandonedIfNothingPending(TenantVoiceState state, string sid)
    {
        lock (state.ModelRetryGate)
        {
            if (state.ModelRetries.TryGetValue(sid, out var held) && held.Pending > 0) return;
            state.NarrationAbandoned[sid] = 1;
        }
    }

    /// <summary>
    /// How long this turn must wait before the model may be called for it again, or null when there is no
    /// such restriction. Non-null only after a provider asked for a delay this service would not hold a
    /// promise across - see <see cref="TenantVoiceState.ModelRetryLedger.NotBefore"/>.
    /// </summary>
    private static TimeSpan? ModelCallHeldOffFor(TenantVoiceState state, string sid, string replyKey)
    {
        lock (state.ModelRetryGate)
        {
            if (!state.ModelRetries.TryGetValue(sid, out var held)
                || !string.Equals(held.ReplyKey, replyKey, StringComparison.Ordinal))
                return null;
            var left = held.NotBefore - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : null;
        }
    }

    /// <summary>
    /// Book one delayed re-attempt at this session's narration, owned by the voice path (issue #2676).
    ///
    /// Fire-and-forget on purpose, and it must be: the caller is inside <see cref="GenerateAsync"/>'s
    /// per-session coalescing marker, so awaiting the delay here would hold that marker for the whole backoff
    /// and block the one thing that must never be blocked - a genuinely NEW turn arriving and narrating
    /// itself. Returning immediately releases the marker, and the delayed call takes it again on its own.
    ///
    /// IT CAN ARRIVE INTO A WORLD THAT HAS MOVED ON, so it re-reads the ledger before doing anything. Four
    /// things retire it, and each was a way for a retry to do harm rather than nothing:
    ///  - its ledger entry is GONE or now belongs to another reply: a new turn, a successful narration or
    ///    voice-off has superseded it. Without this a re-attempt booked for the previous turn could win the
    ///    coalescing race against the new turn's own narration and then store the OLD turn's clip over it,
    ///    where the sweep would never look again because the session now has audio.
    ///  - the session is no longer a voice session.
    ///  - it already has audio - a new turn narrated it while this waited, and re-minting would pull the rug
    ///    on a listener.
    /// It also never MARKS the session as a voice session (unlike every other caller of GenerateAsync): a
    /// retry must be able to do nothing, and a retry that turned voice back on for a session somebody had
    /// just switched off would be the worst kind of nothing.
    ///
    /// It re-enters <see cref="GenerateAsync"/> rather than the inner attempt, so every guard on the ordinary
    /// path still applies: coalescing, the identity-aware "already narrated" skip, and the reply read itself.
    ///
    /// THAT RE-RUNS THE MODEL EVEN WHEN ONLY THE SPEECH LEG FAILED, and it is a deliberate trade rather than
    /// an oversight (raised in review). Resuming at the speech step would save a model call, and would do it
    /// by speaking text that was written for a reply this retry has not re-read - so a turn that moved on in
    /// the meantime would be narrated with the OLD turn's words, which is the failure mode this file has
    /// spent three issues removing. Re-reading is what makes a re-attempt narrate the CURRENT turn. The cost
    /// is one extra model call on a speech-only failure, bounded by the same three-rung ladder.
    /// </summary>
    private void ScheduleModelRetry(TenantId tenant, string sid, SessionVerbClient route, TimeSpan delay, string replyKey, long reservation, int attempt, int cap)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                var state = StateFor(tenant);
                // Release the pending marker as this attempt STARTS, and only if the ledger is still waiting
                // for THIS re-attempt. Releasing it here rather than at the end is what lets this attempt's
                // own failure book the next rung; leaving it set would make the attempt stand aside from
                // itself and the ladder would never advance.
                // Ownership is proven by the RESERVATION this task was booked under, not by the reply key.
                // The key alone matches a ledger that was cleared and re-created for the same reply text, so
                // an old timer could take the pending marker off a NEWLY booked re-attempt and leave the
                // screen promising audio behind a task that would then stand down (found in review).
                bool stillOurs;
                lock (state.ModelRetryGate)
                {
                    stillOurs = state.ModelRetries.TryGetValue(sid, out var ledger)
                                && ledger.Reservation == reservation
                                && string.Equals(ledger.ReplyKey, replyKey, StringComparison.Ordinal)
                                && ledger.Pending > 0;
                    if (stillOurs) state.ModelRetries[sid] = state.ModelRetries[sid] with { Pending = 0 };
                }
                if (!stillOurs)
                {
                    FileLog.Write($"[WingmanVoiceService] model retry {attempt} of {cap} for sid={sid} stood down - the turn it was booked for has been superseded");
                    return;
                }
                if (!IsVoiceSession(tenant, sid)) return;   // voice was switched off while this waited
                if (HasVoice(tenant, sid)) return;          // a new turn already narrated it
                FileLog.Write($"[WingmanVoiceService] model retry {attempt} of {cap} starting for sid={sid} (the voice path's own re-attempt, not the sweep)");
                await GenerateAsync(tenant, sid, route, CancellationToken.None, showReadingWindow: false,
                    markAsVoiceSession: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A boundary: this runs on the thread pool with nobody to catch for it, and a narration
                // retry must never be able to take the Gateway down.
                FileLog.Write($"[WingmanVoiceService] model retry {attempt} of {cap} FAILED for sid={sid}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// How long a single observed primary hang keeps routing THIS session to the backup voice provider
    /// (issue devthrottle_internal#405). The cloud proxy's failover only reacts to an ERROR the primary returns; a silent
    /// hang gives it nothing to react to. The Gateway, which owns the only deadline on the speech path,
    /// is the layer that actually sees the hang - so on a hang it arms this window and, while it lasts,
    /// asks the proxy to skip the stalling primary and serve from the backup. Time-based with NO active
    /// probing: once the window passes we call the primary again, and if it is still silent the next
    /// hang re-arms it. Long enough to ride out a provider blip without re-eating the full per-attempt
    /// deadline on every turn; short enough to return to the cheaper primary promptly once it recovers.
    /// </summary>
    private static readonly TimeSpan PreferBackupWindow = TimeSpan.FromMinutes(3);

    /// <summary>True while this session should route past a silent primary straight to the backup
    /// (the window armed by a recent hang has not yet expired).</summary>
    private bool PrefersBackup(TenantId tenant, string sid)
        => StateFor(tenant).PreferBackupUntil.TryGetValue(sid, out var until) && until > DateTime.UtcNow;

    /// <summary>Arm the "route to the backup" window for this session after the primary went silent.
    /// Overwrites any existing deadline (a fresh hang restarts the full window).</summary>
    private void ArmPreferBackup(TenantId tenant, string sid)
    {
        StateFor(tenant).PreferBackupUntil[sid] = DateTime.UtcNow + PreferBackupWindow;
        FileLog.Write($"[WingmanVoiceService] primary voice provider went silent on sid={sid}; " +
                      $"routing this session to the backup provider for {PreferBackupWindow.TotalMinutes:0} min (issue devthrottle_internal#405)");
    }

    /// <summary>
    /// Synthesize the spoken summary to audio through the SAME provider seam the <c>/wingman/tts</c>
    /// endpoint uses (issue #939): the configured mode's base URL + key + model + the user's chosen
    /// voice (<see cref="TtsVoiceConfig"/> / <see cref="TtsModelConfig"/>). This replaced a hardcoded
    /// legacy hardcoded speech call - so a DevThrottle-mode user now hears their configured
    /// voice and hosted narration works from their DevThrottle account. An out-of-credits / cap / setup
    /// condition is returned as a typed <see cref="HostedAiState"/> instead of a silent null, so the
    /// caller can surface the consistent unavailable state.
    /// </summary>
    private async Task<TtsResult> TtsAsync(TenantId tenant, string sid, string text, CancellationToken ct)
    {
        var mode = TranscriptionModeConfig.Get();
        var tts = TranscriptionEndpointResolver.ResolveTts(mode);
        var key = _vault.Get(tts.KeyName);
        if (string.IsNullOrWhiteSpace(key))
            return new TtsResult(null, null, HostedAiState.NeedsKey);

        // The runaway guard, and it announces itself when it fires (issue #1612). This used to be a
        // bare `text[..4000]`: a silent mid-word cut enforcing OpenAI's 4096 limit on a provider that
        // has none, months after we stopped calling OpenAI. The real length control is now the
        // wingman's own instructions (a ~30-second spoken budget) - a summary is short because we
        // asked for a summary, not because we cut an essay in half.
        // ONE decision, made by the one decider (issue #1031): the language and the voice arrive together in an
        // utterance this service could not have built without a language. It resolves neither itself.
        var spoken = NarrationText.LimitForSpeech(_tenantSettings.Utterance(tenant, mode, text), out var wasCut);
        if (wasCut)
            FileLog.Write($"[WingmanVoiceService] narration EXCEEDED {NarrationText.MaxChars} chars " +
                          $"({text.Length}) - spoken text cut and the listener told. The wingman is not summarising.");
        // The ENGINE, on its own, with no knowledge of the language - a language picks a voice inside the one
        // engine and never the engine itself (devthrottle_internal#547).
        var model = _tenantSettings.TtsModel(tenant, mode);
        var url = tts.BaseUrl.TrimEnd('/') + "/audio/speech";
        // The injected client (tests) or the shared static - never a per-call one. Auth goes on the
        // REQUEST, not the client's default headers, so one shared client is safe under concurrent
        // turn-ends. The effective timeout is the per-attempt deadline inside TtsSynthesis - derived
        // from the text length, since synthesis scales linearly with it - which also retries once when
        // a stalled upstream worker never answers, so a flaky voice backend no longer freezes the turn.
        //
        // This was `_ttsHttp ?? new HttpClient()`: in production _ttsHttp is null, so EVERY turn-end
        // built and dropped a client, leaving a socket in TIME_WAIT and forfeiting the warm TLS
        // connection to the proxy. The narration leg fires on a sweep, so that was the hottest speech
        // path in the product paying the reconnect cost every time.
        var http = _ttsHttp ?? SharedTtsHttp;
        // If this session recently saw the primary go silent, ask the proxy to skip it and serve from the
        // backup (issue devthrottle_internal#405). A hang is invisible to the proxy's own failover, so this is how a hang
        // becomes a failover: the Gateway saw it, the proxy did not. The window expires on its own.
        var preferBackup = PrefersBackup(tenant, sid);
        // Time the whole synthesis call (request sent -> response in hand), so how long a narration
        // actually took is in the log next to transcription's transcribeMs. A healthy call is a second
        // or two; the per-attempt deadline caps it at <=108s, so any elapsedMs over ~108s means the
        // deadline bound itself failed (the >3-minute tripwire). Milliseconds, matching transcription.
        // READ THE LANGUAGE BEFORE SPEAKING (audit finding C1): this sink consumed only the text, the voice and
        // the length, so a fabricated utterance with no language was still speakable here. Now it fails loud
        // before a provider call is billed, and the log carries which language was spoken - a code is ASCII and
        // log-safe where the words are not.
        var spokenLanguage = spoken.LanguageCode;
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await TtsSynthesis.PostAsync(http, url, key, new { model, voice = spoken.Voice, input = spoken.Text, response_format = "mp3" }, spoken.Length, preferBackup, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[WingmanVoiceService] tts {mode.ToConfigString()} {(int)resp.StatusCode}");
                // Out of credits / monthly cap (402): map by code to the shared state so the caller
                // records the consistent unavailable state instead of a silent null (issue #939).
                // 402 is the account, not the service: out of credits or over the cap. It is the user's
                // to fix, so it is mapped straight to the account state (NeedsCredits / CapReached) and
                // surfaced at once - there is nothing to back off from, and nothing reaches other sessions.
                if ((int)resp.StatusCode == HostedAiHttp.PaymentRequired)
                    return new TtsResult(null, null, HostedAiErrorMapper.Map402(body));

                // A 429 is the provider saying "stop calling" for THIS call. There is no shared gate any
                // more (removed 2026-07-17): it never reaches across to silence another session.
                //
                // IT IS TRANSIENT BY DEFINITION, so the narration path books a re-attempt against the turn's
                // own bounded ladder and honours the Retry-After carried back here. That number used to be
                // parsed into a log line and dropped, while this branch claimed the session "retries on its
                // own next turn-end / idle sweep" - a sentence with nothing behind it, and the last of the
                // three places in this file that said it (issues #2676 and #2685 were the other two).
                if ((int)resp.StatusCode == 429)
                {
                    var retryAfter = RetryAfterHeader.Parse(resp.Headers);
                    FileLog.Write($"[WingmanVoiceService] tts 429 sid={sid} - the speech provider refused this call" +
                                  $"{(retryAfter is null ? " and sent no Retry-After" : $" and asked for {retryAfter.Value.TotalSeconds:F0}s")}; no audio this cycle - whether a re-attempt is booked is decided by the narration path, not here");
                    return new TtsResult(null, null, HostedAiState.ServiceDown, Failure: SpeechFailure.RateLimited, RetryAfter: retryAfter);
                }

                // Any other error status means the far end failed, and we KNOW it. This used to return a
                // bare null ("other provider error: logged, no shared state") - the reason was written to
                // a log nobody reads and thrown away three lines from where it was known, so the phone
                // fell back to "the Gateway has not made one, or this session's computer is offline".
                // Both false. On 2026-07-15 that cost ~45 minutes of the owner not being able to tell an
                // outage from a bug. Say what actually happened - this ONE session's ServiceDown. No shared
                // cooldown reaches across to other sessions.
                //
                // NO RE-ATTEMPT IS BOOKED for this one, and the log no longer says one is. The service told
                // us it is failing, which is a claim the red "Voice service down" screen is right to make
                // and which a second call seconds later has no reason to change - unlike a refusal that
                // names its own deadline, or a call that never answered at all.
                FileLog.Write($"[WingmanVoiceService] tts {(int)resp.StatusCode} sid={sid} - the server answered with a failure; this session has no audio this cycle and NOTHING is booked for it - the next turn-end or sweep is what comes back");
                return new TtsResult(null, null, HostedAiState.ServiceDown, Failure: SpeechFailure.AnsweredFailure);
            }
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            // The cloud proxy sets this out-of-band header when it quietly failed the primary voice
            // provider over to the backup (Phase 1). Its mere PRESENCE is the whole signal - we never read
            // the value (a generic opaque marker, not the provider name). A fallback is a SUCCESS: the
            // audio is real and playable; we only note it so the Voice screen can add the generic
            // backup-voice line.
            var servedViaFallback = resp.Headers.Contains(FallbackHeaderName);
            if (servedViaFallback)
                FileLog.Write($"[WingmanVoiceService] tts served via backup voice provider (fallback) - {mode.ToConfigString()}");
            var audio = await resp.Content.ReadAsByteArrayAsync(ct);
            sw.Stop();
            // The request -> done span for this narration, in the log for the speed watch (the proxy also
            // returns X-DevThrottle-Elapsed-Ms, its own view one hop out). elapsedMs over ~108s = the
            // deadline bound broke; over 180000 = the three-minute alarm.
            FileLog.Write($"[WingmanVoiceService] tts ok sid={sid}: elapsedMs={sw.ElapsedMilliseconds}, chars={spoken.Length}, lang={spokenLanguage}, served={(servedViaFallback ? "backup" : "primary")}");
            return new TtsResult(audio, contentType, null, servedViaFallback);
        }
        // A timeout (TtsSynthesis exhausted its attempts) or a transport failure is the same story from
        // the user's side: the service did not answer. It is not their fault and there is nothing for
        // them to fix, so it must reach them as ServiceDown rather than as silence.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A TimeoutException here is TtsSynthesis giving up after its attempts - the exact failure it
            // exists to bound. Same story for a transport failure: the service did not answer.
            //
            // A timeout is the ABSENCE of evidence - the service said nothing, so we know nothing about
            // the service. It is reported as Retrying, NOT ServiceDown: ServiceDown makes the phone say
            // "Voice service down", a claim about the service that a non-answer cannot support, whereas
            // Retrying tells the honest truth - the audio is not ready yet and this session is trying
            // again on its own next turn-end / idle sweep. (An ANSWERED failure - the 429/5xx branches
            // above - keeps ServiceDown, because there the service really did tell us it is failing.)
            // This is per session and touches nothing else: there is no shared gate for a timeout to arm.
            sw.Stop();
            FileLog.Write($"[WingmanVoiceService] tts did not answer for this narration sid={sid} (elapsedMs={sw.ElapsedMilliseconds}): {ex.Message} - " +
                          "no answer from the service, so this is Retrying (not down); the narration path books the re-attempt");
            // A TimeoutException specifically means the primary went SILENT (the per-attempt deadline
            // fired, no answer). That silent hang is exactly what the cloud proxy's own failover cannot
            // see, so arm this session to route past the primary to the backup on its next turn-end /
            // idle-sweep retry (issue devthrottle_internal#405, Option B). A transport failure (HttpRequestException) is a
            // DIFFERENT fault - the proxy itself was unreachable, which does not implicate the primary -
            // so it does not arm the backup route.
            if (ex is TimeoutException) ArmPreferBackup(tenant, sid);
            return new TtsResult(null, null, HostedAiState.Retrying, Failure: SpeechFailure.DidNotAnswer);
        }
        // NOTE: no `finally { http.Dispose(); }`. `http` is now either the caller's injected client or
        // the shared static, and disposing EITHER would be wrong - the static must outlive every call
        // (that is the entire point of sharing it) and the injected one belongs to the test that made it.
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mediaType.Length == 0 ? null : mediaType;
    }

    private static string DetectAudioContentType(byte[] audio)
    {
        if (audio.Length >= 4
            && audio[0] == (byte)'R'
            && audio[1] == (byte)'I'
            && audio[2] == (byte)'F'
            && audio[3] == (byte)'F')
            return "audio/wav";

        if (audio.Length >= 3
            && audio[0] == (byte)'I'
            && audio[1] == (byte)'D'
            && audio[2] == (byte)'3')
            return "audio/mpeg";

        if (audio.Length >= 2 && audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0)
            return "audio/mpeg";

        return "audio/mpeg";
    }
}
