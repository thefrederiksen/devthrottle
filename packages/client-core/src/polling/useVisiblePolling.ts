// A visibility-aware replacement for the per-page "AbortController + setInterval" poll block (issue
// #1239). The pages that poll an endpoint only THEY read - Schedule, the Wingman pipeline, Exes, Lists,
// the Directors registry list - do not need a shared store, but they DO need to go quiet when the tab is
// hidden. This hook is the one place that logic lives: it calls `refresh` immediately, ticks it on the
// interval while visible, and on `visibilitychange` to hidden it stops the timer and cancels the
// in-flight request; on return to visible it refetches at once and resumes. The caller keeps its own
// state exactly as before - this only owns the timer and the visibility gating.
//
// `refresh` must be stable (wrap it in useCallback). The effect re-runs when `refresh`, `intervalMs` or
// `enabled` changes, tearing the old loop down cleanly. `enabled` false runs no loop at all (for example a
// page with nothing selected yet); it defaults to true, so existing callers are unchanged.
import { useEffect } from "react";

export function useVisiblePolling(
  refresh: (signal: AbortSignal) => Promise<void> | void,
  intervalMs: number,
  enabled: boolean = true,
): void {
  useEffect(() => {
    if (!enabled) return;
    const hasDocument = typeof document !== "undefined";
    let controller = new AbortController();
    let timer: number | undefined;

    const tick = (): void => void refresh(controller.signal);

    const armInterval = (): void => {
      if (timer === undefined) timer = window.setInterval(tick, intervalMs);
    };
    const disarmInterval = (): void => {
      if (timer !== undefined) {
        window.clearInterval(timer);
        timer = undefined;
      }
    };

    const isHidden = (): boolean => hasDocument && document.visibilityState === "hidden";

    const onVisibilityChange = (): void => {
      if (isHidden()) {
        // Hidden: stop ticking and cancel the in-flight fetch so the tab is truly quiet.
        disarmInterval();
        controller.abort();
      } else if (timer === undefined) {
        // Returned to visible: a fresh signal (the old one was aborted), refetch now, resume.
        controller = new AbortController();
        tick();
        armInterval();
      }
    };

    if (!isHidden()) {
      tick();
      armInterval();
    }
    if (hasDocument) document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      if (hasDocument) document.removeEventListener("visibilitychange", onVisibilityChange);
      disarmInterval();
      controller.abort();
    };
  }, [refresh, intervalMs, enabled]);
}
