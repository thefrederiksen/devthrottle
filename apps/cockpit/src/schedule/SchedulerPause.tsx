import { useCallback, useState } from "react";
import { getCronPause, setCronPause, type CronPause } from "@devthrottle/client-core/schedule/scheduleClient";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button } from "../components";
import "./schedulerPause.css";

// Pause everything (issue #3910): the account's one switch that stops every schedule from starting, and the banner
// that says so while it is on. The Schedule page and the Factories screen mount the same control, so the two can
// never disagree. Whether it is paused, the banner and the button's words are the Gateway's (CronPauseDto), shown
// as given; this file only reads the switch, sends the tap, and lays the two out.
//
// One tap each way, with no confirmation: pausing is the safe direction and has to be fast when something is
// wrong, and resuming changes no schedule - each is exactly as active or as paused as it was.

const POLL_MS = 10000;

export interface SchedulerPauseControl {
  /** The switch as the Gateway last answered it; null until the first read lands. */
  pause: CronPause | null;
  /** Why the switch could not be read or set; null when it could. */
  error: string | null;
  /** True while a tap is on its way to the Gateway. */
  busy: boolean;
  /** Pause everything when it is running, resume everything when it is paused. */
  toggle: () => Promise<void>;
}

/** Read the pause-everything switch, keep it fresh while the tab is visible, and flip it on a tap. */
export function useSchedulerPause(surface: string): SchedulerPauseControl {
  const [pause, setPause] = useState<CronPause | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(
    async (signal?: AbortSignal) => {
      try {
        setPause(await getCronPause(signal));
        setError(null);
        backgroundRecovered(surface, "read whether schedules are paused");
      } catch (err) {
        if (signal?.aborted === true) return;
        setError(describeAndReport(surface, "read whether schedules are paused", err, { background: true }));
      }
    },
    [surface],
  );
  useVisiblePolling(refresh, POLL_MS);

  const toggle = useCallback(async () => {
    if (pause === null) return;
    setBusy(true);
    try {
      setPause(await setCronPause(!pause.paused));
      setError(null);
    } catch (err) {
      setError(
        `${pause.actionLabel} failed: ${describeAndReport(surface, pause.paused ? "resume every schedule" : "pause every schedule", err)}`,
      );
    } finally {
      setBusy(false);
    }
  }, [pause, surface]);

  return { pause, error, busy, toggle };
}

/** The one control, for a page header. Nothing is drawn until the switch has been read: a button that could say
 *  the wrong thing is worse than a moment without one. */
export function PauseEverythingButton({ control }: { control: SchedulerPauseControl }) {
  const { pause, busy, toggle } = control;
  if (pause === null) return null;
  return (
    <Button
      variant={pause.paused ? "primary" : "secondary"}
      data-testid="schedules-pause-toggle"
      title={pause.actionTitle}
      disabled={busy}
      onClick={() => void toggle()}
    >
      {busy ? "Saving..." : pause.actionLabel}
    </Button>
  );
}

/** The banner while everything is paused, with the way back in it, so a paused fleet never looks like a broken
 *  one. Also where a failure to read or set the switch is said. */
export function PausedBanner({ control }: { control: SchedulerPauseControl }) {
  const { pause, error, busy, toggle } = control;
  return (
    <>
      {error !== null && (
        <div className="sched-pause-error" role="alert" data-testid="schedules-pause-error">
          {error}
        </div>
      )}
      {pause !== null && pause.paused && (
        <div className="sched-pause-banner" role="status" data-testid="schedules-paused-banner">
          <span className="sched-pause-banner-text">{pause.banner}</span>
          <Button variant="primary" disabled={busy} title={pause.actionTitle} onClick={() => void toggle()}>
            {busy ? "Saving..." : pause.actionLabel}
          </Button>
        </div>
      )}
    </>
  );
}
