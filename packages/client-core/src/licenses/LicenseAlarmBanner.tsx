import { useCallback, useState, type MouseEvent } from "react";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import { getLicenseAlarms, type LicenseAlarms } from "./licensesClient";

// THE LICENSE ALARM BANNER (License Switching phase 6): one red banner, mounted once in each shell - the Cockpit's frame
// and the phone app's - so it is on every page while a license alarm is open, and gone the moment none is. A problem
// with licenses is meant to be LOUD and to STAY until it is fixed; a banner on one page only would be a window to catch.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). Every word is the Gateway's (GET /gateway/licenses/alarms, folded by
// LicenseAlarmFold): the summary line when there are several, one line per alarm, and the link's words. This decides
// nothing - not whether a problem is serious, not how to phrase it, not when it is over. Count 0 is no banner.
//
// The shell supplies only where the link goes: the Cockpit's own Licenses route, or, on the phone, the Cockpit's page
// (the phone has no Licenses screen of its own). Each shell styles `.license-alarm-banner` in its own sheet.

/** How often the banner asks the Gateway again while the page is visible. */
export const LICENSE_ALARM_POLL_MS = 30_000;

const SURFACE = "license-alarm-banner";
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

export interface LicenseAlarmBannerProps {
  /** False while the shell is not showing the person's own fleet; no read is made and nothing is drawn. */
  enabled: boolean;
  /** Where the link goes. */
  href: string;
  /** For a shell with its own router: open `href` in-app instead of loading the page. */
  onOpen?: () => void;
}

/** The red banner, in the Gateway's words, or nothing when no alarm is open. */
export function LicenseAlarmBanner({ enabled, href, onOpen }: LicenseAlarmBannerProps) {
  const alarms = useLicenseAlarms(enabled);
  return <LicenseAlarmBannerView alarms={alarms} href={href} onOpen={onOpen} />;
}

/** The banner for one answer of the Gateway; split out so a test can render an answer without a network. */
export function LicenseAlarmBannerView({
  alarms,
  href,
  onOpen,
}: {
  alarms: LicenseAlarms | null;
  href: string;
  onOpen?: () => void;
}) {
  if (alarms === null || alarms.count === 0 || alarms.alarms.length === 0) return null;
  const open = (event: MouseEvent<HTMLAnchorElement>) => {
    if (onOpen === undefined) return;
    event.preventDefault();
    onOpen();
  };
  return (
    <div className="license-alarm-banner" role="alert" data-testid="license-alarm-banner">
      {/* error-report-exempt: shows the Gateway's own open license alarms and the words it folded (rule 7) - a problem with the owner's licenses, not a failure in this client */}
      <div className="license-alarm-banner-text">
        {alarms.summary !== null && (
          <p className="license-alarm-banner-summary" data-testid="license-alarm-summary">
            {alarms.summary}
          </p>
        )}
        <ul className="license-alarm-banner-lines">
          {alarms.alarms.map((alarm) => (
            <li key={alarm.alarmId} data-testid="license-alarm-line">
              {alarm.text}
            </li>
          ))}
        </ul>
      </div>
      {alarms.linkText !== null && (
        <a className="license-alarm-banner-link" href={href} onClick={open} data-testid="license-alarm-link">
          {alarms.linkText}
        </a>
      )}
    </div>
  );
}
