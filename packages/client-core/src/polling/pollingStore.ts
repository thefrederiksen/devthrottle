// One shared, visibility-aware poll loop behind a subscriber interface (issue #1239). This is the
// single source of truth the Cockpit's fleet-reading pages build on: however many pages subscribe to
// one store, there is exactly ONE poll loop and ONE in-flight request, and every subscriber reads the
// identical snapshot at the same moment. It replaces the "AbortController + setInterval +
// keep-last-on-error" block that was copy-pasted across nine views, each with its own timer that
// ignored whether the tab was even visible.
//
// Behavior:
//  - Lazy: the loop starts when the first subscriber arrives and stops when the last one leaves, so a
//    store nobody is reading costs nothing.
//  - Visibility-aware: while the page is hidden the loop is paused and the in-flight request cancelled;
//    on return to visible it refetches immediately and resumes. A backgrounded tab makes zero requests
//    after its first load: a store that starts while the page is already hidden (a tab opened in the
//    background) still takes ONE first fetch, so the page has something to show instead of "Loading..."
//    forever (issue #3603), and then stays quiet until the page is visible.
//  - Keep-last-on-error: a failed poll raises the error but keeps the last good data on screen, so a
//    transient Gateway blip never blanks the roster.
//
// It is framework-agnostic on purpose (the React binding is usePollingStore): every moving part - the
// timer, the visibility source, the fetch - is injectable, so the whole loop is unit-tested in Node
// with no DOM.
import { documentVisibility, type VisibilitySource } from "./visibility";
import { reportShownErrorWhenNew, type ReportMemory } from "../errors/reportClientError";

// The snapshot every subscriber reads. `data` is null until the first fetch settles; `loading` is true
// only over that first fetch (a subsequent failed poll keeps the last data and sets `error`, it does
// not go back to loading).
export interface PollState<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
}

// The shared poll loop. subscribe/getSnapshot match React's useSyncExternalStore contract so the React
// hook is a one-liner; refreshNow and isPolling are for imperative refresh (after a mutation) and for
// tests/inspection.
export interface PollingStore<T> {
  subscribe(listener: () => void): () => void;
  getSnapshot(): PollState<T>;
  /** Fetch immediately - e.g. right after creating a session - so the change shows without waiting for
   *  the next tick. A no-op while there are no subscribers or the page is hidden. */
  refreshNow(): void;
  /** Whether the interval timer is currently armed (has subscribers AND the page is visible). */
  isPolling(): boolean;
}

// The injectable timer seam. Defaults to the browser's window timers; tests pass fakes so ticks are
// driven by hand.
export interface PollTimers {
  setInterval(handler: () => void, ms: number): number;
  clearInterval(id: number): void;
}

export interface PollingStoreOptions<T> {
  /** The one request this store polls. It is handed an AbortSignal so a paused/hidden tab can cancel
   *  the in-flight fetch. */
  fetcher: (signal: AbortSignal) => Promise<T>;
  intervalMs: number;
  /** Map a thrown value to the message subscribers see (defaults to the Error message). */
  mapError?: (err: unknown) => string;
  /** Where a failed poll is filed and what it was doing, in the user's words ("load the sessions"). Subscribers show
   *  the error, so it is reported too (issue #3675) - when it first appears, not on every round. */
  reporting: { surface: string; action: string };
  visibility?: VisibilitySource;
  timers?: PollTimers;
}

const defaultTimers: PollTimers = {
  setInterval: (handler, ms) => window.setInterval(handler, ms),
  clearInterval: (id) => window.clearInterval(id),
};

export function createPollingStore<T>(options: PollingStoreOptions<T>): PollingStore<T> {
  const {
    fetcher,
    intervalMs,
    mapError = (err) => (err instanceof Error ? err.message : String(err)),
    reporting,
    visibility = documentVisibility(),
    timers = defaultTimers,
  } = options;

  const listeners = new Set<() => void>();
  let state: PollState<T> = { data: null, error: null, loading: true };
  let timer: number | undefined;
  let controller: AbortController | undefined;
  let unsubscribeVisibility: (() => void) | undefined;
  // What the last failed poll reported, so the same failure is not reported again every round.
  const reported: ReportMemory = { current: null };

  function emit(): void {
    for (const listener of listeners) listener();
  }

  // Replace the snapshot with a fresh object (so useSyncExternalStore's Object.is check fires) and
  // notify. Between polls getSnapshot returns the SAME object, which is what keeps React from looping.
  function setState(next: PollState<T>): void {
    state = next;
    emit();
  }

  async function poll(): Promise<void> {
    controller?.abort();
    const own = new AbortController();
    controller = own;
    try {
      const data = await fetcher(own.signal);
      if (own.signal.aborted) return;
      reported.current = null;
      setState({ data, error: null, loading: false });
    } catch (err) {
      // A cancelled poll (tab hidden, unmount) is not an error to show - just stop.
      if (own.signal.aborted) return;
      // Keep the last good data; raise the error alongside it.
      setState({
        data: state.data,
        error: reportShownErrorWhenNew(reported, reporting.surface, reporting.action, mapError(err), undefined, err),
        loading: false,
      });
    }
  }

  function armInterval(): void {
    if (timer === undefined) timer = timers.setInterval(() => void poll(), intervalMs);
  }

  function disarmInterval(): void {
    if (timer !== undefined) {
      timers.clearInterval(timer);
      timer = undefined;
    }
  }

  // Begin (or resume) polling: fetch straight away, then tick on the interval.
  function resume(): void {
    void poll();
    armInterval();
  }

  // Pause polling: stop the timer and cancel any in-flight request so a hidden tab is truly quiet.
  function pause(): void {
    disarmInterval();
    controller?.abort();
  }

  function onVisibilityChange(): void {
    if (listeners.size === 0) return;
    if (visibility.isVisible()) {
      if (timer === undefined) resume(); // returned to visible: refetch now and resume
    } else {
      pause();
    }
  }

  function start(): void {
    unsubscribeVisibility = visibility.subscribe(onVisibilityChange);
    if (visibility.isVisible()) resume();
    else if (state.data === null) void poll(); // hidden from the start: one first load, no interval (#3603)
  }

  function stop(): void {
    pause();
    unsubscribeVisibility?.();
    unsubscribeVisibility = undefined;
  }

  return {
    subscribe(listener) {
      listeners.add(listener);
      if (listeners.size === 1) start();
      return () => {
        listeners.delete(listener);
        if (listeners.size === 0) stop();
      };
    },
    getSnapshot() {
      return state;
    },
    refreshNow() {
      if (listeners.size > 0 && visibility.isVisible()) void poll();
    },
    isPolling() {
      return timer !== undefined;
    },
  };
}
