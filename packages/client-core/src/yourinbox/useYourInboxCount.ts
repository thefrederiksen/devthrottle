import { useEffect, useState } from "react";
import { describeAndReport, backgroundRecovered } from "../errors/reportClientError";
import { getYourInboxCount, type YourInboxCount } from "./yourInboxClient";

// THE UNREAD COUNT ON THE INBOX ENTRY (Factory Inbox mission, step C): the Gateway's count, read from
// GET /gateway/inbox/count and printed as it is - the Cockpit's rail, the phone's menu and the Your inbox tab show the
// same number because they read the same answer (rule 7: the client never counts for itself).
//
// ONE READ FOR THE WHOLE PAGE. Every caller shares one answer and one timer, so the rail and the tab can never disagree.
// The count is read again every 30 seconds while anything shows it, and at once after the owner marks, archives or
// replies (refreshYourInboxCount), so a message read on the screen is not still counted half a minute later.

const REFRESH_MS = 30_000;
const SURFACE = "your-inbox";
const WHAT = "read how many messages are unread";

type Listener = (count: YourInboxCount | null) => void;

/** The count as a caller holds it: null until the Gateway has answered. */
export type YourInboxCountAnswer = YourInboxCount | null;

let current: YourInboxCount | null = null;
const listeners = new Set<Listener>();
let timer: ReturnType<typeof setTimeout> | null = null;
let reading = false;
let failedOnce = false;

function publish(): void {
  for (const l of listeners) l(current);
}

function schedule(): void {
  if (timer !== null) clearTimeout(timer);
  timer = listeners.size > 0 ? setTimeout(() => void read(), REFRESH_MS) : null;
}

// A read asked for while one is on its way is not dropped: it runs as soon as that one ends, so a mark made while the count
// was being read is still counted.
let again = false;

async function read(): Promise<void> {
  if (reading) {
    again = true;
    return;
  }
  reading = true;
  again = false;
  try {
    current = await getYourInboxCount();
    if (failedOnce) backgroundRecovered(SURFACE, WHAT);
    failedOnce = false;
    publish();
  } catch (err: unknown) {
    // A count that cannot be read is reported once in the background and the entry keeps its last answer; the inbox page
    // itself says why when it is opened.
    failedOnce = true;
    describeAndReport(SURFACE, WHAT, err, { background: true });
  } finally {
    reading = false;
    if (again) void read();
    else schedule();
  }
}

/** Read the count again now: after a mark, an archive or a reply. */
export function refreshYourInboxCount(): void {
  void read();
}

// SOMETHING CHANGED ON A MESSAGE. The reading pane marks, archives and replies; the list beside it (the Cockpit) or behind
// it (the phone) reads itself again, and so does the count. One signal, so no shell has to wire the two together.
const changeListeners = new Set<() => void>();

/** Say a message was marked, archived or replied to: the count and every open list read again. */
export function yourInboxChanged(): void {
  refreshYourInboxCount();
  for (const l of changeListeners) l();
}

/** Be told when a message changes. Returns the unsubscribe. */
export function onYourInboxChanged(listener: () => void): () => void {
  changeListeners.add(listener);
  return () => {
    changeListeners.delete(listener);
  };
}

/** Forget the shared answer (tests). */
export function resetYourInboxCount(): void {
  current = null;
  listeners.clear();
  if (timer !== null) clearTimeout(timer);
  timer = null;
  reading = false;
  again = false;
  failedOnce = false;
  changeListeners.clear();
}

/**
 * The Gateway's unread count, or null until it has answered. `enabled` false reads nothing - the Inbox entry is shown
 * only while the factory area switch is on, and an entry that is not shown has no count to read.
 */
export function useYourInboxCount(enabled = true): YourInboxCountAnswer {
  const [count, setCount] = useState<YourInboxCount | null>(enabled ? current : null);
  useEffect(() => {
    if (!enabled) return undefined;
    const listener: Listener = (c) => setCount(c);
    const first = listeners.size === 0;
    listeners.add(listener);
    setCount(current);
    if (first) void read();
    return () => {
      listeners.delete(listener);
      if (listeners.size === 0 && timer !== null) {
        clearTimeout(timer);
        timer = null;
      }
    };
  }, [enabled]);
  return count;
}
