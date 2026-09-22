using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

public class SessionViewModel : INotifyPropertyChanged
{
    // The sidebar colour strip reads the SHARED FOLD - SessionOrdering.EffectiveColor (see
    // StatusColorBrush below) - which is what makes Desktop, Cockpit and phone agree. It does NOT read
    // the SessionStatusWingman's Session.StatusColor, and has not since issue #1177 Phase 2: the Gateway
    // is the single fold and reads the Director's cooked StatusColor for NOTHING.
    //
    // This comment used to say the strip read Session.StatusColor "directly, so Desktop and Gateway
    // always show the same color" - a trap of exactly the kind the spec's section 4 lists (naming the
    // wingman as the source of truth). It named the wrong source AND inverted the reason for agreement:
    // agreement comes from calling the one shared fold, not from everyone reading the Director's colour.
    //
    // The colours the fold emits (see SessionOrdering.EffectiveColor) are:
    //   blue   = working          red    = needs you
    //   green  = ready (brand-new session, parked at its prompt with nothing needed)
    //   cyan   = the Wingman judged the stop finished - done, or a report (issue #2892)
    //   yellow = wingman narrating purple = the Wingman judged it carrying on by itself
    //   orange = dictation in flight, or a deep dive running
    //   grey   = parked (on hold) or exited      supporting = a live-controlled Worker's suppressed red
    //   error  = crashed (issue #959)
    //
    // The hexes are NOT here any more. They live in ONE table, StatusPalette, which the turn review
    // also calls, and which the spec's palette table is the written source for.
    // This class had its own private copy, and so did four other surfaces - that was defect 18.
    //
    // THE GREY IS ONE GREY. This strip used to pick between a light #9CA3AF and a #6A6A6A by
    // re-reading the raw Session.OnHold flag - a CLIENT INVENTING A DISTINCTION THE GATEWAY
    // DELIBERATELY DID NOT MAKE. The fold folds snoozed and exited to the same "grey" string
    // precisely so clients render them identically (SessionOrdering.RawActivityColor,
    // owner-approved). That split broke two closed laws at once: the client decided a colour, and
    // the desktop showed two greys where the phone showed one, so the devices disagreed.
    //
    // The distinction is NOT lost - it travels on the fold's own StateLabel ("Snoozed" vs "Exited"),
    // beside the dot. That is the Phase 3 precedent: lifecycle travels on badges and labels, never
    // on colour. Only the DOT collides. Whether snoozed deserves its own dot colour is an OPEN
    // PRODUCT QUESTION (see the spec's "Still open"); if the answer is ever yes it MUST arrive as a
    // distinct NAME from the fold (e.g. "onhold"), never as a client re-reading a raw flag.

    public Session Session { get; }

    public SessionViewModel(Session session)
    {
        Session = session;
        session.OnActivityStateChanged += OnActivityStateChanged;
        session.OnVerificationStatusChanged += OnVerificationStatusChanged;
        session.OnTerminalVerificationStatusChanged += OnTerminalVerificationStatusChanged;
        session.OnStatusColorChanged += OnStatusColorChanged;
        session.OnCachedExplainChanged += OnCachedExplainChangedVm;
        session.OnHoldChanged += OnHoldChangedVm;
        session.OnViewModeChanged += OnViewModeChangedVm;
        session.OnReceivingDictationChanged += OnReceivingDictationChangedVm;
        session.OnNumberChanged += OnNumberChangedVm;
        session.OnPendingDeletionChanged += OnPendingDeletionChangedVm;
        session.OnGatewayResolvedRoleChanged += OnGatewayResolvedRoleChangedVm;
        // THE FOLD ITSELF, arriving over the wire. The Gateway stamps this session's folded display state
        // (colour, label, triage, needs-you-since, the snooze clock, the snooze-ended marker) down onto the
        // Session, and the rail renders it VERBATIM instead of re-folding from local facts it cannot see.
        // Like the role stamp, a fold answer arriving is none of the activity/hold/dictation events the rail
        // already hears, so without this the row keeps its last answer until an unrelated event repaints it.
        session.OnGatewayDisplayStateChanged += OnGatewayDisplayStateChangedVm;
        // THE THREE TRANSIENT OVERLAYS THE RAIL NEVER HEARD ABOUT. Map feeds IsBackgroundRunning,
        // IsTranscribing and IsAutoExplaining into the fold, which renders them purple, orange and yellow -
        // and nothing subscribed, so a desktop dictation or a background-task verdict reached the Gateway
        // (correct on the phone) while this rail kept its old dot, label, count and timer until an
        // unrelated event happened to repaint the row. Same class as the role-stamp bug, one layer earlier:
        // a fold input with no invalidation path. Found by review of pull request 1598.
        session.OnIsBackgroundRunningChanged += OnFoldInputChangedVm;
        session.OnIsTranscribingChanged += OnFoldInputChangedVm;
        session.OnIsExplainingChanged += OnFoldInputChangedVm;
        // The GATE on the purple and yellow overlays, not an overlay itself: turning the Wingman off on a
        // session parked on its background task flips the fold from purple "Background" to red "Needs you"
        // with no overlay flag changing. Easier to miss than a flag for exactly that reason.
        session.OnWingmanEnabledChanged += OnFoldInputChangedVm;
        // The uncommitted-file count now arrives from the Director's monitor rather than from a timer this
        // window owns, so the badge needs its own invalidation the same way every other pushed fact does.
        session.OnUncommittedCountChanged += OnUncommittedCountChangedVm;
        // A prompt was lost, or a later one landed and cleared the alarm (issue internal#811). Its own
        // handler, like the deletion badge, because it is a raw local FACT and not one of the Gateway-fold
        // inputs RaiseFoldProjection re-reads.
        session.OnPromptDeliveryChanged += OnPromptDeliveryChangedVm;
        // The model this session is running (issue internal#1340). Its own handler for the same reason as
        // the two above: it is a raw local FACT re-read at turn-end, not one of the Gateway-fold inputs
        // RaiseFoldProjection re-reads, and a /model switch inside a working session raises none of the
        // events the rail already hears.
        session.OnCurrentModelChanged += OnCurrentModelChangedVm;

        if (session.PromptQueue != null)
        {
            _queueCount = session.PromptQueue.Count;
            session.PromptQueue.OnQueueChanged += OnQueueChanged;
        }
    }

    /// <summary>
    /// Re-read EVERYTHING the shared fold feeds. Every handler for a fold input calls exactly this, and
    /// none of them keeps a list of its own.
    ///
    /// WHY THIS EXISTS RATHER THAN SIX HAND-WRITTEN LISTS. Each handler used to name the properties it
    /// thought its fact touched, and they all disagreed: hold raised seven, activity three, the cached
    /// explain two, the role stamp three. Every list was a private chance to miss one, and missing one
    /// does not fail loudly - it renders a row that has HALF updated, where the dot reads "supporting" and
    /// the text beside it still reads "Needs you" with a live timer. That is worse than a stale row: a
    /// half-updated row looks deliberate, so the reader believes the wrong half.
    ///
    /// The lists were also wrong in a way no test caught: the fold's inputs GROW - role, dictation,
    /// background and auto-explain all arrived over this mission - and a new input meant editing six lists
    /// correctly. This asks the opposite question, "what does the fold feed?", once, in one place. Add a
    /// fold input and every handler already tells the truth about it.
    ///
    /// These are exactly the properties whose getters run FoldInput through SessionOrdering: the dot
    /// (StatusColorBrush), its hover (ColourHover), the row text (ActivityLabel), the triage verdict
    /// behind the "N need you" count (NeedsYou), and the waiting timer, which gates on the folded colour
    /// (HasWaitingDuration/WaitingDurationLabel). Raw flags that are NOT folded - IsOnHold, the number,
    /// the deletion badge - stay with their own handlers, because they are not this question.
    ///
    /// THE ROLE BADGE IS HERE ON PURPOSE, THOUGH ITS GETTER DOES NOT FOLD. ResolvedRole reads
    /// Session.GatewayResolvedRole, which is the SAME Gateway-owned fact the colour folds through
    /// SessionOrdering. The two must therefore move together or the row disagrees with itself - a dot
    /// folded to "supporting" beside a glyph that has not caught up is the gap 1 defect wearing a new
    /// coat. The alternative, raising the badge from the role handler alone, rebuilds the private
    /// per-handler list this method exists to abolish; six of those all disagreeing is what caused four
    /// of the seven defects review found on pull request 1598. Raising four extra cheap getters on an
    /// unrelated input costs nothing measurable; a list that can be missed costs a row that lies.
    /// </summary>
    private void RaiseFoldProjection()
    {
        OnPropertyChanged(nameof(StatusColorBrush));
        OnPropertyChanged(nameof(ColourHover));
        OnPropertyChanged(nameof(ActivityLabel));
        OnPropertyChanged(nameof(NeedsYou));
        OnPropertyChanged(nameof(HasWaitingDuration));
        OnPropertyChanged(nameof(WaitingDurationLabel));
        OnPropertyChanged(nameof(HasHoldTime));
        OnPropertyChanged(nameof(HoldTimeLabel));
        OnPropertyChanged(nameof(IsSnoozeEnded));
        OnPropertyChanged(nameof(ResolvedRole));
        OnPropertyChanged(nameof(HasRoleGlyph));
        OnPropertyChanged(nameof(RoleGlyphText));
        OnPropertyChanged(nameof(RoleTooltip));
        OnPropertyChanged(nameof(InboxLine));
        OnPropertyChanged(nameof(HasInboxLine));
    }

    /// <summary>A fold input changed and carries nothing else - re-read the projection. Serves the three
    /// transient overlays (background task, dictation, auto-explain).</summary>
    private void OnFoldInputChangedVm(bool _) => Dispatcher.UIThread.Post(RaiseFoldProjection);

    // Issue #1181, Task 3b: the session started or stopped receiving a phone dictation - repaint the
    // rail strip (orange while receiving) and refresh its reason text.
    private void OnReceivingDictationChangedVm(bool receiving)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
        });
    }

    private void OnStatusColorChanged(string oldColor, string newColor, string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
        });
    }

    /// <summary>
    /// The live session projected into the wire DTO the shared presentation fold reads, via the ONE mapper
    /// that already builds it for the Gateway - so the rail folds exactly the same inputs the phone does.
    ///
    /// Deliberately NOT cached. Map is pure property reads with no I/O, and the rail re-reads only when a
    /// change event raises a property, not in a loop. A cache here would buy nothing measurable and would
    /// add an invalidation obligation across nine change handlers, where missing one means the rail quietly
    /// shows stale state - the precise failure this whole change exists to remove.
    /// </summary>
    /// <remarks>
    /// INTERNAL, not private, because the rail's row projection (<see cref="SessionRailTree"/>) builds the
    /// ownership tree over these same wire objects. It reads the fold's input through the one mapper rather
    /// than reaching into <see cref="Session"/> for a supervisor id of its own, so the tree the Director draws
    /// is built from exactly the fields the Cockpit and the phone build theirs from.
    /// </remarks>
    internal SessionDto FoldInput => ControlEndpoints.Map(Session, directorId: "");

    /// <summary>
    /// The presentation colour the rail renders - the GATEWAY'S folded answer, stamped down onto this
    /// Session and read back through <see cref="FoldInput"/> (<c>SessionDto.EffectiveColor</c>, sourced from
    /// <c>Session.GatewayEffectiveColor</c>). The rail RENDERS it and computes nothing.
    ///
    /// This used to be <c>SessionOrdering.EffectiveColor(FoldInput)</c> - the rail re-running the shared fold
    /// over its own LOCAL facts. That was the disease, not the cure: the fold reads inputs only the Gateway
    /// has (a phone dictation, the Gateway's transcription, a voice summary being prepared, and the snooze
    /// clock and its expiry), so the desktop's copy diverged for every session that had stopped - a snoozed
    /// session read red "Needs you" here while the phone and the Cockpit read "Snoozed". Pushing four more
    /// inputs down would have narrowed the gap; the fix is that the desktop does not fold at all. The Gateway
    /// is the single fold; this seam carries its answer down to the one screen that cannot poll for itself.
    ///
    /// NO STAMP YET -&gt; "unknown" (a neutral grey), NOT magenta. Until a Gateway has stamped a fold (a fresh
    /// session before its first push, or a Director with no tunnel - the "no Gateway, no fold" floor) the
    /// value is null, and the rail shows a neutral placeholder rather than guessing a colour. A genuinely
    /// unrecognised stamp value still falls through to the magenta sentinel in <see cref="StatusColorBrush"/>,
    /// which is the real fail-loud. (docs/new_architecture/sessions.html.)
    /// </summary>
    private string EffectiveColor => Dot.Colour;

    /// <summary>
    /// The rail dot as the rail is actually painting it right now - the colour AND whether that colour is the
    /// Gateway's own stamp for this session. The second half is what stops the hover contradicting the dot;
    /// see <see cref="RailDot"/>.
    /// </summary>
    private RailDot Dot => RailDotFor(IsGatewayOffline, FoldInput.EffectiveColor, Session.ActivityState, IsGatewaySettled, Session.OnHold);

    /// <summary>
    /// The rail dot's colour name. Pure so it is tested without an Avalonia app - the getter above binds the
    /// three live inputs.
    ///
    /// ONLINE (<paramref name="gatewayOffline"/> false): render the Gateway's stamped answer VERBATIM. When
    /// there is NO stamp, tell the connect warm-up apart from a broken push by whether the tunnel has SETTLED
    /// (<paramref name="gatewaySettled"/>): not yet settled -&gt; the neutral placeholder ("unknown" -&gt; grey),
    /// waiting for the first push; settled but STILL unstamped -&gt; the loud magenta <see cref="UnstampedSentinel"/>,
    /// because the push seam is not delivering the Gateway's verdict and a grey would read as "parked". The
    /// desktop computes NO session colour; the Gateway owns every one. The magenta is an ALARM that the seam is
    /// broken, not a state the desktop invented - the same missing-stamp condition the cockpit fails loud on.
    ///
    /// GATEWAY-OFFLINE FLOOR (owner's ruling, 2026-07-19): the desktop HOSTS these sessions and is the one
    /// surface that can still tell the truth with no Gateway to ask. When the tunnel is down the Gateway
    /// stamp is frozen on whatever it last sent (a stale yellow / red / grey), so instead paint the ONE fact
    /// this Director owns firsthand - is the agent producing terminal output right now - as blue (working) or
    /// red (idle), and NOTHING else. NEVER run the Gateway fold (SessionOrdering) here: it reads Gateway-only
    /// facts - VoiceAudioReady, dictation, the snooze clock - that are false/absent on the Director, so a
    /// local fold would wedge every voice-mode session permanently yellow "Preparing voice". Two colours
    /// only; every richer state waits for the Gateway to come back and stamp it. The phone and Cockpit keep
    /// the neutral placeholder when offline because, unlike the Director, they do not host the session and
    /// cannot know what it is doing. (docs/new_architecture/sessions.html.)
    /// </summary>
    internal static string RailColor(bool gatewayOffline, string? gatewayStamp, ActivityState localActivity, bool gatewaySettled, bool isHeld = false)
        => RailDotFor(gatewayOffline, gatewayStamp, localActivity, gatewaySettled, isHeld).Colour;

    /// <summary>
    /// The rail dot: the colour <see cref="RailColor"/> paints, and WHERE THAT COLOUR CAME FROM.
    ///
    /// <paramref name="ColourIsTheGatewaysStamp"/> is true only when the dot is showing the colour the
    /// Gateway itself stamped onto this session - so the Gateway's stamped LABEL for the session describes
    /// the very state the dot is showing, and the two may be read as one sentence. It is false whenever the
    /// rail chose the pixel for itself: the gateway-offline floor's blue and red, its grey for a held session
    /// with no frozen stamp, the warm-up placeholder, and the unstamped sentinel.
    ///
    /// WHY THIS EXISTS. The offline floor paints a LIVE local fact (is the agent producing output right now)
    /// while <c>SessionDto.StateLabel</c> is frozen on whatever the Gateway last said before the tunnel
    /// dropped. Those two describe different moments, and the Session Cards mission combined them into one
    /// hover - which produced the string "Working: Snoozed", a row contradicting itself, which is the exact
    /// defect this mission exists to remove. The floor is not the defect and is not changed here; combining
    /// its colour with a label about another state was. <see cref="SessionDotHover"/> reads this flag and
    /// declines to join the two.
    /// </summary>
    public readonly record struct RailDot(string Colour, bool ColourIsTheGatewaysStamp);

    /// <summary>
    /// <see cref="RailColor"/>'s decision, with its provenance. One function so the colour and "is this the
    /// Gateway's stamp" can never drift apart - <see cref="RailColor"/> is this function's Colour.
    /// </summary>
    internal static RailDot RailDotFor(bool gatewayOffline, string? gatewayStamp, ActivityState localActivity, bool gatewaySettled, bool isHeld = false)
    {
        if (gatewayOffline)
        {
            if (localActivity is ActivityState.Working or ActivityState.Starting)
                return new RailDot("blue", ColourIsTheGatewaysStamp: false);

            // A snooze is an explicit, durable hold the USER set, cached on the Director as a Gateway-owned
            // fact (Session.OnHold, stamped down from SessionDto.OnHold) - a locally-known truth, NOT a
            // Gateway-only fold input like VoiceAudioReady. So the floor CAN honour it without running the
            // fold: keep a snoozed idle session snoozed (the grey the Gateway last stamped) instead of
            // flattening it to red. Without this, a snoozed session reverts to "Needs you" red on every
            // tunnel reconnect - and when the tunnel flaps, snooze appears to "not stick" at all.
            // The frozen stamp is kept here, so when there IS one the dot is once again showing the Gateway's
            // own colour and its frozen label describes that same state. With no stamp the grey is the rail's
            // own choice, and the label - if any - is about something else.
            if (isHeld)
                return gatewayStamp is not null
                    ? new RailDot(gatewayStamp, ColourIsTheGatewaysStamp: true)
                    : new RailDot("grey", ColourIsTheGatewaysStamp: false);

            return new RailDot("red", ColourIsTheGatewaysStamp: false);
        }

        if (gatewayStamp is not null)
            return new RailDot(gatewayStamp, ColourIsTheGatewaysStamp: true);

        // Online, but the Gateway has stamped no display state for this session. Until the tunnel has SETTLED
        // (the first stamps arrive within the Gateway's ~5s fold sweep) this is the normal connect warm-up, so
        // show the neutral placeholder and wait. Once settled and STILL unstamped, the push seam is not
        // delivering - the exact fault that let a working session sit grey while the Gateway folded it blue
        // (issue #1966) - so raise the loud magenta sentinel, never a grey that reads as "parked".
        return new RailDot(gatewaySettled ? UnstampedSentinel : "unknown", ColourIsTheGatewaysStamp: false);
    }

    /// <summary>The colour name <see cref="RailColor"/> returns when the Director is CONNECTED and settled yet
    /// holds no Gateway stamp for a session - a broken display-state push, not a real state. It renders the
    /// magenta <see cref="StatusPalette.Broken"/> pixel and a specific loud log line
    /// (<see cref="StatusPalette.ReportMissingStamp"/>). Distinct from a Gateway-emitted "unknown" COLOUR,
    /// which is a genuine indeterminate activity state and renders grey.</summary>
    internal const string UnstampedSentinel = "unstamped";

    /// <summary>
    /// The owning Director's tunnel to its Gateway is not up, so no fresh fold can arrive and the last stamp
    /// is stale. <see cref="GatewayConnectionStatus.Connected"/> is the ONLY state that renders the Gateway's
    /// pushed answer; every other one - dialing, reconnecting, failed, or a local-only Director with no
    /// Gateway configured at all - falls to the two-colour activity floor in <see cref="EffectiveColor"/>.
    /// Resolved live off the app's single <see cref="GatewayConnectionMonitor"/> (the same instance the
    /// sidebar indicator reads). Null (host not started yet) counts as NOT offline, so a just-launched
    /// Director shows the neutral placeholder until its monitor exists rather than flashing the floor.
    /// </summary>
    private static bool IsGatewayOffline
    {
        get
        {
            var monitor = (global::Avalonia.Application.Current as App)?.ControlApiHost?.GatewayMonitor;
            return monitor is not null && monitor.Status != GatewayConnectionStatus.Connected;
        }
    }

    /// <summary>
    /// The tunnel is Connected AND has been for longer than <see cref="GatewayStampGrace"/> - long enough that
    /// the Gateway's fold sweep (~5s) should have stamped every session at least once. <see cref="RailColor"/>
    /// uses it to tell a normal connect warm-up (no stamp yet -&gt; neutral placeholder) from a broken push seam
    /// (settled but still unstamped -&gt; the loud magenta sentinel). Resolved off the same single
    /// <see cref="GatewayConnectionMonitor"/> the sidebar reads;
    /// <see cref="GatewayConnectionMonitor.LastVerifiedAt"/> is stamped once when the tunnel comes up and is not
    /// churned while it stays up, so it marks the moment this connection settled.
    /// </summary>
    private static bool IsGatewaySettled
    {
        get
        {
            var monitor = (global::Avalonia.Application.Current as App)?.ControlApiHost?.GatewayMonitor;
            if (monitor is null || monitor.Status != GatewayConnectionStatus.Connected) return false;
            return monitor.LastVerifiedAt is { } since && DateTime.UtcNow - since > GatewayStampGrace;
        }
    }

    /// <summary>How long after the tunnel connects the rail waits for the first Gateway stamp before it treats
    /// a still-unstamped session as a broken push (magenta) rather than a warm-up (neutral). Covers the
    /// Gateway's ~5s fold sweep with margin.</summary>
    private static readonly TimeSpan GatewayStampGrace = TimeSpan.FromSeconds(15);

    /// <summary>Repaint the rail dot because the Gateway connection flipped (connected &lt;-&gt; offline).
    /// The offline floor in <see cref="EffectiveColor"/> reads the connection status, and that status change
    /// carries none of the per-session events the row already hears - so MainWindow, which owns the one
    /// <see cref="GatewayConnectionMonitor"/> subscription, calls this on every row when the status changes.</summary>
    public void RefreshGatewayFloor() => Dispatcher.UIThread.Post(RaiseFoldProjection);

    /// <summary>
    /// The sidebar colour strip's brush: the shared fold's colour, mapped through the ONE palette.
    /// Hold, dictation, briefing and the activity colour are all folded by
    /// <see cref="SessionOrdering"/>, and the name-to-hex mapping is <see cref="StatusPalette"/> -
    /// this property decides nothing at all, which is the point.
    /// </summary>
    public ISolidColorBrush StatusColorBrush
    {
        get
        {
            var color = EffectiveColor;
            // The magenta sentinel is meant to be unmissable on the rail; this makes it diagnosable
            // too, by naming the colour in the log. Logged only on the edge - a binding getter runs
            // on every repaint, and a line per frame is not a diagnostic, it is a flood.
            if (!StatusPalette.Knows(color) && color != _lastUnknownColorLogged)
            {
                _lastUnknownColorLogged = color;
                if (color == UnstampedSentinel)
                    StatusPalette.ReportMissingStamp(Session.Id.ToString());
                else
                    StatusPalette.ReportUnknownColor(color, Session.Id.ToString());
            }
            return StatusPalette.BrushFor(color);
        }
    }

    private string? _lastUnknownColorLogged;

    /// <summary>
    /// True when this session is flagged for deletion and awaiting the reaper - drives the rail's
    /// "winding down" badge (defect 23).
    ///
    /// PENDING DELETION IS A BADGE, NEVER A COLOUR (owner's ruling, 14 July 2026). It says nothing about
    /// what the agent is DOING, and a flagged session may still be working - the reaper waits out a
    /// running final turn (SessionManager.ReapPendingDeletions). So it rides BESIDE the dot and never
    /// touches <see cref="StatusColorBrush"/>: a flagged session that is working shows a BLUE strip with
    /// this badge; a flagged session waiting on the user still shows red "Needs you" with this badge.
    ///
    /// Deliberately NOT folded into <see cref="EffectiveColor"/> - putting it there would make it a
    /// colour and would spend the dot, which says what a session is DOING, on saying it is scheduled to
    /// go. That is the same mistake as the "Supporting" grey that hid 23 minutes of real work.
    /// </summary>
    public bool IsPendingDeletion => Session.PendingDeletion;

    /// <summary>
    /// True when a prompt to this session was NOT delivered and nothing has landed since - the user's
    /// words are gone right now (issue internal#811). Drives the rail's red "NOT DELIVERED" badge.
    ///
    /// THE RAIL CALLS THE SHARED FOLD HERE, AND THAT IS NOT A RELAPSE. The rail deliberately does not
    /// fold the COLOUR (see <see cref="EffectiveColor"/>): that fold reads inputs only the Gateway has -
    /// a phone dictation, a voice summary in preparation, the snooze clock - so a local re-run diverges.
    /// This fold reads the opposite kind of input. Every fact behind it is DIRECTOR-LOCAL and firsthand:
    /// only the machine running the terminal can see a submit fail, and <see cref="FoldInput"/> carries
    /// those facts because this Director is the thing that reported them upward in the first place.
    /// Calling <see cref="SessionOrdering.PromptDeliveryNotice"/> here is therefore the SAME answer the
    /// Gateway gives, from the same function - not a second one - and it survives the tunnel being down,
    /// which is exactly when a delivery is most likely to have failed.
    ///
    /// A BADGE, NEVER A COLOUR, for the same reason as the winding-down badge above: the agent never
    /// heard the prompt, so nothing about what it is DOING changed. Recolouring the dot would tell a lie
    /// about the agent in order to tell the truth about the delivery.
    /// </summary>
    public bool HasUndeliveredPrompt => SessionOrdering.PromptDeliveryNotice(FoldInput) is not null;

    /// <summary>The "NOT DELIVERED" badge tooltip: the Gateway fold's own sentence, so the rail, the
    /// Cockpit and the phone all say the words once.</summary>
    public string UndeliveredPromptTooltip =>
        SessionOrdering.PromptDeliveryNotice(FoldInput) ?? "";

    /// <summary>The "winding down" badge tooltip: the human reason captured when the session was
    /// flagged (e.g. "jobs-auto: nothing to report"), or a plain fallback when none was given.</summary>
    public string PendingDeletionTooltip => Session.DeletionReason is { } reason
        ? $"Marked for deletion - {reason}"
        : "Marked for deletion - reaping shortly";

    /// <summary>Issue: defect 23. The pending-deletion FACT changed on the Director - repaint the rail
    /// badge on the UI thread. This is the session's own signal (<see cref="Session.OnPendingDeletionChanged"/>);
    /// the rail used to learn about deletion only because MarkForDeletion wrote a colour, which it no
    /// longer does and was never allowed to.</summary>
    /// <summary>Issue internal#811: the delivery alarm turned on or off - repaint the badge on the UI
    /// thread. Both properties move together; they read the same fold.</summary>
    private void OnPromptDeliveryChangedVm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(HasUndeliveredPrompt));
            OnPropertyChanged(nameof(UndeliveredPromptTooltip));
        });
    }

    private void OnPendingDeletionChangedVm(bool _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsPendingDeletion));
            OnPropertyChanged(nameof(PendingDeletionTooltip));
        });
    }

    // The Gateway stamped (or cleared) this session's role. That is a FOLD INPUT - SessionOrdering
    // suppresses a controlled worker's red to "supporting" - so the rail must re-read exactly as it does
    // for an activity or hold change. Nothing re-read on a role before this, so the stamp arrived, the
    // fold was right, and the dot stayed red anyway.
    //
    // RAISE EVERY PROPERTY THE FOLD FEEDS, NOT JUST THE DOT. The first version of this handler raised
    // only the brush, the reason and the count, and review caught it: ActivityLabel is
    // SessionOrdering.StateLabel(FoldInput), and HasWaitingDuration/WaitingDurationLabel gate on
    // EffectiveColor - all three read the role. So the dot repainted to "supporting" while the row text
    // still said "Needs you" with a live waiting timer beside it. A half-re-read is its own lie: one row
    // disagreeing with itself is worse than the stale row it replaced, because it looks deliberate.
    // Match OnStatusColorChanged and OnActivityStateChanged between them - the same fold, the same reads.
    private void OnGatewayResolvedRoleChangedVm(string? _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
        });
    }

    /// <summary>
    /// The Gateway stamped this session's folded display state down (colour, label, triage, needs-you-since,
    /// snooze clock, snooze-ended). That IS what the rail renders now - the dot, the row text, the "N need
    /// you" verdict, the waiting timer, the snooze countdown and the snooze-ended badge all read it - so a
    /// stamp arriving must repaint the whole projection. RaiseFoldProjection covers the fold outputs; the
    /// countdown/timer labels ride along because HoldTimeLabel/WaitingDurationLabel are in it.
    /// </summary>
    private void OnGatewayDisplayStateChangedVm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
        });
    }

    /// <summary>True when the user has parked this session on hold. Drives the menu toggle
    /// label and the light-gray strip color.</summary>
    public bool IsOnHold => Session.OnHold;

    /// <summary>
    /// What the colour dot says when you hover it: the legend's name for the colour the Gateway painted,
    /// then the Gateway's stamped label when it adds anything. Every word is the Gateway's - see
    /// <see cref="SessionDotHover"/>, which does the choosing and the punctuation and writes nothing.
    ///
    /// THIS USED TO BE THE DIRECTOR'S OWN SENTENCE, and that was the defect. It read
    /// <c>Session.OnHold ? "Snoozed (set aside by you)" : Session.IsReceivingDictation ? "Receiving a
    /// dictation from your phone" : Session.LastStatusReason</c> - three sentences this machine wrote
    /// itself, two of them hard-coded here and the third written by the Director before the Wingman had
    /// judged the stop. So a purple "Carrying on" dot hovered "needs you", and a calm row could hover the
    /// words the owner scans for. The owner's ruling: <em>"Nobody should give any local reason for
    /// anything. It should always be the gateway ... if you hover over the color, it should show you what
    /// that color means."</em>
    ///
    /// Nothing is lost by dropping those two hard-coded sentences: the fold already stamps "Snoozed" and
    /// the dictation state into <see cref="ActivityLabel"/>, which this hover renders - from the Gateway,
    /// where every surface reads the same words.
    ///
    /// Empty when the Gateway has given this desktop neither a legend entry nor a label, which is what the
    /// unstamped sentinel is: the Gateway said nothing, so the rail says nothing.
    ///
    /// WITH THE TUNNEL DOWN IT IS THE COLOUR'S NAME ALONE. The gateway-offline floor paints a live local
    /// reading of the terminal while <see cref="ActivityLabel"/> stays frozen on the Gateway's last word, so
    /// the two are about different moments - joined, they read "Working: Snoozed". <see cref="Dot"/> carries
    /// which pixels the rail chose for itself, and <see cref="SessionDotHover"/> leaves the frozen label off
    /// those. The floor itself is a separate ruling and is untouched.
    /// </summary>
    public string ColourHover => SessionDotHover.For(Dot, ActivityLabel, ColourLegend);

    /// <summary>
    /// What this desktop last read from its Gateway about what the colours mean, or null when it has not
    /// read it yet (no Gateway, not connected, or the read failed). Resolved live off the app's single
    /// <see cref="SessionColourLegendCache"/> - the same one the "What do the colours mean?" window reads,
    /// so the window and the hover can never explain a colour differently. Never blocks: the cache is a
    /// last-known copy and this is read from a binding getter.
    /// </summary>
    private static SessionColourLegendDto? ColourLegend =>
        (global::Avalonia.Application.Current as App)?.ControlApiHost?.ColourLegend.Current;

    private void OnHoldChangedVm(bool onHold)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
            // IsOnHold is a RAW flag the rail renders directly (the snooze glyph), not a fold output - so
            // it is not RaiseFoldProjection's business and stays here.
            OnPropertyChanged(nameof(IsOnHold));
        });
    }

    /// <summary>True when a phone is currently watching this session through the Voice (in-car)
    /// tab. Drives the rail's in-voice-mode ear indicator (issue #554). A pure passthrough of
    /// <see cref="Session.VoiceMode"/> so the rail never reads the model directly.</summary>
    public bool IsVoiceMode => Session.VoiceMode;

    private void OnViewModeChangedVm(MobileViewMode oldMode, MobileViewMode newMode)
    {
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsVoiceMode)));
    }

    private void OnCachedExplainChangedVm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // The waiting duration is proxied from CachedExplainAt, which this briefing just
            // set. When a session is already red and its first briefing lands, HasWaitingDuration
            // flips false->true here; without raising it the "waiting Xm" list label would not
            // appear until the next 15s timer tick.
            RaiseFoldProjection();
        });
    }

    /// <summary>How long this session has been waiting on you, shown in the list only when red, so you can
    /// see at a glance WHICH needs-you session is the most stale and triage it first. Reads the GATEWAY-owned
    /// clock (<c>SessionDto.NeedsYouSince</c>, stamped down from <c>Session.GatewayNeedsYouSince</c>) so the
    /// "waiting 11m" here matches every other surface exactly. It used to proxy the local last-briefing time,
    /// which drifted from the Gateway's. Gated on the folded colour being red, so a snoozed session (grey)
    /// never nags with a clock.</summary>
    public bool HasWaitingDuration =>
        string.Equals(EffectiveColor, "red", StringComparison.OrdinalIgnoreCase)
        && FoldInput.NeedsYouSince is not null;

    public string WaitingDurationLabel
    {
        get
        {
            if (FoldInput.NeedsYouSince is not { } since || !HasWaitingDuration) return "";
            var d = DateTime.UtcNow - since.ToUniversalTime();
            if (d.TotalMinutes < 1) return "waiting <1m";
            if (d.TotalMinutes < 60) return $"waiting {(int)d.TotalMinutes}m";
            return $"waiting {(int)d.TotalHours}h";
        }
    }

    /// <summary>True when this session is snoozed with a running clock, so the rail can show WHEN it comes
    /// back. Reads the GATEWAY-owned snooze deadline (<c>SessionDto.SnoozeUntil</c>, stamped down from
    /// <c>Session.GatewaySnoozeUntil</c>) - the Director never owns the snooze clock. Null for a deferred
    /// snooze that has not landed (no deadline yet) and for anything not snoozed.</summary>
    public bool HasHoldTime => FoldInput.SnoozeUntil is not null;

    /// <summary>"wakes in 3h 48m" - how long until an armed snooze returns the session to needs-you. Shown
    /// beside the "Snoozed" label so a snoozed row says not just that it is parked but until when. Reads the
    /// Gateway's deadline; the Director renders the countdown but owns no clock. Empty when there is no
    /// running snooze.</summary>
    public string HoldTimeLabel
    {
        get
        {
            if (FoldInput.SnoozeUntil is not { } until) return "";
            var remaining = until.ToUniversalTime() - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return "waking up";
            if (remaining.TotalMinutes < 1) return "wakes in <1m";
            if (remaining.TotalMinutes < 60) return $"wakes in {(int)remaining.TotalMinutes}m";
            var hours = (int)remaining.TotalHours;
            var mins = remaining.Minutes;
            return mins > 0 ? $"wakes in {hours}h {mins}m" : $"wakes in {hours}h";
        }
    }

    /// <summary>True when this session JUST came back on its own because its snooze timer fired - the
    /// Gateway's <c>SessionDto.SnoozeExpired</c> marker (stamped down from <c>Session.GatewaySnoozeExpired</c>).
    /// Drives the rail's distinct "SNOOZE ENDED" badge so the owner knows this is a "go see why it went
    /// quiet" item, not a fresh turn-end.</summary>
    public bool IsSnoozeEnded => FoldInput.SnoozeExpired;

    /// <summary>Re-raise time-derived list labels; called periodically so the waiting duration and the
    /// snooze countdown tick without an event.</summary>
    public void RefreshTimeLabels()
    {
        OnPropertyChanged(nameof(WaitingDurationLabel));
        OnPropertyChanged(nameof(HasWaitingDuration));
        OnPropertyChanged(nameof(HoldTimeLabel));
        OnPropertyChanged(nameof(HasHoldTime));
    }

    public string DisplayName => Session.CustomName
        ?? Path.GetFileName(Session.RepoPath.TrimEnd('\\', '/'));

    /// <summary>The session's three-digit number as text (issue #820), e.g. "412", or empty when
    /// the session has no number. A separate field from <see cref="DisplayName"/> so it shows as a
    /// muted prefix in the rail and header and a rename never affects it.</summary>
    public string NumberBadge => Session.Number is int n ? n.ToString() : "";

    /// <summary>True when the session has a three-digit number to show (issue #820).</summary>
    public bool HasNumber => Session.Number.HasValue;

    /// <summary>Issue #1292: the Gateway assigned this session's number after creation - repaint the
    /// rail badge on the UI thread so the number appears when it arrives.</summary>
    private void OnNumberChangedVm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(NumberBadge));
            OnPropertyChanged(nameof(HasNumber));
        });
    }

    /// <summary>Repaint the amber "N chg" badge when the Director's git monitor publishes a new count.
    /// Posted to the UI thread: the monitor raises this from a background poll.</summary>
    private void OnUncommittedCountChangedVm(int? _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(UncommittedCount));
            OnPropertyChanged(nameof(HasUncommittedChanges));
        });
    }

    public string? CustomColor
    {
        get => Session.CustomColor;
        set
        {
            Session.CustomColor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomColor));
            OnPropertyChanged(nameof(CustomColorBrush));
            OnPropertyChanged(nameof(DragHandleBrush));
        }
    }

    public bool HasCustomColor => !string.IsNullOrWhiteSpace(CustomColor);

    private static readonly ISolidColorBrush DefaultDragHandleBrush = new SolidColorBrush(Color.Parse("#3C3C3C"));

    public ISolidColorBrush DragHandleBrush => HasCustomColor ? CustomColorBrush : DefaultDragHandleBrush;

    public ISolidColorBrush CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CustomColor))
                return new SolidColorBrush(Colors.Transparent);
            try
            {
                var color = Color.Parse(CustomColor);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(Colors.Transparent);
            }
        }
    }

    /// <summary>
    /// The session's state in words - the GATEWAY'S folded label (<c>SessionDto.StateLabel</c>, stamped down
    /// from <c>Session.GatewayStateLabel</c>), the SAME string the Cockpit and the phone render, so a session
    /// cannot read "Needs you" here and "Snoozed" there. Rendered VERBATIM; the rail does not label anything
    /// itself. This used to be <c>SessionOrdering.StateLabel(FoldInput)</c> - the rail re-folding locally -
    /// which is exactly why it disagreed. Empty until a Gateway stamps one (the no-Gateway floor).
    /// </summary>
    public string ActivityLabel => FoldInput.StateLabel ?? "";

    /// <summary>
    /// The row line (Message Load mission, slice 4): what waits in this session's fleet inbox, in the
    /// GATEWAY'S words (<c>SessionDto.InboxLine</c>, stamped down from <c>Session.GatewayInboxLine</c>) -
    /// the same string the Cockpit and the phone render. Verbatim; the rail never counts or words messages.
    /// Empty when nothing waits or no Gateway has stamped one.
    /// </summary>
    public string InboxLine => FoldInput.InboxLine ?? "";

    /// <summary>Whether the row line has anything to show. Presence only - the words decide nothing here.</summary>
    public bool HasInboxLine => InboxLine.Length > 0;

    /// <summary>
    /// True when this session is waiting on YOU - the shared fold's triage verdict, not a colour.
    /// Drives the "N need you" count beside the rail's SESSIONS header.
    ///
    /// Reads <see cref="SessionOrdering.Classify"/>, the SAME function the phone's web-push badge
    /// counts by (WebPushNeedsYouNotifier), so the number on the header and the number on the phone
    /// are folded by one rule. The header used to count the RAW cooked colour
    /// (<c>Session.StatusColor == "red"</c>) with no hold check, no role, and no overlays - so a
    /// snoozed session sat grey and labelled "Snoozed" underneath a header reading "1 need you".
    /// Three readings of one session, and nothing reconciled them.
    ///
    /// This reads the GATEWAY'S folded triage bucket (<c>SessionDto.TriageBucket</c>, stamped down from
    /// <c>Session.GatewayTriageBucket</c>), the SAME verdict the phone's web-push badge counts by, rendered
    /// VERBATIM. It used to be <c>SessionOrdering.Classify(FoldInput)</c> - the rail re-classifying locally,
    /// so a session the Gateway had bucketed onHold (dictation, voice, a snooze) still counted here. Absent
    /// stamp counts as not-needing-you (the no-Gateway floor never nags).
    /// </summary>
    public bool NeedsYou => string.Equals(FoldInput.TriageBucket, "needsYou", StringComparison.Ordinal);

    // Agent badge for the session list. Colored pill shown next to the session name
    // so it's visually obvious which agent CLI this session is running.
    private static readonly ISolidColorBrush ClaudeAgentBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly ISolidColorBrush PiAgentBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly ISolidColorBrush CodexAgentBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xA3, 0x7F));
    private static readonly ISolidColorBrush GeminiAgentBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0x43, 0x35));
    private static readonly ISolidColorBrush OpenCodeAgentBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
    private static readonly ISolidColorBrush CursorAgentBrush = new SolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4));  // cyan - distinct from Claude blue / Pi violet / RawCli slate, readable on the dark rail
    private static readonly ISolidColorBrush GrokAgentBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08));  // amber - Grok, distinct from OpenCode orange / Gemini red, readable on the dark rail
    private static readonly ISolidColorBrush CopilotAgentBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));  // emerald - GitHub Copilot, distinct from Cursor cyan / Grok amber, readable on the dark rail
    private static readonly ISolidColorBrush RawCliAgentBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));  // slate gray - neutral, not tied to any brand

    public string AgentLabel => LabelFor(Session.AgentKind);

    public ISolidColorBrush AgentBadgeBrush => BadgeBrushFor(Session.AgentKind);

    // ===== The model, folded into the agent badge (issue internal#1340) =====
    //
    // The owner picked the combined badge over a second one beside it: "Claude Code | fable-5" reads as one
    // identity - which tool, running which model - and costs one badge's worth of the row rather than two,
    // on a rail that already carries a role glyph, a not-delivered alarm and a winding-down marker.
    //
    // The rail RENDERS this; it does not decide it. Both halves come from ModelDisplayFold, the same
    // function the Gateway stamps onto SessionDto.ModelDisplay for the browser clients, so the desktop and
    // the Cockpit cannot word the same session two ways. It is folded here from the LOCAL DTO rather than
    // read from a Gateway stamp because its two inputs are facts this Director owns firsthand (its records
    // watcher writes the model; its driver declares the capability), so the answer is identical and it
    // still reads correctly with no Gateway attached - unlike the session COLOUR, which reads Gateway-only
    // inputs and is deliberately never folded here.

    /// <summary>The model half of the agent badge: the recorded model shortened for the rail
    /// (<c>fable-5</c>), or the honest words for whichever absence this is.</summary>
    public string ModelLabel => ModelDisplayFold.For(FoldInput).Text;

    /// <summary>True when there is no recorded model, so the badge can mute the model half rather than
    /// give an absence the same weight as a fact. STYLING only - the words already say which absence
    /// it is.</summary>
    public bool IsModelAbsent => ModelDisplayFold.For(FoldInput).IsAbsent;

    /// <summary>The model half's opacity: full weight for a recorded model, dimmed for either absence, so
    /// a row with no model reads as quieter without the words changing. Bound directly rather than through
    /// a bool-to-opacity converter, which is a whole class to say one number.</summary>
    public double ModelOpacity => IsModelAbsent ? 0.72 : 1.0;

    /// <summary>The badge tooltip: the agent, then the FULL recorded model id (the badge itself shows a
    /// shortened form), or the sentence explaining the absence. One badge, one tooltip.</summary>
    public string AgentModelTooltip => $"{AgentLabel} - {ModelDisplayFold.For(FoldInput).Tooltip}";

    /// <summary>The agent's records named a different model - a mid-session <c>/model</c> switch, or the
    /// first turn finishing and recording one at last. Repaint the badge on the UI thread: the records
    /// watcher raises this from a background read.</summary>
    private void OnCurrentModelChangedVm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ModelLabel));
            OnPropertyChanged(nameof(IsModelAbsent));
            OnPropertyChanged(nameof(ModelOpacity));
            OnPropertyChanged(nameof(AgentModelTooltip));
        });
    }

    /// <summary>Pure agent-kind -> rail label mapping. Every provider has its own arm; the
    /// default is Claude Code (kind 0). Static so it can be unit-tested without a live
    /// <see cref="Session"/> (issue #517 regression: Cursor must not fall through to "Claude Code").</summary>
    public static string LabelFor(AgentKind kind) => kind switch
    {
        AgentKind.Pi => "Pi",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        AgentKind.OpenCode => "OpenCode",
        AgentKind.Cursor => "Cursor",
        AgentKind.Grok => "Grok",
        AgentKind.Copilot => "GitHub Copilot",
        AgentKind.RawCli => "Custom CLI",
        _ => "Claude Code"
    };

    /// <summary>Pure agent-kind -> rail badge brush mapping. Each provider uses its own brand
    /// hue; the default is the Claude blue (kind 0). Static for the same reason as
    /// <see cref="LabelFor"/> (issue #517).</summary>
    public static ISolidColorBrush BadgeBrushFor(AgentKind kind) => kind switch
    {
        AgentKind.Pi => PiAgentBrush,
        AgentKind.Codex => CodexAgentBrush,
        AgentKind.Gemini => GeminiAgentBrush,
        AgentKind.OpenCode => OpenCodeAgentBrush,
        AgentKind.Cursor => CursorAgentBrush,
        AgentKind.Grok => GrokAgentBrush,
        AgentKind.Copilot => CopilotAgentBrush,
        AgentKind.RawCli => RawCliAgentBrush,
        _ => ClaudeAgentBrush
    };

    // ===== Automatic session role (non-color rail glyph) =====

    /// <summary>
    /// This session's resolved automatic role, or NULL when no Gateway has said yet.
    ///
    /// GAP 1, CLOSED: this used to be STAMPED by MainWindow on every list rebuild from
    /// SessionManager.ResolveLocalRole - the Director resolving a role for itself, which law 2 forbids.
    /// The colour already read the Gateway's stamp while this glyph read the Director's local guess, so
    /// one row could show a Gateway-resolved colour beside a Director-resolved badge that contradicted
    /// it. Worse, the local resolver only ever saw THIS Director's roster, so a controller on another
    /// machine was invisible to it - the exact cross-machine blind spot the down-channel exists to fix.
    /// It now reads Session.GatewayResolvedRole, the same fact the colour folds. One source, one answer.
    ///
    /// BEFORE THE FIRST STAMP ARRIVES, NOTHING SHOWS - decided by the Architect, and it is why this is
    /// nullable rather than defaulting to Standalone. The old default asserted "Standalone" about a
    /// session nobody had classified yet, which is a guess wearing the costume of an answer; a session
    /// the Gateway has not spoken about is not standalone, it is unknown. "No answer yet" renders as no
    /// badge, and no badge is not a lie. RoleGlyphFor(null) already returns "" via its catch-all arm,
    /// so an unstamped session and a Standalone one look identical on the rail - the badge only ever
    /// appears when the Gateway has named a Manager, Worker or Architect.
    /// </summary>
    public string? ResolvedRole => Session.GatewayResolvedRole;

    /// <summary>True when the role warrants a rail glyph (Manager, Worker, or Architect). Standalone,
    /// an unknown value, and "no stamp yet" (null) all show nothing, so the badge stays out of the way
    /// for the common case and stays silent until the Gateway has actually answered.</summary>
    public bool HasRoleGlyph => RoleGlyphFor(ResolvedRole).Length > 0;

    /// <summary>The single-letter, non-color role glyph ("M"/"W"/"A") or "" for Standalone/unknown.</summary>
    public string RoleGlyphText => RoleGlyphFor(ResolvedRole);

    /// <summary>The full role name for the badge tooltip ("Manager"/"Worker"/"Architect"), or "" when
    /// there is no glyph to explain - Standalone, an unknown role, or no Gateway stamp yet. Written as a
    /// pattern match rather than gating on <see cref="HasRoleGlyph"/> and silencing the nullable warning:
    /// the two read the same source, so this states the "a glyph implies a role to name" invariant in a
    /// form the compiler checks, instead of asserting it with a null-forgiving operator.</summary>
    public string RoleTooltip => ResolvedRole is { } role && RoleGlyphFor(role).Length > 0
        ? role
        : string.Empty;

    /// <summary>Pure role -> single-letter glyph mapping. Static so it can be unit-tested without a
    /// live <see cref="Session"/>, mirroring <see cref="LabelFor"/> / <see cref="BadgeBrushFor"/>.
    /// Manager -> "M", Worker -> "W", Architect -> "A"; Standalone and anything else -> "".</summary>
    public static string RoleGlyphFor(string? role) => role switch
    {
        SessionRoles.Manager => "M",
        SessionRoles.Worker => "W",
        SessionRoles.Architect => "A",
        _ => ""
    };

    // ===== The ownership tree (Session List Views, slice 2) =====
    //
    // THE RAIL DECIDES NONE OF THIS. Every value below is STAMPED by SessionRailTree.Project from the
    // ONE fold in CcDirector.Gateway.Contracts.SessionTree - the same fold the Cockpit and the phone
    // read through their TypeScript twin - and this view model only holds it for the binding to read.
    // There is deliberately no computation here: a count worked out on the desktop is a second answer
    // to a question the fold already answers, which is the defect this mission exists to remove.

    private static readonly global::Avalonia.Media.ISolidColorBrush SectionNeedsYouBrush =
        new SolidColorBrush(Color.Parse(StatusPalette.Red));
    private static readonly global::Avalonia.Media.ISolidColorBrush SectionOrdinaryBrush =
        new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));

    private static readonly IReadOnlyList<ISolidColorBrush> NoCrew = Array.Empty<ISolidColorBrush>();

    private int _railDepth;
    private bool _hasCrew;
    private bool _isCrewExpanded;
    private string _crewLineText = "";
    private string _crewAgeText = "";
    private IReadOnlyList<ISolidColorBrush> _crewColorBrushes = NoCrew;
    private string _sectionHeaderText = "";
    private bool _sectionIsNeedsYou;

    /// <summary>How deep this row sits in the ownership tree: 0 at the top level, 1 for a child, and up
    /// with no limit. Drives the row's indent and its guide line.</summary>
    public int RailDepth => _railDepth;

    /// <summary>The width of this row's indent in pixels, so a grandchild sits further in than a child.</summary>
    public double RailIndentWidth => _railDepth * 18.0;

    /// <summary>True for any row under a parent - the row that draws the vertical guide line.</summary>
    public bool IsRailChild => _railDepth > 0;

    /// <summary>True when this session supervises at least one other session in the rail: it gets a chevron.</summary>
    public bool HasCrew => _hasCrew;

    /// <summary>True when this crew is open and its sessions are rows of their own below it.</summary>
    public bool IsCrewExpanded => _isCrewExpanded;

    /// <summary>The crew line rides under the state line while the crew is CLOSED - it is what stops a
    /// collapsed crew hiding anything, so it is absent exactly when the sessions themselves are visible.</summary>
    public bool ShowCrewLine => _hasCrew && !_isCrewExpanded;

    /// <summary>The crew line's words, formatted once by the shared fold: "9 under it: 4 working, 5 stopped,
    /// 0 need you". Never composed here.</summary>
    public string CrewLineText => _crewLineText;

    /// <summary>How long the crew has been going, from its oldest session ("5h 29m"), ticking on the rail's
    /// existing timer. Empty when no session under it carries a usable creation stamp.</summary>
    public string CrewAgeText => _crewAgeText;

    /// <summary>One square per session under this one, at EVERY level, in the crew's own order, each in that
    /// session's own stamped colour - so collapsing a crew hides no colour.</summary>
    public IReadOnlyList<ISolidColorBrush> CrewColorBrushes => _crewColorBrushes;

    /// <summary>True on the first row of an attention section, which draws its heading above itself.</summary>
    public bool ShowSectionHeader => _sectionHeaderText.Length > 0;

    /// <summary>The attention section heading, e.g. "NEEDS YOU 2". Empty in my order, which has no sections.</summary>
    public string SectionHeaderText => _sectionHeaderText;

    /// <summary>The heading's colour: red for needs you, muted for the rest.</summary>
    public ISolidColorBrush SectionHeaderBrush => _sectionIsNeedsYou ? SectionNeedsYouBrush : SectionOrdinaryBrush;

    /// <summary>
    /// Take this row's place in the tree from the projection. ONE entry point, raising every dependent
    /// property, for the same reason <see cref="RaiseFoldProjection"/> exists: a per-caller list of what
    /// to raise is a private chance to miss one, and a half-updated row looks deliberate, so the reader
    /// believes the wrong half.
    ///
    /// RAISES ONLY WHAT CHANGED. RebuildRail stamps every row every fifteen seconds and on every roster
    /// change, and almost every stamp is the same as the last one. Each incoming value is compared with the
    /// stored one and only a property whose value moved is raised, together with everything derived from it.
    /// The list of properties is still written once, here; the compare only makes the unchanged raises free.
    /// The crew squares compare by the brush each square holds, which is the one shared palette brush for
    /// its colour, so the same colours in the same order are the same squares.
    /// </summary>
    internal void ApplyRailRow(int depth, bool hasCrew, bool isExpanded, string crewLine, string crewAge,
                               IReadOnlyList<ISolidColorBrush> crewColors, string sectionHeader, bool sectionIsNeedsYou)
    {
        var depthChanged = _railDepth != depth;
        var hasCrewChanged = _hasCrew != hasCrew;
        var expandedChanged = _isCrewExpanded != isExpanded;
        var crewLineChanged = !string.Equals(_crewLineText, crewLine, StringComparison.Ordinal);
        var crewAgeChanged = !string.Equals(_crewAgeText, crewAge, StringComparison.Ordinal);
        var crewColorsChanged = !_crewColorBrushes.SequenceEqual(crewColors);
        var headerChanged = !string.Equals(_sectionHeaderText, sectionHeader, StringComparison.Ordinal);
        var headerNeedsYouChanged = _sectionIsNeedsYou != sectionIsNeedsYou;

        _railDepth = depth;
        _hasCrew = hasCrew;
        _isCrewExpanded = isExpanded;
        _crewLineText = crewLine;
        _crewAgeText = crewAge;
        _crewColorBrushes = crewColors;
        _sectionHeaderText = sectionHeader;
        _sectionIsNeedsYou = sectionIsNeedsYou;

        if (depthChanged)
        {
            OnPropertyChanged(nameof(RailDepth));
            OnPropertyChanged(nameof(RailIndentWidth));
            OnPropertyChanged(nameof(IsRailChild));
        }
        if (hasCrewChanged) OnPropertyChanged(nameof(HasCrew));
        if (expandedChanged) OnPropertyChanged(nameof(IsCrewExpanded));
        if (hasCrewChanged || expandedChanged) OnPropertyChanged(nameof(ShowCrewLine));
        if (crewLineChanged) OnPropertyChanged(nameof(CrewLineText));
        if (crewAgeChanged) OnPropertyChanged(nameof(CrewAgeText));
        if (crewColorsChanged) OnPropertyChanged(nameof(CrewColorBrushes));
        if (headerChanged)
        {
            OnPropertyChanged(nameof(ShowSectionHeader));
            OnPropertyChanged(nameof(SectionHeaderText));
        }
        if (headerNeedsYouChanged) OnPropertyChanged(nameof(SectionHeaderBrush));
    }

    // ===== Group membership (issue #225) =====

    private static readonly ISolidColorBrush GroupAccentBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x6D, 0xA3));

    /// <summary>The group this session belongs to (issue #225), or null when solo. Set by
    /// MainWindow after construction and on reorder so the bracket/header reflow.</summary>
    public Guid? GroupId => Session.GroupId;

    /// <summary>True when this session is a group member - drives all group visuals.</summary>
    public bool IsGroupMember => Session.GroupId is not null;

    private bool _isGroupFirst;
    private bool _isGroupLast;

    /// <summary>True for the TOP member of its group: renders the group header + top bracket.</summary>
    public bool IsGroupFirst
    {
        get => _isGroupFirst;
        set { if (_isGroupFirst != value) { _isGroupFirst = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowGroupHeader)); } }
    }

    /// <summary>True for the BOTTOM member of its group: renders the bottom bracket.</summary>
    public bool IsGroupLast
    {
        get => _isGroupLast;
        set { if (_isGroupLast != value) { _isGroupLast = value; OnPropertyChanged(); } }
    }

    /// <summary>The group header ("PRODUCT GROUP ...") renders above the first member only.</summary>
    public bool ShowGroupHeader => IsGroupMember && IsGroupFirst;

    /// <summary>Header label, e.g. "PRODUCT GROUP" - on the first member only.</summary>
    public string GroupHeaderText =>
        string.IsNullOrWhiteSpace(Session.GroupName) ? "GROUP" : Session.GroupName.ToUpperInvariant() + " GROUP";

    /// <summary>The brush for the group's left accent stripe + bracket.</summary>
    public ISolidColorBrush GroupAccent => GroupAccentBrush;

    private static readonly ISolidColorBrush GroupRowTintBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x30));

    /// <summary>Subtle tint behind a group member's row (transparent for solo sessions),
    /// binding the members visually together.</summary>
    public IBrush GroupRowBackground => IsGroupMember ? GroupRowTintBrush : Brushes.Transparent;

    public string RepoPath => Session.RepoPath;

    /// <summary>
    /// How many files are changed in this session's working tree, for the rail's amber "N chg" badge.
    /// READ STRAIGHT OFF THE SESSION - this view model no longer keeps a copy, and the window no longer
    /// polls git for it. The count is produced once, on the Director, by <c>SessionGitStatusMonitor</c>,
    /// which is also what puts it on the wire for the Cockpit roster and the phone. Before that it was
    /// computed here and nowhere else, so the number existed only on this screen.
    ///
    /// Zero when the count is UNKNOWN (no successful probe yet) - and <see cref="HasUncommittedChanges"/>
    /// is false in that case, so the badge is absent rather than reading "0 chg". Unknown must never render
    /// as a verified-clean tree (issue 516).
    /// </summary>
    public int UncommittedCount => Session.UncommittedCount ?? 0;

    public bool HasUncommittedChanges => Session.UncommittedCount is > 0;

    private int _queueCount;
    public int QueueCount
    {
        get => _queueCount;
        set
        {
            if (_queueCount == value) return;
            _queueCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQueuedItems));
        }
    }

    public bool HasQueuedItems => _queueCount > 0;

    public void Rename(string? newName, string? color = null)
    {
        Session.CustomName = newName;
        if (color != null)
            Session.CustomColor = color;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CustomColor));
        OnPropertyChanged(nameof(HasCustomColor));
        OnPropertyChanged(nameof(CustomColorBrush));
        OnPropertyChanged(nameof(DragHandleBrush));
    }

    public bool IsVerified => Session.VerificationStatus == SessionVerificationStatus.Verified;

    public bool HasVerificationWarning =>
        Session.VerificationStatus is SessionVerificationStatus.FileNotFound
                                    or SessionVerificationStatus.Error
                                    or SessionVerificationStatus.ContentMismatch;

    public string VerificationStatusText => Session.VerificationStatus switch
    {
        SessionVerificationStatus.Verified => "Verified",
        SessionVerificationStatus.FileNotFound => "Session file not found",
        SessionVerificationStatus.NotLinked => "Waiting for Claude session ID...",
        SessionVerificationStatus.ContentMismatch => "Session content mismatch",
        SessionVerificationStatus.Error => "Verification error",
        _ => ""
    };

    public string? VerifiedFirstPrompt => Session.VerifiedFirstPrompt;

    public TerminalVerificationStatus TerminalVerificationStatus => Session.TerminalVerificationStatus;

    public string TerminalVerificationStatusText => Session.TerminalVerificationStatus switch
    {
        TerminalVerificationStatus.Waiting => "Waiting...",
        TerminalVerificationStatus.Potential => "Potential Match",
        TerminalVerificationStatus.Matched => "Matched",
        TerminalVerificationStatus.Failed => "Verification Failed",
        _ => ""
    };

    public bool ShowVerificationDot => Session.TerminalVerificationStatus is TerminalVerificationStatus.Waiting
                                                                           or TerminalVerificationStatus.Potential
                                                                           or TerminalVerificationStatus.Failed;

    private static readonly ISolidColorBrush VerificationWaitingBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly ISolidColorBrush VerificationPotentialBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly ISolidColorBrush VerificationFailedBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    public ISolidColorBrush VerificationDotBrush => Session.TerminalVerificationStatus switch
    {
        TerminalVerificationStatus.Waiting => VerificationWaitingBrush,
        TerminalVerificationStatus.Potential => VerificationPotentialBrush,
        TerminalVerificationStatus.Failed => VerificationFailedBrush,
        _ => VerificationWaitingBrush
    };

    private void OnQueueChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            QueueCount = Session.PromptQueue?.Count ?? 0;
        });
    }

    private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Raised three and missed the waiting timer: ActivityState drives EffectiveColor through
            // SessionOrdering.RawActivityColor, and HasWaitingDuration gates on that - so a red row with a
            // cached explain could go blue/grey/error and keep a visible "waiting Xm" until the 15s tick.
            RaiseFoldProjection();
        });
    }

    private void OnVerificationStatusChanged(SessionVerificationStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(HasVerificationWarning));
            OnPropertyChanged(nameof(VerificationStatusText));
            OnPropertyChanged(nameof(VerifiedFirstPrompt));
        });
    }

    private void OnTerminalVerificationStatusChanged(TerminalVerificationStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(TerminalVerificationStatus));
            OnPropertyChanged(nameof(TerminalVerificationStatusText));
            OnPropertyChanged(nameof(ShowVerificationDot));
            OnPropertyChanged(nameof(VerificationDotBrush));
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(HasVerificationWarning));
            OnPropertyChanged(nameof(VerificationStatusText));
        });
    }

    /// <summary>Refresh Claude metadata from sessions-index.json.</summary>
    public void RefreshClaudeMetadata()
    {
        Session.RefreshClaudeMetadata();
    }

    /// <summary>Notify UI that display properties may have changed.</summary>
    public void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CustomColor));
        OnPropertyChanged(nameof(HasCustomColor));
        OnPropertyChanged(nameof(CustomColorBrush));
        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(AgentBadgeBrush));
        // The model rides ON the agent badge, so anything that repaints the badge repaints both halves.
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(IsModelAbsent));
            OnPropertyChanged(nameof(ModelOpacity));
        OnPropertyChanged(nameof(AgentModelTooltip));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
