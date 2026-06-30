import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  getWingmanMenu,
  getWingmanVoice,
  listSessions,
  markVoiceAndExplain,
  pressWingmanMenu,
  sendPrompt,
  setVoiceMode,
  stopWingmanVoice,
  type SessionDto,
  type WingmanMenu,
  type WingmanMenuOption,
  type WingmanVoice,
} from "../api/client";
import { DictationDialog } from "../dictation/DictationDialog";
import { SessionManageBar } from "../components/SessionManageBar";
import { ViewTabs } from "../components/ViewTabs";
import { ensureClip, getClipState, useVoiceClips } from "../voice/clips";

// Session Voice mode (issue #850): the hands-free Wingman narration screen, the third session view
// alongside Terminal (#817) and Chat (#811). A read-only Wingman narrates every completed turn as
// audio; the Gateway renders + caches the clip at turn-end (proven path, GatewayHost turn-end
// watcher), and this screen downloads it to the phone before offering playback - the one new rule
// is "phone-ready, not just gateway-ready" (see voice/clips.ts and docs/architecture/mobile/voice-mode.html).
//
// Screen states (mockups A-D, F): off (one "Switch to voice mode" button), working (Wingman reading
// + phone downloading), speaking (clip plays, narrative shown, Respond), and waiting-on-choice (the
// agent is waiting on options rendered as buttons). Reply is the existing dictation interface with
// Cancel / Pause / Send and NO Insert (mockup F); Send transcribes and goes straight into the
// session via the same POST /prompt path. A future "Talk to the Wingman" reply target is out of
// scope; the Respond control leaves room for it but does not build it.

const VOICE_POLL_MS = 3000; // a slightly faster poll than the 5s roster, only while this screen is open

function isWaiting(s: SessionDto | null): boolean {
  if (s === null) return false;
  const state = s.assessedState ?? s.activityState ?? "";
  return state === "WaitingForInput" || state === "WaitingForPerm";
}

// mm:ss for the playback clock (display only).
function formatClock(seconds: number): string {
  if (!isFinite(seconds) || seconds < 0) return "0:00";
  const total = Math.floor(seconds);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${String(s).padStart(2, "0")}`;
}

export function VoiceMode() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const sid = sessionId ?? "";

  const [name, setName] = useState<string | null>(null);
  const [session, setSession] = useState<SessionDto | null>(null);
  const [voice, setVoice] = useState<WingmanVoice | null>(null);
  const [menu, setMenu] = useState<WingmanMenu | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Optimistic "this session is now a voice session" the instant Switch is tapped, so the screen
  // moves to the working state immediately (responsive UI) before the roster reflects voiceMode.
  const [localEnabled, setLocalEnabled] = useState(false);
  const localEnabledRef = useRef(false);
  useEffect(() => {
    localEnabledRef.current = localEnabled;
  }, [localEnabled]);

  const [enabling, setEnabling] = useState(false);
  const [enableNote, setEnableNote] = useState<string>(""); // "nothing to summarize yet" message
  const [responding, setResponding] = useState(false);
  const [selected, setSelected] = useState<Set<number>>(new Set()); // multi-select choices

  // Re-render this screen when a clip download completes (phone-ready).
  useVoiceClips();
  const clip = getClipState(sid);

  // ----- playback element + progress (display only) -----
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const autoPlayedRef = useRef<string>(""); // the generatedAt we have already auto-played
  const [pos, setPos] = useState(0);
  const [dur, setDur] = useState(0);

  // ----- the single poll that drives every state (session flags + voice + menu) -----
  const poll = useCallback(
    async (signal: AbortSignal) => {
      try {
        const all = await listSessions(signal);
        const match = all.find((s) => s.sessionId === sid) ?? null;
        setSession(match);
        if (match?.name && match.name.trim()) setName(match.name.trim());

        const on = localEnabledRef.current || Boolean(match?.voiceMode);
        if (!on) {
          setVoice(null);
          setMenu(null);
          setError(null);
          return;
        }

        const v = await getWingmanVoice(sid, signal);
        setVoice(v);
        // Kick the phone-side download the moment a (new) clip is ready on the Gateway.
        if (v.ready && v.generatedAt) void ensureClip(sid, v.generatedAt);

        // Only ask the Wingman for the on-screen menu when the agent is actually waiting - this is
        // the cheap gate the Gateway uses too, so a working agent never triggers a menu extraction.
        if (isWaiting(match)) {
          const m = await getWingmanMenu(sid, signal);
          setMenu(m.isMenu && m.options.length > 0 ? m : null);
        } else {
          setMenu(null);
        }
        setError(null);
      } catch (err) {
        if (signal.aborted) return;
        // Background poll: keep the last-known view on screen and surface a soft note; the next tick
        // retries. (Mirrors the roster's keep-last-known behavior - not a degraded fallback.)
        setError(err instanceof Error ? err.message : "Voice update failed");
      }
    },
    [sid],
  );

  useEffect(() => {
    const controller = new AbortController();
    void poll(controller.signal);
    const timer = window.setInterval(() => void poll(controller.signal), VOICE_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [poll]);

  // Reset multi-select when the waiting choice changes (or clears).
  useEffect(() => {
    setSelected(new Set());
  }, [menu?.question, menu?.options.length]);

  const generatedAt = voice?.generatedAt ?? "";
  const phoneReady =
    generatedAt.length > 0 && clip.phase === "ready" && clip.generatedAt === generatedAt && clip.url !== null;

  // Auto-play a freshly downloaded clip exactly once, and only while the voice screen is foreground
  // (decision 4: never auto-play from the list, never while the app is hidden).
  useEffect(() => {
    if (!phoneReady || generatedAt.length === 0) return;
    if (autoPlayedRef.current === generatedAt) return;
    if (typeof document !== "undefined" && document.hidden) return;
    autoPlayedRef.current = generatedAt;
    const el = audioRef.current;
    if (el) {
      el.currentTime = 0;
      void el.play().catch(() => {
        /* autoplay policy may require a gesture; the play-triangle covers it */
      });
    }
  }, [phoneReady, generatedAt]);

  const onSwitchOn = useCallback(async () => {
    if (sid.length === 0 || enabling) return;
    setEnabling(true);
    setError(null);
    setLocalEnabled(true); // show the working screen immediately (responsive UI)
    try {
      // Two steps, matching the native phone app's enter-voice flow: first mark the session a Voice
      // session on the owning Director (ViewMode=Voice) so SessionDto.VoiceMode flips true and the
      // state persists across navigation and shows on the roster; then explain on the Gateway, which
      // marks its turn-end re-narration set and reads the first turn (caching the spoken text + audio).
      await setVoiceMode(sid, true);
      const explained = await markVoiceAndExplain(sid);
      // A fresh/text-only session has nothing to read yet - show its truthful note in the working
      // card instead of spinning forever waiting for audio that will not come until the next turn.
      setEnableNote(explained.nothingYet ? explained.spoken : "");
    } catch (err) {
      setLocalEnabled(false); // the enable did not take - fall back to the off screen, no half state
      setError(err instanceof Error ? err.message : "Could not switch to voice mode");
    } finally {
      setEnabling(false);
    }
  }, [sid, enabling]);

  const onSwitchOff = useCallback(async () => {
    if (sid.length === 0) return;
    // Revert the screen to off immediately (responsive UI), then tell the owning Director to leave
    // voice (ViewMode=Text) - the same call the native app's ClearVoiceMode makes. The optimistic
    // session edit flips voiceOn false now; the next poll confirms it.
    setLocalEnabled(false);
    setVoice(null);
    setMenu(null);
    setEnableNote("");
    setSession((prev) => (prev ? { ...prev, voiceMode: false } : prev));
    autoPlayedRef.current = "";
    try {
      // Two calls, matching the on path's two: tell the Director to leave voice (roster flag) AND
      // tell the Gateway to stop keeping voice (stops the per-turn Opus + text-to-speech, issue #859).
      await setVoiceMode(sid, false);
      await stopWingmanVoice(sid);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not turn voice off");
    }
  }, [sid]);

  const onReplay = useCallback(() => {
    const el = audioRef.current;
    if (!el) return;
    el.currentTime = 0;
    void el.play().catch(() => {
      /* ignore - a tap already gestured */
    });
  }, []);

  const onRespondSend = useCallback(
    async (text: string) => {
      setResponding(false);
      const trimmed = text.trim();
      if (sid.length === 0 || trimmed.length === 0) return;
      try {
        // Same write path the Send button uses; the transcript is already dictionary-corrected by
        // the Gateway and is sent verbatim (transcript integrity, CodingStyle s16).
        await sendPrompt(sid, trimmed, true);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Send failed");
      }
    },
    [sid],
  );

  const onPressSingle = useCallback(
    async (opt: WingmanMenuOption) => {
      if (sid.length === 0) return;
      try {
        await pressWingmanMenu(sid, opt.send);
        setMenu(null); // optimistic; the next narration replaces it
      } catch (err) {
        setError(err instanceof Error ? err.message : "Could not select that option");
      }
    },
    [sid],
  );

  const onSubmitMultiple = useCallback(async () => {
    if (sid.length === 0 || menu === null) return;
    const chosen = menu.options.filter((_, i) => selected.has(i));
    if (chosen.length === 0) return;
    // Multiple-select: each option's send is just its toggle keystroke; press every chosen toggle
    // then the menu's submit keystroke completes the selection.
    const send = chosen.map((o) => o.send).join("");
    const submit = menu.submit.length > 0 ? menu.submit : "\r";
    try {
      await pressWingmanMenu(sid, send, submit);
      setMenu(null);
      setSelected(new Set());
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not submit your selection");
    }
  }, [sid, menu, selected]);

  const toggleSelected = useCallback((index: number) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(index)) next.delete(index);
      else next.add(index);
      return next;
    });
  }, []);

  const voiceOn = localEnabled || Boolean(session?.voiceMode);
  const waitingChoice = voiceOn && menu !== null;
  const speaking = voiceOn && !waitingChoice && phoneReady && (voice?.spoken.length ?? 0) > 0;
  const working = voiceOn && !waitingChoice && !speaking;
  const progress = dur > 0 ? Math.min(1, pos / dur) : 0;
  const narrative = voice?.spoken ?? "";

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1 className="term-title">{name ?? "Session"}</h1>
      </header>

      <ViewTabs sessionId={sessionId} active="voice" />

      <SessionManageBar sessionId={sessionId} />

      {/* The clip element is always mounted (hidden) so auto-play works in any state; the visible
          play controls live in the speaking state below. */}
      <audio
        ref={audioRef}
        src={clip.url ?? undefined}
        preload="auto"
        onLoadedMetadata={(e) => setDur(e.currentTarget.duration)}
        onTimeUpdate={(e) => setPos(e.currentTarget.currentTime)}
        onEnded={(e) => setPos(e.currentTarget.duration)}
        style={{ display: "none" }}
      />

      <div className="voice-body">
        {error !== null && (
          <div className="banner banner-error" role="alert">{error}</div>
        )}

        {/* A. OFF - one clear "Switch to voice mode" button. */}
        {!voiceOn && (
          <div className="voice-off">
            <p className="voice-off-title">Voice mode is off for this session.</p>
            <p className="voice-hint">Turn it on and the Wingman will start narrating every turn.</p>
            <button type="button" className="voice-switch" onClick={onSwitchOn} disabled={enabling}>
              {enabling ? "Switching..." : "Switch to voice mode"}
            </button>
          </div>
        )}

        {/* B. WORKING - the Wingman is reading and the phone is downloading the clip. */}
        {working && (
          <>
            <div className="voice-statusbar">
              <span className="voice-state voice-state-yellow">Wingman is reading...</span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-title">Listening</div>
              <div className="voice-narr-body">
                {enableNote.length > 0
                  ? enableNote
                  : "Preparing the spoken summary of the latest turn. This will play automatically."}
              </div>
            </div>
            {enableNote.length === 0 && (
              <div className="voice-working">
                <span className="voice-spinner" aria-hidden="true" />
                <span className="voice-ref">rendering audio + downloading</span>
              </div>
            )}
          </>
        )}

        {/* C. SPEAKING - the clip plays; the narrative is shown; replay or dictate a reply. */}
        {speaking && (
          <>
            <div className="voice-statusbar">
              <span className="voice-state voice-state-green">Speaking</span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-title">{narrative}</div>
              <div className="voice-narr-body">Tap to replay, or just answer below.</div>
            </div>
            <div className="voice-player">
              <button type="button" className="voice-tri-btn" onClick={onReplay} aria-label="Replay">
                <span className="voice-tri" aria-hidden="true" />
              </button>
              <div className="voice-progress" aria-hidden="true">
                <div className="voice-progress-fill" style={{ width: `${progress * 100}%` }} />
              </div>
              <span className="voice-ref">{formatClock(pos)}</span>
            </div>
            <button type="button" className="voice-respond" onClick={() => setResponding(true)}>
              Respond
            </button>
          </>
        )}

        {/* D. WAITING ON CHOICE - options become buttons (single, or "pick any" with Submit). */}
        {waitingChoice && menu !== null && (
          <>
            <div className="voice-statusbar">
              <span className="voice-state voice-state-green">Waiting on you</span>
              <span className="voice-ref">
                {menu.selectionMode === "multiple" ? "pick any that apply" : "pick one"}
              </span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-title">{menu.spoken.length > 0 ? menu.spoken : narrative}</div>
              <div className="voice-narr-body">
                {menu.selectionMode === "multiple"
                  ? "Select the items to include, then submit."
                  : "Tap an option to choose it."}
              </div>
            </div>

            {menu.options.map((opt, i) => {
              const isSelected = selected.has(i);
              if (menu.selectionMode === "multiple") {
                return (
                  <button
                    type="button"
                    key={`${opt.key}-${i}`}
                    className={`voice-opt${opt.recommended ? " voice-opt-rec" : ""}${isSelected ? " voice-opt-selected" : ""}`}
                    onClick={() => toggleSelected(i)}
                    aria-pressed={isSelected}
                  >
                    <span className="voice-opt-key">{i + 1}</span>
                    <span className="voice-opt-label">{opt.key}</span>
                    {opt.recommended && <span className="voice-opt-tag">SUGGESTED</span>}
                  </button>
                );
              }
              return (
                <button
                  type="button"
                  key={`${opt.key}-${i}`}
                  className={`voice-opt${opt.recommended ? " voice-opt-rec" : ""}`}
                  onClick={() => void onPressSingle(opt)}
                >
                  <span className="voice-opt-key">{i + 1}</span>
                  <span className="voice-opt-label">{opt.key}</span>
                  {opt.recommended && <span className="voice-opt-tag">SUGGESTED</span>}
                </button>
              );
            })}

            {menu.selectionMode === "multiple" && (
              <button
                type="button"
                className="voice-submit"
                onClick={() => void onSubmitMultiple()}
                disabled={selected.size === 0}
              >
                Submit selection
              </button>
            )}
          </>
        )}

        {/* Turn voice off: a low-emphasis control present in every on-state (working, speaking,
            waiting-on-choice). It tells the owning Director to leave voice mode (ViewMode -> Text),
            the same call the native app makes, and the screen returns to the off state. */}
        {voiceOn && (
          <button type="button" className="voice-off-toggle" onClick={() => void onSwitchOff()}>
            Turn voice off
          </button>
        )}
      </div>

      {/* F. Reply: the shared dictation interface with NO Insert - Send goes straight into the
          session. There is no hold-to-talk; you tap Respond, speak, then Send. */}
      {responding && (
        <DictationDialog
          showInsert={false}
          onSend={(text) => void onRespondSend(text)}
          onClose={() => setResponding(false)}
        />
      )}
    </div>
  );
}
