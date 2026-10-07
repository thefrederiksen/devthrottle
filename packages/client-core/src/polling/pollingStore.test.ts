import { describe, expect, it } from "vitest";
import { createPollingStore, type PollTimers } from "./pollingStore";
import type { VisibilitySource } from "./visibility";

// The polling store is the heart of issue #1239 (one shared loop, visibility-aware, keep-last-on-error),
// so it is tested directly - no DOM, every seam injected: a hand-driven interval, a controllable
// visibility source, and a fetcher the test resolves or rejects on demand.

// A fake interval clock: setInterval records the handler, and tick() fires every armed handler once.
function fakeTimers(): PollTimers & { tick(): void; armed(): number } {
  const handlers = new Map<number, () => void>();
  let nextId = 1;
  return {
    setInterval(handler) {
      const id = nextId++;
      handlers.set(id, handler);
      return id;
    },
    clearInterval(id) {
      handlers.delete(id);
    },
    tick() {
      for (const handler of [...handlers.values()]) handler();
    },
    armed() {
      return handlers.size;
    },
  };
}

// A controllable page-visibility source: set(false)/set(true) flips it and notifies subscribers, exactly
// as a real "visibilitychange" event would.
function fakeVisibility(initial = true): VisibilitySource & { set(v: boolean): void } {
  let visible = initial;
  const listeners = new Set<() => void>();
  return {
    isVisible: () => visible,
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    set(v) {
      visible = v;
      for (const listener of [...listeners]) listener();
    },
  };
}

// A fetcher whose results the test controls: each call returns the next queued value, or rejects when the
// next queued item is an Error. Records how many times it was called.
function fakeFetcher() {
  const queue: Array<unknown> = [];
  let calls = 0;
  const fetcher = (): Promise<number> => {
    calls++;
    const next = queue.length > 0 ? queue.shift() : calls;
    if (next instanceof Error) return Promise.reject(next);
    return Promise.resolve(next as number);
  };
  return {
    fetcher,
    calls: () => calls,
    queueValue: (v: number) => queue.push(v),
    queueError: (e: Error) => queue.push(e),
  };
}

// Let queued microtasks (the awaited fetch inside poll) settle before asserting.
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

describe("createPollingStore", () => {
  it("does not poll until the first subscriber arrives, and stops after the last leaves", async () => {
    const timers = fakeTimers();
    const f = fakeFetcher();
    const store = createPollingStore({ fetcher: f.fetcher, intervalMs: 1000, visibility: fakeVisibility(), timers });

    // No subscribers: nothing runs.
    expect(f.calls()).toBe(0);
    expect(store.isPolling()).toBe(false);

    const unsubscribe = store.subscribe(() => {});
    await settle();
    expect(f.calls()).toBe(1); // immediate fetch on start
    expect(store.isPolling()).toBe(true);

    unsubscribe();
    expect(store.isPolling()).toBe(false);
  });

  it("runs ONE loop shared across many subscribers", async () => {
    const timers = fakeTimers();
    const f = fakeFetcher();
    const store = createPollingStore({ fetcher: f.fetcher, intervalMs: 1000, visibility: fakeVisibility(), timers });

    let aNotified = 0;
    let bNotified = 0;
    store.subscribe(() => aNotified++);
    store.subscribe(() => bNotified++);
    await settle();

    // Two subscribers, but only ONE immediate fetch and ONE armed timer.
    expect(f.calls()).toBe(1);
    expect(timers.armed()).toBe(1);

    // One tick advances the single loop; both subscribers see it.
    timers.tick();
    await settle();
    expect(f.calls()).toBe(2);
    expect(aNotified).toBeGreaterThan(0);
    expect(bNotified).toBeGreaterThan(0);
    expect(store.getSnapshot().data).toBe(2);
  });

  it("keeps the last good data when a poll fails, and raises the error", async () => {
    const timers = fakeTimers();
    const f = fakeFetcher();
    f.queueValue(42); // first poll succeeds
    f.queueError(new Error("Gateway down")); // next poll fails
    const store = createPollingStore({ fetcher: f.fetcher, intervalMs: 1000, visibility: fakeVisibility(), timers });

    store.subscribe(() => {});
    await settle();
    expect(store.getSnapshot().data).toBe(42);
    expect(store.getSnapshot().error).toBeNull();
    expect(store.getSnapshot().loading).toBe(false);

    timers.tick(); // the failing poll
    await settle();
    expect(store.getSnapshot().data).toBe(42); // last good data retained
    expect(store.getSnapshot().error).toBe("Gateway down");
  });

  it("stops polling while the page is hidden and refetches on return to visible", async () => {
    const timers = fakeTimers();
    const f = fakeFetcher();
    const visibility = fakeVisibility(true);
    const store = createPollingStore({ fetcher: f.fetcher, intervalMs: 1000, visibility, timers });

    store.subscribe(() => {});
    await settle();
    expect(f.calls()).toBe(1);
    expect(store.isPolling()).toBe(true);

    // Hidden: the loop pauses - the timer is disarmed and a tick fires nothing.
    visibility.set(false);
    expect(store.isPolling()).toBe(false);
    timers.tick();
    await settle();
    expect(f.calls()).toBe(1); // no new request while hidden

    // Visible again: refetch immediately and resume.
    visibility.set(true);
    await settle();
    expect(f.calls()).toBe(2);
    expect(store.isPolling()).toBe(true);
  });

  it("takes exactly one first fetch when it starts hidden, then none until the page becomes visible (#3603)", async () => {
    const timers = fakeTimers();
    const f = fakeFetcher();
    const visibility = fakeVisibility(false);
    const store = createPollingStore({ fetcher: f.fetcher, intervalMs: 1000, visibility, timers });

    store.subscribe(() => {});
    await settle();
    // A tab opened in the background must still load once, or the page says "Loading..." forever.
    expect(f.calls()).toBe(1);
    expect(store.getSnapshot()).toEqual({ data: 1, error: null, loading: false });
    // ...but it does not start polling while hidden.
    expect(store.isPolling()).toBe(false);
    timers.tick();
    timers.tick();
    await settle();
    expect(f.calls()).toBe(1);

    visibility.set(true);
    await settle();
    expect(f.calls()).toBe(2);
    expect(store.isPolling()).toBe(true);
  });

  it("shows the error, not Loading, when the first fetch fails while hidden, and retries only once visible (#3603)", async () => {
    const timers = fakeTimers();
    const f = fakeFetcher();
    f.queueError(new Error("Gateway down"));
    const visibility = fakeVisibility(false);
    const store = createPollingStore({ fetcher: f.fetcher, intervalMs: 1000, visibility, timers });

    store.subscribe(() => {});
    await settle();
    expect(f.calls()).toBe(1);
    expect(store.getSnapshot()).toEqual({ data: null, error: "Gateway down", loading: false });
    expect(store.isPolling()).toBe(false);

    visibility.set(true);
    await settle();
    expect(f.calls()).toBe(2);
    expect(store.getSnapshot().data).toBe(2);
    expect(store.isPolling()).toBe(true);
  });

  it("refreshNow fetches immediately while visible, and is a no-op with no subscribers or while hidden", async () => {
    const timers = fakeTimers();
    const f = fakeFetcher();
    const visibility = fakeVisibility(true);
    const store = createPollingStore({ fetcher: f.fetcher, intervalMs: 1000, visibility, timers });

    // No subscribers: refreshNow does nothing.
    store.refreshNow();
    await settle();
    expect(f.calls()).toBe(0);

    store.subscribe(() => {});
    await settle();
    expect(f.calls()).toBe(1);

    store.refreshNow();
    await settle();
    expect(f.calls()).toBe(2); // immediate extra fetch

    visibility.set(false);
    store.refreshNow();
    await settle();
    expect(f.calls()).toBe(2); // hidden: no-op
  });

  it("returns a stable snapshot reference between changes (useSyncExternalStore safe)", async () => {
    const timers = fakeTimers();
    const f = fakeFetcher();
    const store = createPollingStore({ fetcher: f.fetcher, intervalMs: 1000, visibility: fakeVisibility(), timers });

    store.subscribe(() => {});
    await settle();
    const first = store.getSnapshot();
    // No change happened between these two reads - the reference must be identical.
    expect(store.getSnapshot()).toBe(first);

    timers.tick();
    await settle();
    expect(store.getSnapshot()).not.toBe(first); // a real change swaps the reference
  });
});
