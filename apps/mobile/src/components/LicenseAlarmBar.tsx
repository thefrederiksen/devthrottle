import { useEffect, useRef } from "react";
import { LicenseAlarmBannerView, useLicenseAlarms } from "@devthrottle/client-core/licenses/LicenseAlarmBanner";

// THE LICENSE ALARM ON THE PHONE (License Switching phase 6): the shared red banner, in the Gateway's words, on every
// gated screen while a license alarm is open. Its link goes to the Cockpit's Licenses page (/licenses, outside the
// /mobile app), because the phone has no Licenses screen of its own.
//
// IT PUBLISHES ITS OWN MEASURED HEIGHT AS --licensealarm-h, exactly as the recording banner publishes --recbanner-h and
// for the same reason: the pinned screens offset themselves by --topbars-h, the sum of every top bar, and a bar that
// does not add its height paints over their headers. Zero when no alarm is open, and then nothing is drawn at all.
export function LicenseAlarmBar() {
  const alarms = useLicenseAlarms(true);
  const barRef = useRef<HTMLDivElement | null>(null);
  const shown = alarms !== null && alarms.count > 0;

  useEffect(() => {
    const root = document.documentElement;
    if (!shown) {
      root.style.setProperty("--licensealarm-h", "0px");
      return;
    }
    const el = barRef.current;
    if (el === null) return;
    const apply = () => root.style.setProperty("--licensealarm-h", `${Math.round(el.getBoundingClientRect().height)}px`);
    apply();
    const observer = new ResizeObserver(apply);
    observer.observe(el);
    return () => {
      observer.disconnect();
      root.style.setProperty("--licensealarm-h", "0px");
    };
  }, [shown]);

  if (!shown) return null;
  return (
    <div className="licensealarm-bar" ref={barRef}>
      <LicenseAlarmBannerView alarms={alarms} href="/licenses" />
    </div>
  );
}
