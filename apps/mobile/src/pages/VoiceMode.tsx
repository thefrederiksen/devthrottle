import { useCallback, useEffect, useRef, useState } from "react";
import { useLocation, useNavigate, useParams } from "react-router-dom";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";
import { buildSnoozeMenu } from "@devthrottle/client-core/settings/snoozeMenu";
import { useSnoozeOptions } from "@devthrottle/client-core/settings/snoozeOptions";
import { touchQueue } from "@devthrottle/client-core/voice/queueTouch";
import { formatClock, useVoiceMode } from "@devthrottle/client-core/voice/useVoiceMode";
import { DictationStatusStrip } from "@devthrottle/client-core/dictation/DictationStatusStrip";
import { SessionAppBar } from "../components/SessionAppBar";
import { useSessionManage } from "../components/useSessionManage";
import { ViewTabs } from "../components/ViewTabs";
import { WingmanRetryCountdown } from "@devthrottle/client-core/sessions/WingmanErrorLine";
import type { WingmanErrorDisplay } from "@devthrottle/client-core/sessions/wingmanError";

// Session Voice mode (issue #850): the hands-free Wingman narration screen, the third session view
// alongside Terminal (#817) and Chat (#811). This component is a THIN view - all of its state, the
// poll, the state-machine derivation, the playback handlers, and the clip management live in the
// shared useVoiceMode hook (packages/client-core/src/voice/useVoiceMode.ts), so the phone app and the
// Cockpit render the identical screen from one copy of the logic (issue #1213, plan phase 4). The
// only thing this file owns is the router wiring (session id + the roster's voice-mode seed) and the
// JSX; nothing here changes behavior from the pre-hoist page.

export function VoiceMode() {
  const { sessionId } = useParams<{ sessionId: string }>();

  // The roster hands the known voice-mode state on navigation (issue #1015), so the screen renders
  // the right state on the FIRST paint instead of flashing OFF while its first poll resolves. Read it
  // from the router here and pass it into the hook - the shared hook stays router-free.
  //
  // switchOn arrives from "Switch to voice mode" in the Chat/Terminal overflow menu (SessionAppBar):
  // that menu item navigates here and asks the hook to run its own onSwitchOn, so the one place that
  // knows how to enter voice mode stays the one place that does it.
  const location = useLocation();
  const navigate = useNavigate();
  const navState = location.state as {
    voiceMode?: boolean;
    switchOn?: boolean;
    // Voice-mode queue flow: the auto-speak queue navigated here by itself (read from the beginning),
    // and which roster tab to return to after Respond / Snooze.
    autoSpeak?: boolean;
    fromTab?: string;
  } | null;
  const seededVoiceOn = navState?.voiceMode;

  // Captured once at mount, like switchOn below: whether the auto-speak queue drove this entry.
  // The mount effect below consumes this one-shot command from the history entry, so a service-worker
  // reload cannot unexpectedly restart the same narration from the beginning.
  const [autoSpeakEntry] = useState(() => navState?.autoSpeak === true);

  // Ordinary Back preserves the roster lens the session was opened from. Completing a voice action
  // is different: Respond and Snooze always return to the Voice queue, replace this history entry,
  // and let Auto-speak select the next waiting session.
  const backTab = navState?.fromTab === "all" ? "all" : "voice";
  const goBackToList = useCallback(() => {
    navigate("/", { replace: true, state: { tab: "voice" } });
  }, [navigate]);

  // switchOn and autoSpeak are ONE-SHOT COMMANDS, so they are read once and then scrubbed off the
  // history entry.
  //
  // It cannot be read straight from location.state on every render: router state is persisted in the
  // history entry and survives a reload, while the hook's "already did it" guard is only a ref, which a
  // remount resets. So the command would fire AGAIN on any remount of this history entry - turn voice
  // on from the Chat menu, turn it off, let the service worker update reload the app (it does, on every
  // deploy), and voice would switch itself back on against your explicit wish. Caught in review of
  // #1631.
  //
  // useState's initializer captures the command exactly once, at mount; the effect then rewrites the
  // entry with both commands cleared, so any later remount reads spent commands and does nothing.
  const [autoSwitchOn] = useState(() => navState?.switchOn === true);
  useEffect(() => {
    if (navState?.switchOn !== true && navState?.autoSpeak !== true) return;
    navigate(location.pathname + location.search, {
      replace: true,
      state: { ...navState, switchOn: false, autoSpeak: false },
    });
    // Mount-only: this consumes the arriving command. Re-running it on navState changes would fight
    // the very rewrite it performs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const {
    voiceOn,
    speaking,
    voiceDisplay,
    pollDone,
    narrative,
    title,
    error,
    autoPlayBlocked,
    resumed,
    playing,
    pos,
    dur,
    enabling,
    regenerating,
    responding,
    setResponding,
    setPlaying,
    clipUrl,
    onSwitchOn,
    onSwitchOff,
    onGenerateNow,
    setAudioEl,
    onLoadedMeta,
    onTimeUpdate,
    onEndedAudio,
    onSeek,
    onRestart,
    onTogglePlay,
    onRespondSend,
    onRespondSendAudio,
    menuBlocked,
    clearMenuBlocked,
  } = useVoiceMode(sessionId, {
    seededVoiceOn,
    autoSwitchOn,
    // Voice-mode queue flow: dropping into a voice session always STARTS the narration by itself.
    // An auto-speak entry reads from the beginning (the queue reads every item out in full); a
    // manual tap resumes from where it left off (never rewinds a half-listened clip).
    autoPlayOnEntry: autoSpeakEntry ? "restart" : "resume",
  });

  // Dumb-renderer mapping: the Gateway's voiceDisplay verdict decides which card shows. The view only
  // maps its kind to a layout and renders its label / message / actions VERBATIM - it derives no state.
  // The one thing the phone still owns is `speaking` (it holds playable bytes right now), which takes
  // precedence so a listener is never interrupted. When the verdict is "ready" but the phone is not yet
  // speaking, the bytes are still downloading locally - a phone-local affordance, not a Gateway state.
  const vd = voiceDisplay;
  // A live session owns this one, so it is not the user's to be read aloud (the Gateway's verdict - see
  // VoiceDisplayFold.HeldDisplay). It is checked before every other branch and it suppresses the OFF card
  // below, because the off card's whole content is a "Switch to voice mode" button that cannot work here:
  // the enrolment sweep refuses a held session, so pressing it either fails or is undone. The banner says
  // voice mode narrates the sessions you own; this card says why this one is not one of them.
  const showHeld = vd?.kind === "held";
  const showDownloading = !showHeld && voiceOn && !speaking && vd?.kind === "ready";
  const showBusy = !showHeld && voiceOn && !speaking && (vd?.kind === "preparing" || vd?.kind === "working");
  const showStatus =
    !showHeld && voiceOn && !speaking && vd != null &&
    vd.kind !== "ready" && vd.kind !== "preparing" && vd.kind !== "working" && vd.kind !== "off";

  // Snooze and Remove share this one live held-state, so the action buttons and the overflow menu
  // can never disagree about it.
  const manage = useSessionManage(sessionId);

  // A queue touch means LISTENED, not merely opened. Record the first real playback start so a hard
  // reload still remembers it, then refresh the stamp on leave so a long narration receives the full
  // cooldown and cannot immediately trap Auto-speak back in the same one-item queue.
  const listenedRef = useRef(false);
  const markListened = useCallback(() => {
    if (!sessionId) return;
    listenedRef.current = true;
    touchQueue(sessionId);
  }, [sessionId]);
  useEffect(() => {
    return () => {
      if (listenedRef.current && sessionId) touchQueue(sessionId);
    };
  }, [sessionId]);

  // Snooze, then BACK TO THE LIST: once the snooze is accepted there is nothing left to do on this
  // screen, so the flow returns to the queue (owner's flow, 2026-07-24). Unsnoozing stays put - that
  // is a "wake it back up and look at it" action, not a "done with it" action. On failure the screen
  // stays too, so the surfaced error is actually seen.
  const onSnoozeTap = useCallback(async () => {
    const wasSnoozed = manage.held || manage.deferred;
    const ok = await manage.toggleHold();
    if (ok && !wasSnoozed) goBackToList();
  }, [manage, goBackToList]);

  // THE SPLIT SNOOZE BUTTON (owner, 2026-07-25). The wide part is the button as it always was: one tap,
  // the user's DEFAULT length, back to the queue. The narrow part opens this sheet to snooze for a
  // length picked on the spot - the same Gateway-owned lengths the Cockpit's "Snooze for" flyout and the
  // desktop rail offer, through the same shared cache and the same buildSnoozeMenu rules, so all three
  // surfaces read identically.
  //
  // No lengths means NO caret: when this phone has never successfully read them from the Gateway,
  // buildSnoozeMenu returns no choices and only the plain Snooze shows. Inventing a plausible list would
  // be the one genuinely bad outcome - it would offer lengths that are not the user's.
  const snoozeOptions = useSnoozeOptions();
  const snoozeMenu = buildSnoozeMenu(manage.held || manage.deferred, snoozeOptions);
  const [pickingLength, setPickingLength] = useState(false);

  // Picking a length is ALWAYS a snooze, never an unsnooze - even while already snoozed, where it re-arms
  // the clock to the new length. So it always ends the same way the plain snooze does when it lands: back
  // to the queue. On failure the sheet closes but the screen stays, so the surfaced error is seen.
  const onPickLength = useCallback(async (minutes: number) => {
    setPickingLength(false);
    if (await manage.holdFor(minutes)) goBackToList();
  }, [manage, goBackToList]);

  // Respond sent, BACK TO THE LIST: the reply is cached and delivered in the background, so there is
  // no reason to sit on this session's page once it is answered - the queue has the next one. The
  // text path waits for the send to be accepted (stays put on failure, error visible); the audio
  // path is fire-and-forget by design and its progress/failures show on the roster rows.
  const onRespondText = useCallback(
    (text: string, spokenDeliveryId?: string) => {
      void (async () => {
        if (await onRespondSend(text, spokenDeliveryId)) goBackToList();
      })();
    },
    [onRespondSend, goBackToList],
  );

  return (
    <div className="terminal-screen">
      {/* Snooze is NOT in this menu - the bottom bar owns it on this screen (owner: "I press the
          snooze button a lot"), and a verb in two places is a verb that disagrees with itself. */}
      <SessionAppBar
        title={title}
        manage={manage}
        backState={{ tab: backTab }}
        extraMenuItems={
          voiceOn ? (
            <>
              {(speaking || vd?.canPlay) && (
                <button
                  type="button"
                  className="menu-item"
                  role="menuitem"
                  onClick={() => void onGenerateNow()}
                  disabled={regenerating}
                >
                  {regenerating ? "Regenerating..." : "Regenerate response"}
                </button>
              )}
              <button
                type="button"
                className="menu-item"
                role="menuitem"
                onClick={() => void onSwitchOff()}
              >
                Turn voice off
              </button>
            </>
          ) : undefined
        }
      />

      <ViewTabs sessionId={sessionId} active="voice" />

      {/* Live dictation status so a spoken reply Send is never silent (#1139). */}
      <DictationStatusStrip sessionId={sessionId} />

      {/* The clip element is always mounted (hidden) so auto-play works in any state; the visible
          play controls live in the speaking state below. */}
      <audio
        ref={setAudioEl}
        src={clipUrl ?? undefined}
        preload="auto"
        onLoadedMetadata={onLoadedMeta}
        onTimeUpdate={onTimeUpdate}
        onPlay={() => {
          setPlaying(true);
          markListened();
        }}
        onPause={() => setPlaying(false)}
        onEnded={onEndedAudio}
        style={{ display: "none" }}
      />

      {/* In the speaking state the audio controls are a FIXED header and only the narrative text
          scrolls, in its own window below them (voice-body-speaking). In the other states the body
          scrolls normally. */}
      <div className={`voice-body${speaking ? " voice-body-speaking" : ""}`}>
        {/* THE BUTTONS COME FIRST (owner's layout rule, 2026-07-24): in voice mode the buttons are
            the important part - the text below is a nice-to-have. Two big targets at the top of the
            screen, in the same place in every state: Respond (only while there is a narration to
            answer, exactly when it was offered before) and Snooze, which also returns to the list -
            the arrow says so. */}
        <div className="voice-actions">
          {!showHeld && speaking && (
            <button type="button" className="voice-action-respond" onClick={() => setResponding(true)}>
              Respond
            </button>
          )}
          {/* One slab, two targets. The wide part snoozes for the default length; the narrow part opens
              the length picker. They are separate buttons rather than one button that guesses from where
              the thumb landed, so each has its own hit box and its own accessible name. */}
          <div className="voice-snooze-split">
            <button
              type="button"
              className="voice-action-snooze"
              onClick={() => void onSnoozeTap()}
              disabled={manage.busy || manage.onHold === null}
            >
              {manage.held || manage.deferred ? (
                "Unsnooze"
              ) : (
                <>
                  Snooze
                  <span className="voice-back-arrow" aria-hidden="true" />
                </>
              )}
            </button>
            {snoozeMenu.choices.length > 0 && (
              <button
                type="button"
                className="voice-snooze-more"
                onClick={() => setPickingLength(true)}
                disabled={manage.busy || manage.onHold === null}
                aria-haspopup="dialog"
                aria-label="Snooze for a different length"
              >
                <span className="voice-snooze-caret" aria-hidden="true" />
              </button>
            )}
          </div>
        </div>

        {error !== null && (
          <div className="banner banner-error" role="alert">{error}</div>
        )}

        {/* A menu owns this session's screen, so the last spoken reply was NOT typed (issue #2193).
            Nothing was sent and no Enter was pressed - a spoken sentence typed into a chooser does
            nothing, and the trailing Enter would have confirmed whichever option was highlighted. The
            wingman says this out loud too; the banner is what stays on screen afterwards. */}
        {menuBlocked !== null && (
          <div className="banner banner-menu banner-action" role="alert">
            <span>{menuBlocked}</span>
            <button type="button" className="banner-btn" onClick={clearMenuBlocked}>
              Dismiss
            </button>
          </div>
        )}

        {autoPlayBlocked && (
          <div className="banner" role="status">
            Automatic playback was blocked. Tap play to continue.
          </div>
        )}

        {/* TTS fallback: the Gateway folded a generic "switched to a backup voice" notice onto this turn's
            ready clip (the primary provider was temporarily overloaded). Rendered VERBATIM - the client
            derives nothing and never names a provider. Shows above whichever voice card is up, for as long
            as the backup clip is the current one (downloading, then speaking). */}
        {vd?.voiceFallbackNotice != null && vd.voiceFallbackNotice !== "" && (
          <div className="banner voice-fallback-note" role="status">{vd.voiceFallbackNotice}</div>
        )}

        {/* A0. HELD - a live session owns this one, so it is read by that owner and never narrated to you.
            The Gateway's own verdict, rendered verbatim, and it offers NOTHING: no play, no Generate, and
            above all no "Switch to voice mode". That button was the whole complaint - twelve sessions on a
            twenty-session fleet drew it under a banner promising every session narrates, and pressing it
            either 503'd with the failure wiped off the screen by the next poll, or briefly succeeded and was
            undone by the enrolment sweep. An action that cannot succeed is worse than no action. */}
        {showHeld && vd && (
          <div className="voice-off">
            <p className="voice-off-title">{vd.label}</p>
            <p className="voice-hint">{vd.message}</p>
          </div>
        )}

        {/* A. OFF - one clear "Switch to voice mode" button. Only ever shown once a poll has confirmed
            the session is NOT in voice mode, so the screen never flashes OFF then flips (issue #1015). */}
        {!showHeld && !voiceOn && pollDone && (
          <div className="voice-off">
            <p className="voice-off-title">Voice mode is off for this session.</p>
            <p className="voice-hint">Turn it on and the Wingman will start narrating every turn.</p>
            <button type="button" className="voice-switch" onClick={onSwitchOn} disabled={enabling}>
              {enabling ? "Switching..." : "Switch to voice mode"}
            </button>
          </div>
        )}

        {/* THE GATEWAY VERDICT, rendered verbatim. This screen used to RULE for itself here - deriving
            "is the audio unavailable" from nine local inputs and branching on retrying / service-down /
            reason to pick the badge, the message, and whether a Generate button appeared. That guessing
            is what put a dead-end "Generate narration now" button next to a red "Voice unavailable" badge
            when the session was simply waiting on a prompt. Now the Gateway folds one voiceDisplay verdict
            (see VoiceDisplayFold) and this renders it: label, tone, message, and a Generate button ONLY
            when the Gateway says one can help (canGenerate). The client derives nothing. */}

        {/* Busy: the agent is working, or the Wingman is preparing this turn's narration. Both come from
            the Gateway (kind = working / preparing); the phone only shows the spinner + its words. */}
        {showBusy && vd && (
          <>
            <div className="voice-statusbar">
              <span className={"voice-state voice-state-" + vd.tone}>{vd.label}</span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-body">{vd.message}</div>
            </div>
            <div className="voice-working">
              <span className="voice-spinner" aria-hidden="true" />
              <span className="voice-ref">{vd.kind === "working" ? "working" : "rendering audio"}</span>
            </div>
          </>
        )}

        {/* Downloading: the Gateway has the audio (kind = ready) but this phone has not pulled the bytes
            down yet. That last hop is the phone's own, so the "downloading" words are the phone's - the
            verdict (there IS audio) is still the Gateway's. */}
        {showDownloading && (
          <>
            <div className="voice-statusbar">
              <span className="voice-state voice-state-yellow">Voice on its way</span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-body">
                Downloading the spoken audio to your phone. It will play automatically.
              </div>
            </div>
            <div className="voice-working">
              <span className="voice-spinner" aria-hidden="true" />
              <span className="voice-ref">downloading</span>
            </div>
          </>
        )}

        {/* Every other verdict - retrying, service down, blocked (credits / key), nothing to narrate, or
            not-made-yet. One uniform card: the Gateway's tone + label + message, and a Generate button
            ONLY when the Gateway says it can help. No dead-end button, ever. */}
        {showStatus && vd && (
          <>
            <div className="voice-statusbar">
              <span className={"voice-state voice-state-" + vd.tone}>{vd.label}</span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-body">{vd.message}</div>
            </div>
            {/* A Wingman error says WHEN the booked retry runs - the shared countdown, the same as on the card. */}
            <WingmanRetryCountdown error={(vd as { wingmanError?: WingmanErrorDisplay | null }).wingmanError} />
            {vd.canGenerate && (
              <button
                type="button"
                className="voice-switch"
                onClick={() => void onGenerateNow()}
                disabled={regenerating}
              >
                {regenerating ? "Generating narration..." : "Generate narration now"}
              </button>
            )}
          </>
        )}

        {/* C. SPEAKING - the audio bar and Respond are a FIXED header at the TOP (the controls you
            actually use in voice mode); the response text sits BELOW and scrolls inside its own window.
            In voice mode the text is a nice-to-have, so a long response must never push the controls
            off-screen and must never scroll up behind them - the body is a fixed-header + scrolling
            narrative column, not one big scroll (issue #1003, voice-mode screen layout). */}
        {!showHeld && speaking && (
          <>
            <div className="voice-top">
              <div className="voice-statusbar">
                <span className="voice-state voice-state-green">Speaking</span>
                {resumed && <span className="voice-ref">picked up where you left off</span>}
              </div>
              <div className="voice-player">
                <button
                  type="button"
                  className="voice-tri-btn"
                  onClick={onTogglePlay}
                  aria-label={playing ? "Pause" : "Play"}
                >
                  <span className={playing ? "voice-pause" : "voice-tri"} aria-hidden="true" />
                </button>
                <button
                  type="button"
                  className="voice-restart-btn"
                  onClick={onRestart}
                  aria-label="Restart from the beginning"
                >
                  <span className="voice-restart" aria-hidden="true" />
                </button>
                <input
                  type="range"
                  className="voice-seek"
                  min={0}
                  max={dur > 0 ? dur : 0}
                  step="any"
                  value={dur > 0 ? Math.min(pos, dur) : 0}
                  onChange={onSeek}
                  aria-label="Seek through the narration"
                />
                <span className="voice-ref voice-clock">{formatClock(pos)} / {formatClock(dur)}</span>
              </div>
            </div>
            <div className="voice-narr voice-narr-scroll">
              <div className="voice-narr-title">{narrative}</div>
              <div className="voice-narr-body">
                {playing ? "Speaking. Tap pause to stop, or Respond to answer." : "Tap play to listen, or Respond to answer."}
              </div>
            </div>
          </>
        )}

        {/* Regenerate response and Turn voice off used to sit here as full-width buttons below the
            text. Both are occasional, so they moved into the app bar's overflow menu (above) and the
            bottom of the screen now belongs to the two controls actually used every turn. The verbs
            themselves are unchanged: the same onGenerateNow (/wingman/explain force path) and
            onSwitchOff (ViewMode -> Text) calls. */}
      </div>

      {/* The old bottom bar is gone: Respond and Snooze moved to the TOP of the body as the big
          voice-actions block (owner's layout rule - the buttons are the important part). */}

      {/* The snooze length picker: a bottom-anchored sheet, so every row sits in thumb reach on a screen
          used one-handed - the lengths are the reason it opened, and they must not be at the top of the
          phone. The rows are buildSnoozeMenu's, rendered VERBATIM, including which one it marks default. */}
      {pickingLength && (
        <div
          className="snooze-sheet-overlay"
          role="dialog"
          aria-modal="true"
          aria-label="Snooze for"
          onClick={() => setPickingLength(false)}
        >
          <div className="snooze-sheet" onClick={(e) => e.stopPropagation()}>
            <h2 className="snooze-sheet-title">Snooze for</h2>
            {snoozeMenu.choices.map((choice) => (
              <button
                key={choice.minutes}
                type="button"
                className="snooze-sheet-choice"
                onClick={() => void onPickLength(choice.minutes)}
                disabled={manage.busy}
              >
                {choice.header}
              </button>
            ))}
            <button
              type="button"
              className="snooze-sheet-cancel"
              onClick={() => setPickingLength(false)}
            >
              Cancel
            </button>
          </div>
        </div>
      )}

      {/* F. Reply: the shared dictation interface with NO Insert - Send goes straight into the
          session. There is no hold-to-talk; you tap Respond, speak, then Send. After a successful
          send the screen returns to the session list - the reply is cached and delivered in the
          background, so the queue is the next stop, not this page. */}
      {responding && (
        <DictationDialog
          surface="mobile"
          showInsert={false}
          onSend={onRespondText}
          onSendAudio={(captured) => {
            onRespondSendAudio(captured);
            goBackToList();
          }}
          onClose={() => setResponding(false)}
        />
      )}
    </div>
  );
}
