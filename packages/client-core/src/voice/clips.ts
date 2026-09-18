// The phone-side download gate for Wingman voice clips (issue #850).
//
// The Gateway renders and caches a session's spoken narration as MP3 the instant a turn ends
// (gateway-ready). This module adds the one genuinely new rule of the mobile Voice mode: a clip is
// only "phone-ready" once its bytes have been DOWNLOADED TO THE PHONE and stored locally. The
// roster play-triangle and any auto-play gate on phone-ready, never on gateway-ready - so you never
// tap a play control and then sit waiting for a download.
//
// Design (open decision 2 in docs/architecture/mobile/voice-mode.html): Cache Storage is the
// durable backing, keyed by session id + the clip's generatedAt stamp, so phone-ready survives a
// reload. A small in-memory map mirrors it for reactive rendering and holds the playable object
// URL. The in-memory map is the authoritative "phone-ready" signal; Cache Storage is the durability
// layer beneath it (a cold start re-reads bytes from the cache with no network).

import { useEffect, useState } from "react";
import { fetchWingmanVoiceAudio, getWingmanVoice, type SessionDto, type WingmanVoice } from "../api/client";
import { type VoiceRowInputs } from "./voiceRowState";

export type ClipPhase = "none" | "downloading" | "ready" | "error";

export interface ClipState {
  /** The generatedAt stamp this state describes ("" when nothing is held yet). */
  generatedAt: string;
  phase: ClipPhase;
  /** A playable object URL for the locally-stored bytes, set only in the "ready" phase. */
  url: string | null;
}

const CACHE_NAME = "wingman-voice-v1";
const EMPTY: ClipState = { generatedAt: "", phase: "none", url: null };

// Per-session current-clip state. Only the latest clip per session is tracked; a newer turn
// supersedes the old one (its object URL is revoked and its cache entry evicted).
const _state = new Map<string, ClipState>();
// Guards against two concurrent downloads of the same (session, clip) - e.g. the roster poll and
// the open Voice screen both noticing the same new turn.
const _inflight = new Set<string>();
const _listeners = new Set<() => void>();

function notify(): void {
  for (const listener of [..._listeners]) listener();
}

function setState(sid: string, next: ClipState): void {
  _state.set(sid, next);
  notify();
}

/** The current clip state for a session (EMPTY when nothing is held). Safe to call during render. */
export function getClipState(sid: string): ClipState {
  return _state.get(sid) ?? EMPTY;
}

/** True only when the given clip's bytes are stored on the phone and ready to play with no wait. */
export function isPhoneReady(sid: string, generatedAt: string): boolean {
  const s = _state.get(sid);
  return s !== undefined && s.phase === "ready" && s.generatedAt === generatedAt && s.url !== null;
}

/**
 * The roster's voice verdict for a session, assembled from the two facts only this module can see (what
 * THIS PHONE holds, and which turn it holds it for) plus the four the Gateway stamped on the session.
 *
 * It lives here, next to the clip store, so the roster cannot accidentally ask the weaker question that
 * caused the reported bug - "do I hold any bytes?" instead of "do I hold THIS turn's bytes?". The rule
 * itself is pure and tested in voiceRowState.ts; this is the one place that feeds it real state.
 *
 * The current turn's stamp comes from the voice metadata syncVoiceSessions caches on every poll. A
 * session with no cached metadata has never had a narration this phone knows of, so it holds nothing
 * current by definition.
 *
 * `reachable` must be false for a row the roster RETAINED from an unreachable Director (the caller
 * knows this: it is exactly "this session has a RosterSessionMark"). It cannot be derived here -
 * a retained SessionDto looks perfectly healthy, because it IS the last healthy one - which is why
 * the roster kept drawing triangles on dead machines. It is a required parameter rather than an
 * optional one so a new caller has to answer the question instead of defaulting to the bug.
 */
export function rowVoiceInputs(session: SessionDto, agentWorking: boolean, reachable: boolean): VoiceRowInputs {
  const sid = session.sessionId ?? "";
  // An id-less session is not addressable: it cannot be fetched, downloaded for, or navigated to. Every
  // per-session lookup below keys off the id, and "" is a REAL key in those stores - so without this it
  // would read whatever happens to sit under "", i.e. another session's narration, and could describe a
  // row using text that is not its own. Nothing can be true about this row's voice; say exactly that.
  // Found in review.
  if (sid.length === 0) {
    return {
      voiceMode: Boolean(session.voiceMode),
      reachable,
      agentWorking,
      voiceUnavailable: session.voiceUnavailable != null,
      gatewayGenerating: false,
      gatewayHasAudio: false,
      clipDownloading: false,
      phoneReadyForCurrentTurn: false,
      hasSpokenText: false,
    };
  }
  const meta = getVoiceMeta(sid);
  const currentGeneratedAt = meta?.ready ? (meta.generatedAt ?? "") : "";
  return {
    voiceMode: Boolean(session.voiceMode),
    reachable,
    agentWorking,
    voiceUnavailable: session.voiceUnavailable != null,
    gatewayGenerating: Boolean(session.voiceGenerating),
    gatewayHasAudio: Boolean(session.voiceAudioReady),
    clipDownloading: getClipState(sid).phase === "downloading",
    phoneReadyForCurrentTurn: currentGeneratedAt.length > 0 && isPhoneReady(sid, currentGeneratedAt),
    hasSpokenText: (meta?.spoken?.length ?? 0) > 0,
  };
}

// ----- Cache Storage durability layer (best-effort capability, not a fallback) -----
// Cache Storage exists in any secure context, which a microphone- and service-worker-capable PWA
// already is. When it is genuinely absent the in-memory map still drives phone-ready for the life
// of the page; only cross-reload persistence is lost. This is capability detection, not error
// hiding - the authoritative phone-ready signal is unaffected.
function hasCaches(): boolean {
  return typeof caches !== "undefined";
}

function cacheKey(sid: string, generatedAt: string): string {
  return `/mobile/__voice-clip/${encodeURIComponent(sid)}/${encodeURIComponent(generatedAt)}`;
}

async function readCache(sid: string, generatedAt: string): Promise<ArrayBuffer | null> {
  if (!hasCaches()) return null;
  const cache = await caches.open(CACHE_NAME);
  const hit = await cache.match(cacheKey(sid, generatedAt));
  return hit ? await hit.arrayBuffer() : null;
}

async function writeCache(sid: string, generatedAt: string, bytes: ArrayBuffer): Promise<void> {
  if (!hasCaches()) return;
  const cache = await caches.open(CACHE_NAME);
  await cache.put(cacheKey(sid, generatedAt), new Response(bytes, { headers: { "Content-Type": "audio/mpeg" } }));
}

async function evictCache(sid: string, generatedAt: string): Promise<void> {
  if (!hasCaches()) return;
  const cache = await caches.open(CACHE_NAME);
  await cache.delete(cacheKey(sid, generatedAt));
}

// Ensure the named clip is downloaded to the phone and marked phone-ready. Idempotent: a clip
// already held (or in flight) is a no-op, so it is safe to call on every poll tick. A cache hit
// loads the bytes with no network; otherwise the MP3 is fetched once and persisted.
export async function ensureClip(sid: string, generatedAt: string): Promise<void> {
  if (!generatedAt) return;
  const current = _state.get(sid);
  if (current && current.generatedAt === generatedAt && (current.phase === "ready" || current.phase === "downloading")) {
    return;
  }

  const key = `${sid}|${generatedAt}`;
  if (_inflight.has(key)) return;
  _inflight.add(key);

  // Supersede an older clip for this session: free its object URL and drop its durable entry so the
  // cache holds only the current narration per session. releaseClipUrl, not a bare revoke - see there.
  if (current && current.generatedAt !== generatedAt) {
    releaseClipUrl(current.url);
    void evictCache(sid, current.generatedAt);
  }

  setState(sid, { generatedAt, phase: "downloading", url: null });
  try {
    let bytes = await readCache(sid, generatedAt);
    if (bytes === null) {
      bytes = await fetchWingmanVoiceAudio(sid);
      void writeCache(sid, generatedAt, bytes);
    }
    const url = URL.createObjectURL(new Blob([bytes], { type: "audio/mpeg" }));
    setState(sid, { generatedAt, phase: "ready", url });
  } catch (err) {
    // Record the failure as the clip's phase so the UI keeps showing the working state and the next
    // poll retries; the error is surfaced (not swallowed into a false "ready").
    console.warn(`[voice/clips] download failed sid=${sid} generatedAt=${generatedAt}: ${err instanceof Error ? err.message : String(err)}`);
    setState(sid, { generatedAt, phase: "error", url: null });
  } finally {
    _inflight.delete(key);
  }
}

// ----- Voice metadata cache (issue #1015): the narration state + spoken TEXT, cached alongside the
// audio so the Voice screen can render and start speaking from cache with no network round-trip.
// localStorage (small JSON, survives reloads); absence is capability-detected, not a fallback.
const META_PREFIX = "dt.voice.meta.";

/** Persist a session's latest voice metadata (readiness + spoken narration text). */
export function saveVoiceMeta(sid: string, voice: WingmanVoice): void {
  try {
    if (typeof localStorage !== "undefined") localStorage.setItem(META_PREFIX + sid, JSON.stringify(voice));
  } catch {
    // storage unavailable/full; the in-memory poll still drives the screen this page load
  }
}

/** The cached voice metadata for a session, or null - read synchronously to seed the first render. */
export function getVoiceMeta(sid: string): WingmanVoice | null {
  try {
    if (typeof localStorage === "undefined") return null;
    const raw = localStorage.getItem(META_PREFIX + sid);
    return raw ? (JSON.parse(raw) as WingmanVoice) : null;
  } catch {
    return null;
  }
}

// Pull every gateway-ready voice session's current clip down to the phone. Called from the roster
// poll so a voice session's card can flip from the yellow "working" state to the play-triangle the
// moment its audio is local. Per-session metadata misses are isolated: one session's transient
// fetch failure must not abort the others' sync, and the next poll retries it (correct retry
// behavior, not a degraded fallback).
export async function syncVoiceSessions(sessions: SessionDto[]): Promise<void> {
  const voiceReady = sessions.filter((s) => Boolean(s.voiceMode) && Boolean(s.voiceAudioReady) && Boolean(s.sessionId));
  await Promise.all(
    voiceReady.map(async (s) => {
      const sid = s.sessionId ?? "";
      try {
        const voice = await getWingmanVoice(sid);
        // Cache the state + narration text (issue #1015) so entering the screen shows it instantly.
        saveVoiceMeta(sid, voice);
        if (voice.ready && voice.generatedAt) await ensureClip(sid, voice.generatedAt);
      } catch {
        // Transient per-session miss (Director briefly unreachable); retried on the next poll tick.
      }
    }),
  );
}

// The ONE clip audio object playing right now, across the whole app (roster taps + the voice screen's
// own element go through here). Held so it can always be stopped - the old code did `new Audio()` per
// tap and kept no reference, so a clip could not be stopped and taps overlapped (it "kept talking").
let _currentAudio: HTMLAudioElement | null = null;
let _currentSid: string | null = null; // the session whose clip is playing (for the roster stop toggle)
let _currentUrl: string | null = null; // the object URL that element is streaming from - see releaseClipUrl

// Object URLs superseded while the element was still streaming from them, revoked once it stops.
const _deferredRevokes = new Set<string>();

/**
 * Free a superseded clip's object URL - unless an <audio> element is playing it RIGHT NOW, in which
 * case the revoke waits until that element stops.
 *
 * WHY THIS IS NOT A BARE REVOKE. A narration arrives in two stages: the judge's short words are
 * synthesised first so there is something to hear at once, and the fuller narration call replaces
 * the clip when it answers. Both describe the SAME turn, so the replacement is right - but it used
 * to revoke the URL the element was mid-sentence through. Revoking a blob URL that a live element is
 * streaming from pulls the bytes out from under it: the audio dies partway, and the screen then hands
 * over the new, longer clip, which reads as the voice jumping or starting over by itself.
 *
 * The rule from issue #1322 - never pull the rug on a listener - is exactly this case. Deferring
 * costs nothing: the new clip still becomes available the moment it has downloaded, because this
 * touches only when the OLD bytes are freed, never when the new ones arrive.
 *
 * A clip dropped because its turn is over is a different path and is unaffected: the Gateway stops
 * calling it ready, the screen stops playback, and the URL is then freed here with nothing playing.
 */
function releaseClipUrl(url: string | null): void {
  if (url === null) return;
  if (url === _currentUrl) {
    _deferredRevokes.add(url);
    return;
  }
  URL.revokeObjectURL(url);
}

/** Nothing is playing any more, so any revoke that was waiting on the element can happen now. */
function drainDeferredRevokes(): void {
  for (const url of _deferredRevokes) URL.revokeObjectURL(url);
  _deferredRevokes.clear();
}

// Stop whatever clip is playing right now, everywhere. Safe to call when nothing is playing. Used by
// the roster (tap a playing card to stop) and the voice screen (its stop button + leaving the screen).
export function stopPlayback(): void {
  const audio = _currentAudio;
  const wasPlaying = _currentAudio !== null;
  _currentAudio = null;
  _currentSid = null;
  _currentUrl = null;
  drainDeferredRevokes();
  if (audio !== null) {
    try {
      audio.pause();
    } catch {
      /* element already gone */
    }
  }
  if (wasPlaying) notify(); // re-render the roster so a stopped card drops back to the play triangle
}

/** The session id of the clip playing right now, or null - so the roster shows a stop, not play, on it. */
export function playingSid(): string | null {
  return _currentAudio !== null && !_currentAudio.paused ? _currentSid : null;
}

// Play a session's locally-stored clip immediately (the roster triangle tap). Stops any clip already
// playing first, so playback never overlaps and is always stoppable. Returns false when no phone-ready
// clip is held, so the caller never implies playback that did not happen.
export function playClip(sid: string): boolean {
  const s = _state.get(sid);
  if (!s || s.phase !== "ready" || s.url === null) return false;
  stopPlayback();
  const audio = new Audio(s.url);
  _currentAudio = audio;
  _currentSid = sid;
  _currentUrl = s.url;
  const clear = () => {
    if (_currentAudio === audio) {
      _currentAudio = null;
      _currentSid = null;
      _currentUrl = null;
      drainDeferredRevokes(); // a URL superseded mid-playback is freed now that nothing is streaming it
      notify(); // roster flips this card back to the play triangle
    }
  };
  audio.addEventListener("ended", clear);
  audio.addEventListener("pause", clear);
  void audio.play().catch(() => {
    // A user tap already provided the gesture; ignore the rare autoplay-policy rejection.
  });
  notify(); // roster shows this card as playing (stop control)
  return true;
}

// Subscribe a component to clip-state changes. It returns nothing - the re-render is the effect;
// components read the latest state with getClipState(sid) during render.
export function useVoiceClips(): void {
  const [, setTick] = useState(0);
  useEffect(() => {
    const listener = () => setTick((t) => t + 1);
    _listeners.add(listener);
    return () => {
      _listeners.delete(listener);
    };
  }, []);
}
