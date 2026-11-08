import { createContext, useCallback, useContext, useId, useState, type MouseEvent, type ReactNode } from "react";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import { getLicenseAlarms, type LicenseAlarms } from "./licensesClient";
import "./licenseAlarm.css";

// THE LICENSE ALARM INDICATOR (mission "Web UI Polish", the owner, 7 November 2026): a small triangle with the count of
// open license alarms, beside the bell - in the Cockpit's rail head and in every phone app bar. Clicking or tapping it
// opens Licenses. It replaces the full-width bar across the top of every page ("No bar anywhere"), and matches the
// triangle the desktop Director draws for the same alarms.
//
// It is still on every page while an alarm is open and gone the moment none is, because it lives in the one place every
// page shares: the bell's row. The read is made ONCE per shell, by LicenseAlarmsProvider mounted above the router's
// pages, so the triangle does not blink out and back on every navigation and a phone screen change makes no new read.
//
// ITS COLOUR IS THE GATEWAY'S. The answer's `tone` - "warning" or "critical" - is set as `data-tone` and a class; the
// shared sheet (licenseAlarm.css) draws a warning amber and a critical red. Nothing here compares a number with a
// threshold.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). Every word is the Gateway's (GET /gateway/licenses/alarms, folded by
// LicenseAlarmFold): the count, and the summary and link words, which become the control's name and tooltip. Full
// sentences belong on the Licenses page. Count 0 is no triangle.
//
// THE HOVER LIST (Cockpit only, `details`): hovering or focusing the triangle shows a card with the summary, one line per
// open alarm - each alarm's own `text`, verbatim - and the link words. The rail head sits in a rail that scrolls, which
// would cut a card placed inside it off at the rail's edge, so the card is fixed to the page at the triangle's corner,
// measured when it opens (as RailAlertsBell places its tray). The phone has no hover, so it never asks for the card.

/** How often the alarms are read again while the page is visible. */
export const LICENSE_ALARM_POLL_MS = 30_000;

const SURFACE = "license-alarm-indicator";
const READ = "read the license alarms";

/** The Gateway's open alarms, read now and every LICENSE_ALARM_POLL_MS while the page is visible; null until the first
 *  answer. A read that fails keeps the last answer and is reported centrally as a background poll (once until a read
 *  works again); it is not drawn here, because the connection banner is the one voice for a network problem. */
export function useLicenseAlarms(enabled: boolean): LicenseAlarms | null {
  const [alarms, setAlarms] = useState<LicenseAlarms | null>(null);
  const refresh = useCallback(async (signal: AbortSignal) => {
    try {
      const answer = await getLicenseAlarms(signal);
      if (signal.aborted) return;
      backgroundRecovered(SURFACE, READ);
      // error-report-exempt: the Gateway's open license alarms, its own verdict (rule 7) - a successful read, nothing failed in this client
      setAlarms(answer);
    } catch (err) {
      if (!signal.aborted) describeAndReport(SURFACE, READ, err, { background: true });
    }
  }, []);
  useVisiblePolling(refresh, LICENSE_ALARM_POLL_MS, enabled);
  return enabled ? alarms : null;
}

// No provider means no answer - the same as "not read yet" - exactly as AlertsProvider's NONE: a page rendered on its
// own in a test draws no triangle and makes no read.
const LicenseAlarmsContext = createContext<LicenseAlarms | null>(null);

export interface LicenseAlarmsProviderProps {
  /** False while the shell is not showing the person's own fleet; no read is made and nothing is drawn. */
  enabled: boolean;
  children: ReactNode;
}

/** The one reader of the license alarms in a shell, mounted above every page. */
export function LicenseAlarmsProvider({ enabled, children }: LicenseAlarmsProviderProps) {
  const alarms = useLicenseAlarms(enabled);
  return <LicenseAlarmsContext.Provider value={alarms}>{children}</LicenseAlarmsContext.Provider>;
}

export interface LicenseAlarmIndicatorProps {
  /** Where it goes: Licenses. */
  href: string;
  /** For a shell with its own router: open `href` in-app instead of loading the page. */
  onOpen?: () => void;
  /** True where there is a pointer to hover with (the Cockpit): hovering or focusing lists each open problem. */
  details?: boolean;
}

/** The triangle for the shell's current answer, or nothing when no alarm is open. */
export function LicenseAlarmIndicator({ href, onOpen, details = false }: LicenseAlarmIndicatorProps) {
  const alarms = useContext(LicenseAlarmsContext);
  return <LicenseAlarmIndicatorView alarms={alarms} href={href} onOpen={onOpen} details={details} />;
}

/** The gap, in pixels, between the triangle and the card below it. */
const CARD_GAP_PX = 6;

/** The triangle for one answer of the Gateway; split out so a test can render an answer without a network. */
export function LicenseAlarmIndicatorView({
  alarms,
  href,
  onOpen,
  details = false,
}: {
  alarms: LicenseAlarms | null;
  href: string;
  onOpen?: () => void;
  details?: boolean;
}) {
  const cardId = useId();
  const [anchor, setAnchor] = useState<{ top: number; left: number } | null>(null);
  if (alarms === null || alarms.count === 0 || alarms.alarms.length === 0) return null;
  const show = (target: HTMLElement) => {
    if (!details) return;
    const r = target.getBoundingClientRect();
    setAnchor({ top: r.bottom + CARD_GAP_PX, left: r.left });
  };
  const hide = () => setAnchor(null);
  const open = (event: MouseEvent<HTMLAnchorElement>) => {
    if (onOpen === undefined) return;
    event.preventDefault();
    onOpen();
  };
  // The Gateway's two phrases, joined as the bar used to show them: "2 license problems - Open Licenses".
  const name = [alarms.summary, alarms.linkText].filter((part): part is string => part !== null).join(" - ");
  return (
    // error-report-exempt: shows the Gateway's own open license alarms and the words it folded (rule 7) - a problem with the owner's licenses, not a failure in this client
    <>
      <a
        className={alarms.tone === null ? "license-alarm-indicator" : `license-alarm-indicator license-alarm-indicator--${alarms.tone}`}
        href={href}
        onClick={open}
        aria-label={name}
        // The card is the Cockpit's tooltip; a native one on top of it would say the same thing twice.
        title={details ? undefined : name}
        aria-describedby={anchor === null ? undefined : cardId}
        data-testid="license-alarm-indicator"
        data-tone={alarms.tone ?? undefined}
        onMouseEnter={(event) => show(event.currentTarget)}
        onMouseLeave={hide}
        onFocus={(event) => show(event.currentTarget)}
        onBlur={hide}
      >
        <TriangleIcon />
        <span className="license-alarm-count" data-testid="license-alarm-count" aria-hidden="true">
          {alarms.count}
        </span>
      </a>
      {anchor !== null && (
        <div
          id={cardId}
          role="tooltip"
          className={alarms.tone === null ? "license-alarm-card" : `license-alarm-card license-alarm-card--${alarms.tone}`}
          style={{ top: anchor.top, left: anchor.left }}
          data-testid="license-alarm-card"
        >
          {alarms.summary !== null && <div className="license-alarm-card-summary">{alarms.summary}</div>}
          {alarms.alarms.map((alarm) => (
            <div key={alarm.alarmId} className="license-alarm-card-line" data-testid="license-alarm-card-line">
              {alarm.text}
            </div>
          ))}
          {alarms.linkText !== null && <div className="license-alarm-card-link">{alarms.linkText}</div>}
        </div>
      )}
    </>
  );
}

function TriangleIcon() {
  return (
    <svg viewBox="0 0 20 20" width="18" height="18" aria-hidden="true" focusable="false">
      <path
        d="M10 2.8 18 16.6a.6.6 0 0 1-.5.9H2.5a.6.6 0 0 1-.5-.9L10 2.8z"
        fill="currentColor"
        fillOpacity="0.18"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinejoin="round"
      />
      <path d="M10 7.6v4.6" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" />
      <circle cx="10" cy="14.7" r="1" fill="currentColor" />
    </svg>
  );
}
