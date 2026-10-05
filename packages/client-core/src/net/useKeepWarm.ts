import { useEffect } from "react";
import { keepWarmPing } from "../api/client";

// Keep-warm heartbeat (Network Diagnostics mission, P2), shared by the phone and the Cockpit. While the app
// is FOREGROUNDED it sends a lightweight GET /diag/ping every ~25s so the WireGuard direct path stays active
// during use - an idle path silently drops back to the relay, which is the cold-start slowness this fixes.
// It PAUSES while the document is hidden (battery/data), and pings once immediately on becoming visible so
// the path is warm the moment you look. Best-effort: keepWarmPing never throws. This is the "measure, don't
// assert" half's warmer - the effect shows up in the rollup's percent-direct trend, we don't claim it here.

export const KEEP_WARM_MS = 25000;

// `enabled` false sends nothing: a Cockpit that is only a Collaborator's three pages has no Director path to keep warm
// (devthrottle_internal#2306, review finding F2).
export function useKeepWarm(enabled = true): void {
  useEffect(() => {
    if (!enabled) return undefined;
    if (typeof document === "undefined") {
      // Non-DOM env (SSR/tests): a plain interval with no visibility gating.
      const timer = setInterval(() => void keepWarmPing(), KEEP_WARM_MS);
      return () => clearInterval(timer);
    }

    let timer: ReturnType<typeof setInterval> | undefined;

    const start = () => {
      if (timer !== undefined) return;
      void keepWarmPing(); // warm immediately on (re)foreground
      timer = setInterval(() => void keepWarmPing(), KEEP_WARM_MS);
    };
    const stop = () => {
      if (timer === undefined) return;
      clearInterval(timer);
      timer = undefined;
    };
    const onVisibility = () => (document.visibilityState === "visible" ? start() : stop());

    if (document.visibilityState === "visible") start();
    document.addEventListener("visibilitychange", onVisibility);

    return () => {
      stop();
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [enabled]);
}
