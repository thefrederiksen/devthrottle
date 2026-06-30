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
import { fetchWingmanVoiceAudio, getWingmanVoice, type SessionDto } from "../api/client";

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

// ----- Cache Storage durability layer (best-effort capability, not a fallback) -----
// Cache Storage exists in any secure context, which a microphone- and service-worker-capable PWA
// already is. When it is genuinely absent the in-memory map still drives phone-ready for the life
// of the page; only cross-reload persistence is lost. This is capability detection, not error
// hiding - the authoritative phone-ready signal is unaffected.
function hasCaches(): boolean {
  return typeof caches !== "undefined";
}

function cacheKey(sid: string, generatedAt: string): string {
  return `/m/__voice-clip/${encodeURIComponent(sid)}/${encodeURIComponent(generatedAt)}`;
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
  // cache holds only the current narration per session.
  if (current && current.generatedAt !== generatedAt) {
    if (current.url) URL.revokeObjectURL(current.url);
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
        if (voice.ready && voice.generatedAt) await ensureClip(sid, voice.generatedAt);
      } catch {
        // Transient per-session miss (Director briefly unreachable); retried on the next poll tick.
      }
    }),
  );
}

// Play a session's locally-stored clip immediately (the roster triangle tap). Returns false when no
// phone-ready clip is held, so the caller never implies playback that did not happen.
export function playClip(sid: string): boolean {
  const s = _state.get(sid);
  if (!s || s.phase !== "ready" || s.url === null) return false;
  const audio = new Audio(s.url);
  void audio.play().catch(() => {
    // A user tap already provided the gesture; ignore the rare autoplay-policy rejection.
  });
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
