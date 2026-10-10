import { useCallback, useEffect, useState } from "react";
import { getVoiceModeAllState, setVoiceModeAllSessions, type VoiceModeAllResult } from "../api/client";
import { setAutoSpeak } from "./queueTouch";
import { describeAndReport } from "../errors/reportClientError";

// VOICE MODE - the fleet-wide switch, read from the Gateway and never derived (owner, 2026-07-24).
//
// Voice mode means: every session on this Gateway narrates its turns, including sessions created after the
// switch was thrown. It is one thing. Auto-speak is a DIFFERENT thing - a setting of this phone that also
// opens each waiting voice session in turn and plays it without a tap. You can be in voice mode without
// auto-speak, and that is the ordinary case.
//
// ONE STATE FOR THE WHOLE APP, deliberately module-level. Two components read this at once - the banner in
// the app shell (on every screen) and the switch on the roster - and if each held its own copy with its own
// poll they would disagree for up to a poll interval: you would tap "Turn off" on the banner and the roster
// would go on saying voice mode was on, or turn it on from the roster and wait fifteen seconds for the
// banner to admit it. Two copies of one truth is how the old derived-from-the-roster answer went wrong in
// the first place; this keeps exactly one.
//
// The client is dumb by law (CLAUDE.md rule 7): this asks the Gateway what the state IS and renders that
// answer. It does not look at the roster and work it out.
const POLL_MS = 15000;

let current: boolean | null = null;
// The Gateway's quiet line about voice mode switching itself off (voice mode auto-off), read in the same poll as the
// state it sits beside. Rendered verbatim; never composed here.
let currentNote: string | null = null;
let writing = false;
// Each read remembers the write counter it started under. A read that started BEFORE a write and lands
// AFTER it has finished sees writing=false and would otherwise sail through carrying the pre-write answer -
// repainting "voice mode is on" a moment after you turned it off, which on screen is indistinguishable from
// the app refusing to let you leave. The in-flight flag alone does not catch that one.
let writeSeq = 0;
const listeners = new Set<(v: boolean | null) => void>();
const noteListeners = new Set<(note: string | null) => void>();
let pollTimer: ReturnType<typeof setInterval> | null = null;

function publish(v: boolean | null, note: string | null): void {
  current = v;
  currentNote = note;
  for (const fn of listeners) fn(v);
  for (const fn of noteListeners) fn(note);
}

async function readOnce(): Promise<void> {
  if (writing) return;
  const startedUnder = writeSeq;
  try {
    const state = await getVoiceModeAllState();
    if (!writing && writeSeq === startedUnder) publish(state.enabled, state.note);
  } catch {
    // A failed read leaves the last known answer standing rather than guessing a new one. An unreachable
    // Gateway is not evidence that voice mode was turned off.
  }
}

function subscribe(fn: (v: boolean | null) => void): () => void {
  listeners.add(fn);
  if (pollTimer === null) {
    void readOnce();
    pollTimer = setInterval(() => void readOnce(), POLL_MS);
  }
  return () => {
    listeners.delete(fn);
    if (listeners.size === 0 && pollTimer !== null) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  };
}

/**
 * THE ONE WAY TO THROW THE SWITCH. Every surface that turns voice mode on or off for the whole account goes through
 * here, so the shared state and the quiet line move together and a poll that started before the write can never land
 * after it and repaint the old answer - the old line coming back a moment after you switched voice on. The hook's
 * `set` uses it; so does the Cockpit's roster button, which needs the per-session result to report what changed.
 *
 * Switching it yourself, either way, leaves nothing for the line to say: on clears it at the Gateway, and an off you
 * pressed is not one the Gateway made. Turning it OFF also switches auto-speak off on this device.
 */
export async function writeVoiceModeAll(next: boolean): Promise<VoiceModeAllResult> {
  writing = true;
  writeSeq += 1;
  try {
    const result = await setVoiceModeAllSessions(next);
    publish(next, null);
    if (!next) setAutoSpeak(false);
    return result;
  } finally {
    writing = false;
  }
}

/** Test-only: forget the shared state between cases, so one test's answer cannot leak into the next. */
export function __resetVoiceModeAllForTests(): void {
  if (pollTimer !== null) clearInterval(pollTimer);
  pollTimer = null;
  listeners.clear();
  noteListeners.clear();
  current = null;
  currentNote = null;
  writing = false;
  writeSeq = 0;
}

export interface VoiceModeAll {
  /** Whether the fleet is in voice mode. Null until the first read lands - the banner must not flash. */
  enabled: boolean | null;
  /** The Gateway's line saying voice mode switched itself off, and when and why - or null. Render it verbatim. */
  note: string | null;
  /** A write is in flight. */
  busy: boolean;
  /** The last write failure, in plain English, or null. Never swallowed - a silent failure here means the
   *  person believes they left voice mode when they did not. */
  error: string | null;
  /** Turn the whole fleet's voice mode on or off. Turning it OFF also switches auto-speak off on this
   *  phone: auto-speak with nothing to speak is not a state worth being in, and leaving it armed is how
   *  someone ends up dragged back into a queue they just left. */
  set: (next: boolean) => Promise<boolean>;
}

export function useVoiceModeAll(): VoiceModeAll {
  const [enabled, setEnabled] = useState<boolean | null>(current);
  const [note, setNote] = useState<string | null>(currentNote);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => subscribe(setEnabled), []);
  useEffect(() => {
    noteListeners.add(setNote);
    return () => {
      noteListeners.delete(setNote);
    };
  }, []);

  const set = useCallback(async (next: boolean): Promise<boolean> => {
    setBusy(true);
    setError(null);
    try {
      await writeVoiceModeAll(next);
      return true;
    } catch (err) {
      setError(describeAndReport("voice-mode-all", next ? "turn voice mode on for all sessions" : "turn voice mode off for all sessions", err));
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  return { enabled, note, busy, error, set };
}
