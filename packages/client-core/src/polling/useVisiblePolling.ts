// A visibility-aware replacement for the per-page "AbortController + setInterval" poll block (issue
// #1239). The pages that poll an endpoint only THEY read - Schedule, the Wingman pipeline, Exes, Lists,
// the Directors registry list - do not need a shared store, but they DO need to go quiet when the tab is
// hidden. This hook is the one place that logic lives: it calls `refresh` immediately, ticks it on the
// interval while visible, and on `visibilitychange` to hidden it stops the timer and cancels the
// in-flight request; on return to visible it refetches at once and resumes. The caller keeps its own
// state exactly as before - this only owns the timer and the visibility gating.
//
// `firstReadWhenHidden` (the Licenses redesign, 8 November 2026): a page that sets it makes its FIRST read at once even in
// a hidden tab. Without it, a page opened in a background tab - a link from an email or a notification, a middle click -
// makes no read at all until the tab is shown, and sits on "Loading..." for as long as it stays in the background:
// measured on the live Licenses page, the first request left 12 seconds after load, at the moment the tab was shown, and
// none before. Only the REPEATING poll waits for the tab to be visible. It is off by default: a page such as the session
// chat deliberately makes no request at all while hidden.
//
// `refresh` must be stable (wrap it in useCallback). The effect re-runs when `refresh`, `intervalMs` or
// `enabled` changes, tearing the old loop down cleanly. `enabled` false runs no loop at all (for example a
// page with nothing selected yet); it defaults to true, so existing callers are unchanged.
import { useEffect } from "react";

export function useVisiblePolling(
  refresh: (signal: AbortSignal) => Promise<void> | void,
  intervalMs: number,
  enabled: boolean = true,
  options: { firstReadWhenHidden?: boolean } = {},
): void {
  const firstReadWhenHidden = options.firstReadWhenHidden === true;
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
        // Returned to visible: a fresh signal, refetch now, resume. The old signal is aborted first - it already is when
        // the tab was hidden mid-run; when the tab was hidden from the start, the first read may still be out.
        controller.abort();
        controller = new AbortController();
        tick();
        armInterval();
      }
    };

    if (!isHidden()) {
      tick();
      armInterval();
    } else if (firstReadWhenHidden) {
      // The first answer even behind another tab; the repeating poll waits until the tab is shown.
      tick();
    }
    if (hasDocument) document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      if (hasDocument) document.removeEventListener("visibilitychange", onVisibilityChange);
      disarmInterval();
      controller.abort();
    };
  }, [refresh, intervalMs, enabled, firstReadWhenHidden]);
}
