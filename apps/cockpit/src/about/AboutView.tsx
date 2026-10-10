import { useEffect, useState } from "react";
import { getAbout, type AboutInfo, type BundleStamp } from "@devthrottle/client-core/about/aboutClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-about";

// The About page: the version of each of the three SERVER-SIDE products - this Gateway, the Cockpit
// bundle it serves, and the mobile app bundle it serves - plus how the Gateway is reached, from
// GET /gateway/about.
//
// The Director is deliberately absent (owner ruling 2026-07-26): it has its own About box and its own
// Cockpit screen, so this page is about server versions. Gone with it: the install root (which printed
// the Gateway box's operating-system user name to any enrolled device), the machine name, the run mode,
// and the installer's component manifest - all internal detail about infrastructure a hosted caller does
// not own.
//
// Responsive (CodingStyle.md): renders immediately with a loading state and loads asynchronously; on a
// load failure it shows an explicit error banner (the no-fallback rule).

/** One label/value row. */
function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="abt-row">
      <div className="abt-row-label">{label}</div>
      <div className="abt-row-value">{value}</div>
    </div>
  );
}

// Compact "1d 2h 3m" uptime, from seconds since the Gateway process started.
export function formatUptime(totalSeconds: number): string {
  if (totalSeconds <= 0) return "just started";
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const parts: string[] = [];
  if (days > 0) parts.push(`${days}d`);
  if (hours > 0) parts.push(`${hours}h`);
  parts.push(`${minutes}m`);
  return parts.join(" ");
}

// Format an ISO timestamp to the compact "yyyy-MM-dd HH:mm:ss" (UTC) this page shows. A missing value is
// reported as unknown and an unparseable one is shown verbatim - never a fabricated time.
export function formatServerTime(iso: string): string {
  if (iso.length === 0) return "(unknown)";
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return iso;
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${parsed.getUTCFullYear()}-${pad(parsed.getUTCMonth() + 1)}-${pad(parsed.getUTCDate())} ` +
    `${pad(parsed.getUTCHours())}:${pad(parsed.getUTCMinutes())}:${pad(parsed.getUTCSeconds())}`
  );
}

/**
 * One served bundle's version line: the commit it was built from, and when. A bundle with no stamp says
 * so plainly - a Gateway built without the web apps (a routine Debug build) serves no bundle at all, and
 * naming a build it does not have would be worse than admitting it.
 */
export function formatBundle(stamp: BundleStamp | null | undefined): string {
  if (!stamp) return "(not built into this Gateway)";
  const built = stamp.buildTime ?? "";
  if (built.length === 0) return stamp.commit;
  return `${stamp.commit} (built ${formatServerTime(built)} UTC)`;
}

export function AboutView() {
  const [about, setAbout] = useState<AboutInfo | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        setAbout(await getAbout(controller.signal));
      } catch (err) {
        if (controller.signal.aborted) return;
        setError(describeAndReport(SURFACE, "read the versions this Gateway is serving", err));
      }
    })();
    return () => controller.abort();
  }, []);

  return (
    <div className="page abt">
      <div className="page-head">
        <h1>About DevThrottle</h1>
      </div>
      <p className="abt-lede">The versions this Gateway is serving, and how it is reached.</p>
      {/* The build of the bundle THIS TAB is running, stamped into the static files at build time (see
          vite.config.ts). Shown always - even when the Gateway call below fails - so you can always
          identify the page you are looking at. It is a different fact from the "Cockpit" row below, which
          is the bundle the Gateway currently SERVES: when the two disagree, this tab is stale and a reload
          will move it forward. */}
      <p className="abt-cockpit-build">
        This browser tab is running cockpit <strong>{__COCKPIT_COMMIT__}</strong> (built{" "}
        {formatServerTime(__COCKPIT_BUILD_TIME__)} UTC)
      </p>

      {error !== null ? (
        <div className="abt-error">Could not load About info from the Gateway: {error}</div>
      ) : about === null ? (
        <p className="abt-loading">Loading...</p>
      ) : (
        <>
          <h2 className="abt-h2">VERSIONS</h2>
          <div className="abt-card">
            <Row label="Gateway" value={about.version} />
            <Row label="Gateway built" value={about.buildDate ?? "(unknown)"} />
            {/* The two served bundles as the GATEWAY sees them on disk, not as the browser running this
                page sees itself. That distinction is the point: a Cockpit-only redeploy replaces the bundle
                under a live Gateway and an already-open tab keeps running the OLD one, so reading the
                server means this page reports what is DEPLOYED. */}
            <Row label="Cockpit" value={formatBundle(about.cockpit)} />
            <Row label="Mobile app" value={formatBundle(about.mobile)} />
          </div>

          <h2 className="abt-h2">THIS GATEWAY</h2>
          <div className="abt-card">
            <Row label="Deployment" value={about.deployment} />
            <Row label="Address" value={about.address ?? "(Tailscale unavailable)"} />
            <Row label="Cockpit URL" value={about.cockpitUrl ?? "(Tailscale unavailable)"} />
            {/* Rendered only when the Gateway hands one over. It does not on the hosted service, where
                clients arrive through the address above on 443 and the internal port composes with nothing
                a caller could use. The client does not decide that - it renders what it is given
                (CLAUDE.md rule 7). */}
            {typeof about.port === "number" && <Row label="Listening on port" value={String(about.port)} />}
            <Row label="Uptime" value={formatUptime(about.uptimeSeconds)} />
            <Row label="Gateway time (UTC)" value={formatServerTime(about.serverTime)} />
          </div>
        </>
      )}
    </div>
  );
}
