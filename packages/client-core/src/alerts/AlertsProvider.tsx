import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from "react";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import {
  askingSessionId,
  clearAlert,
  clearAllAlerts,
  listAlerts,
  markAlertsSeen,
  newestFirst,
  type AlertDto,
  type AlertKind,
} from "./alertsClient";

// THE ONE POLLER AND STORE FOR ALERTS (the Alerts mission, section 5.3). Mounted once per shell at the app root, so the
// bell, the tray and the Cockpit's per-session icons all read the same state and nothing else polls. It replaces the
// three pollers the restart, secret transfer and message request cards each ran.
//
// It reads every 5 seconds while the page is visible, goes quiet while it is hidden, and reads again at once after
// any answer or clear. A failed read keeps the last-known list on screen and says so.

const SURFACE = "alerts";
const READ = "load the alerts";

/** How often the alerts are read while the page is visible. */
export const ALERTS_POLL_MS = 5000;

export interface AlertsValue {
  /** Every alert the Gateway listed, newest first. */
  alerts: AlertDto[];
  /** The ones that need an answer, newest first - the "Needs you" section. */
  needsYou: AlertDto[];
  /** The rest, newest first - the "Earlier" section. */
  earlier: AlertDto[];
  /** The Gateway's count of alerts that need an answer. The bell shows this. */
  needsAnswerCount: number;
  /** False until the first read came back, so nothing says "Nothing needs you." before it knows. */
  loaded: boolean;
  /** Why the last read failed, or null. The last-known list stays standing. */
  listError: string | null;
  /** Why the last seen-marking or clear-all failed, or null. */
  actionError: string | null;
  /** The Gateway's sentence for each alert whose clear was refused. */
  clearErrors: Readonly<Record<string, string>>;
  /** The id of the alert being cleared, "all" during a clear-all, or null. */
  clearing: string | null;
  /** Read the alerts again now. */
  refresh: () => Promise<void>;
  /** Mark these alerts seen on the Gateway. */
  markSeen: (ids: string[]) => Promise<void>;
  clear: (id: string) => Promise<void>;
  clearAll: () => Promise<void>;
  /** The alerts that need an answer and were asked by this session - the Cockpit's amber per-session icons. */
  waitingFrom: (sessionId: string, kind?: AlertKind) => AlertDto[];
  now: () => number;
}

// Outside a provider there are no alerts: a component rendered on its own - in a test, or in a shell that has not
// mounted Alerts - shows no badge rather than starting a poll of its own.
const NONE: AlertsValue = {
  alerts: [],
  needsYou: [],
  earlier: [],
  needsAnswerCount: 0,
  loaded: false,
  listError: null,
  actionError: null,
  clearErrors: {},
  clearing: null,
  refresh: async () => {},
  markSeen: async () => {},
  clear: async () => {},
  clearAll: async () => {},
  waitingFrom: () => [],
  now: Date.now,
};

const AlertsContext = createContext<AlertsValue>(NONE);

export interface AlertsProviderProps {
  children: ReactNode;
  /** The clock, injectable for tests. Defaults to Date.now. */
  now?: () => number;
  /** False reads nothing: a shell whose page on screen is not the owner's whole app (a team's pages only) has no
   *  alerts to show. Defaults to true. */
  enabled?: boolean;
}

/** The alerts for everything inside it to read. Mount once, at the shell's root. */
export function AlertsProvider({ children, now, enabled = true }: AlertsProviderProps) {
  const [alerts, setAlerts] = useState<AlertDto[]>([]);
  const [needsAnswerCount, setNeedsAnswerCount] = useState(0);
  const [loaded, setLoaded] = useState(false);
  const [listError, setListError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [clearErrors, setClearErrors] = useState<Record<string, string>>({});
  const [clearing, setClearing] = useState<string | null>(null);
  // Reads can overlap (a poll and a read after an answer). Only the newest one started may land, so an older answer
  // can never put an alert back that a newer one already took away.
  const readSeq = useRef(0);
  // What the last read said about each alert needing an answer, so a read can tell an alert that was ANSWERED (it
  // needed an answer, now it does not) from one that simply sits in Earlier.
  const lastNeedsAnswer = useRef<Map<string, boolean>>(new Map());
  const clock = now ?? Date.now;

  const load = useCallback(async (signal?: AbortSignal) => {
    const seq = ++readSeq.current;
    try {
      const list = await listAlerts(signal);
      if (seq !== readSeq.current) return;
      // A clear's refusal ("This still needs your answer") describes the alert at the moment it was refused. Once the
      // alert is answered - a read lists it as no longer needing an answer, after one that said it did - or once it
      // leaves the list, the sentence is no longer true and goes. A failure on an alert that was already in Earlier and
      // still is (the Gateway could not be reached, say) is not touched: nothing about that alert has changed.
      const before = lastNeedsAnswer.current;
      const after = new Map(list.alerts.map((a) => [a.id, a.needsAnswer] as const));
      lastNeedsAnswer.current = after;
      // error-report-exempt: only REMOVES sentences already reported when they were shown; it shows nothing new
      setClearErrors((errors) => {
        const stale = Object.keys(errors).filter((id) => !after.has(id) || (after.get(id) === false && before.get(id) === true));
        if (stale.length === 0) return errors;
        const kept = { ...errors };
        for (const id of stale) delete kept[id];
        return kept;
      });
      setAlerts(newestFirst(list.alerts));
      setNeedsAnswerCount(list.needsAnswerCount);
      setLoaded(true);
      setListError(null);
      backgroundRecovered(SURFACE, READ);
    } catch (err) {
      if (signal?.aborted || seq !== readSeq.current) return;
      // The last-known list stays on screen; the tray says the read failed.
      setListError(describeAndReport(SURFACE, READ, err, { background: true }));
    }
  }, []);

  useVisiblePolling(load, ALERTS_POLL_MS, enabled);

  const refresh = useCallback(() => load(), [load]);

  const markSeen = useCallback(
    async (ids: string[]) => {
      if (ids.length === 0) return;
      try {
        await markAlertsSeen(ids);
        setActionError(null);
      } catch (err) {
        setActionError(describeAndReport(SURFACE, "mark the alerts seen", err, { background: true }));
        return;
      }
      await load();
    },
    [load],
  );

  const clear = useCallback(
    async (id: string) => {
      setClearing(id);
      setClearErrors((e) => ({ ...e, [id]: "" }));
      try {
        await clearAlert(id);
      } catch (err) {
        // The Gateway's own sentence - "This still needs your answer; it leaves when you answer it or when it
        // expires." - verbatim.
        setClearErrors((e) => ({ ...e, [id]: describeAndReport(SURFACE, "clear the alert", err) }));
      } finally {
        setClearing(null);
      }
      await load();
    },
    [load],
  );

  const clearAll = useCallback(async () => {
    setClearing("all");
    setActionError(null);
    try {
      await clearAllAlerts();
    } catch (err) {
      setActionError(describeAndReport(SURFACE, "clear the alerts", err));
    } finally {
      setClearing(null);
    }
    await load();
  }, [load]);

  const needsYou = useMemo(() => alerts.filter((a) => a.needsAnswer), [alerts]);
  const earlier = useMemo(() => alerts.filter((a) => !a.needsAnswer), [alerts]);

  const waitingFrom = useCallback(
    (sessionId: string, kind?: AlertKind) => {
      const id = sessionId.toLowerCase();
      return needsYou.filter((a) => (kind === undefined || a.kind === kind) && (askingSessionId(a) ?? "").toLowerCase() === id);
    },
    [needsYou],
  );

  const value = useMemo<AlertsValue>(
    () => ({
      alerts,
      needsYou,
      earlier,
      needsAnswerCount,
      loaded,
      listError,
      actionError,
      clearErrors,
      clearing,
      refresh,
      markSeen,
      clear,
      clearAll,
      waitingFrom,
      now: clock,
    }),
    [alerts, needsYou, earlier, needsAnswerCount, loaded, listError, actionError, clearErrors, clearing, refresh, markSeen, clear, clearAll, waitingFrom, clock],
  );
  return <AlertsContext.Provider value={value}>{children}</AlertsContext.Provider>;
}

/** The alerts, from the nearest AlertsProvider. */
export function useAlerts(): AlertsValue {
  return useContext(AlertsContext);
}
