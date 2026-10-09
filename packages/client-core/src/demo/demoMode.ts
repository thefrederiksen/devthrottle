// DEMO MODE (owner, 8 Oct 2026): one switch per account that blurs what the factories and sessions do, for a
// screen share or a live demo. The Gateway owns the answer (GET/PUT /gateway/demo-mode); every Cockpit on the
// account reads it on a short poll, so turning it on from one browser blurs every screen on the account.
//
// The shell puts the `demo-mode` class on its root while this is on, and one global rule blurs every element
// marked `dt-private`. Marking text private is the screen's job; deciding whether to blur is the Gateway's.
import { useEffect, useState } from "react";
import { authHeaders, GatewayError } from "../api/client";

const CHANGED_EVENT = "devthrottle:demo-mode-changed";

/** How often an open Cockpit re-reads the switch, so a change made elsewhere reaches it without a reload. */
export const DEMO_MODE_POLL_MS = 10_000;

// GET /gateway/demo-mode - whether this account is in demo mode. Throws on a non-2xx (no-fallback rule): the
// caller decides what a failed read means for what is on screen.
export async function getDemoMode(signal?: AbortSignal): Promise<boolean> {
  const res = await fetch("/gateway/demo-mode", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw new GatewayError(res.status, `GET /gateway/demo-mode failed: ${res.status}`);
  const body = (await res.json()) as { enabled?: unknown };
  return body.enabled === true;
}

// PUT /gateway/demo-mode { enabled } - turn demo mode on or off for the whole account. Returns the applied value
// and tells every demo-mode reader on this page to apply it at once, rather than at the next poll.
export async function setDemoMode(enabled: boolean, signal?: AbortSignal): Promise<boolean> {
  const res = await fetch("/gateway/demo-mode", {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ enabled }),
    signal,
  });
  if (!res.ok) {
    let detail = `${res.status}`;
    try {
      const b = (await res.json()) as { error?: string };
      if (typeof b.error === "string") detail = b.error;
    } catch {
      /* body unreadable - keep the status code */
    }
    throw new GatewayError(res.status, `PUT /gateway/demo-mode failed: ${detail}`);
  }
  const body = (await res.json()) as { enabled?: unknown };
  const applied = body.enabled === true;
  window.dispatchEvent(new CustomEvent<boolean>(CHANGED_EVENT, { detail: applied }));
  return applied;
}

/**
 * Whether this account is in demo mode, kept current by a poll and by every write made on this page. A failed
 * read keeps the last answer rather than turning the blur off: dropping it mid-demo because one poll failed would
 * show exactly what the presenter asked to hide. Starts OFF - nothing is known yet, and off is what the Gateway
 * answers for an account that never chose.
 */
export function useDemoMode(enabled: boolean): boolean {
  const [on, setOn] = useState(false);
  useEffect(() => {
    if (!enabled) return undefined;
    let cancelled = false;
    const poll = () =>
      void getDemoMode().then(
        (value) => {
          if (!cancelled) setOn(value);
        },
        () => {
          /* keep the last answer - see the note above */
        },
      );
    const onChanged = (e: Event) => setOn((e as CustomEvent<boolean>).detail === true);
    poll();
    const id = window.setInterval(poll, DEMO_MODE_POLL_MS);
    window.addEventListener(CHANGED_EVENT, onChanged);
    return () => {
      cancelled = true;
      window.clearInterval(id);
      window.removeEventListener(CHANGED_EVENT, onChanged);
    };
  }, [enabled]);
  return on;
}
