import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { getFleetCounts, getGatewayHealth, type FleetCounts, type GatewayHealth } from "@devthrottle/client-core/api/client";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { AppBarAlerts } from "../components/AppBarAlerts";

const SURFACE = "mobile-about";
const READ = "read the Gateway's version";
const READ_COUNTS = "read how many Directors and sessions you have";

export function About() {
  const [health, setHealth] = useState<GatewayHealth | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [counts, setCounts] = useState<FleetCounts | null>(null);
  const [countsError, setCountsError] = useState<string | null>(null);
  const sw = serviceWorkerState();
  const displayMode = isStandalone() ? "Installed PWA" : "Browser tab";

  useEffect(() => {
    const controller = new AbortController();
    getGatewayHealth(controller.signal)
      .then((h) => {
        setHealth(h);
        backgroundRecovered(SURFACE, READ);
        setError(null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) setError(describeAndReport(SURFACE, READ, err, { background: true }));
      });
    // The counts come from their own signed-in read, not from /healthz: the public probe has no account, so the
    // hosted Gateway leaves its counts out, and this page used to say "Not reported" for both (Web UI Polish phase 3).
    getFleetCounts(controller.signal)
      .then((c) => {
        setCounts(c);
        backgroundRecovered(SURFACE, READ_COUNTS);
        setCountsError(null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) setCountsError(describeAndReport(SURFACE, READ_COUNTS, err, { background: true }));
      });
    return () => controller.abort();
  }, []);

  const version = health?.version ?? "Loading...";
  const sha = shortSha(version);

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>About</h1>
        <div className="app-bar-spacer" />
        <AppBarAlerts />
      </header>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {/* error-report-exempt: the failure was reported by describeAndReport where it was caught. */}
          {error}
        </div>
      )}
      {countsError !== null && (
        <div className="banner banner-error" role="alert">
          {/* error-report-exempt: the failure was reported by describeAndReport where it was caught. */}
          {countsError}
        </div>
      )}

      <section className="about-panel" aria-label="Application version">
        <div className="about-product">DevThrottle Mobile</div>
        <div className="about-version">{version}</div>
        {sha !== "" && <div className="about-sha">Build {sha}</div>}
      </section>

      <section className="about-list" aria-label="Runtime details">
        {/* The build this app was made from, stamped in at build time (see vite.config.ts). It replaced
            the content-hashed script filename this row used to scrape out of the live DOM: that hash
            changed on every build and named nothing you could look up, so it could not answer the one
            question the row is for - which build am I running. */}
        <AboutRow label="Mobile app build" value={mobileBuild()} />
        <AboutRow label="Gateway status" value={health?.status ?? "Loading..."} />
        <AboutRow label="Gateway time" value={formatTime(health?.serverTime)} />
        <AboutRow label="Directors" value={formatCount(counts?.directors, countsError)} />
        <AboutRow label="Sessions" value={formatCount(counts?.sessions, countsError)} />
        <AboutRow label="Service worker" value={sw} />
        <AboutRow label="Display" value={displayMode} />
      </section>
    </div>
  );
}

function AboutRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="about-row">
      <span className="about-label">{label}</span>
      <span className="about-value">{value}</span>
    </div>
  );
}

/** This app's build: the commit it was built from and when, from the build-time stamp. */
function mobileBuild(): string {
  const built = new Date(__MOBILE_BUILD_TIME__);
  if (Number.isNaN(built.getTime())) return __MOBILE_COMMIT__;
  return `${__MOBILE_COMMIT__} (${built.toLocaleString()})`;
}

function serviceWorkerState(): string {
  if (typeof navigator === "undefined" || !("serviceWorker" in navigator)) return "not supported";
  return navigator.serviceWorker.controller ? "active" : "not controlling yet";
}

function isStandalone(): boolean {
  if (typeof window === "undefined") return false;
  const nav = navigator as Navigator & { standalone?: boolean };
  return window.matchMedia("(display-mode: standalone)").matches || Boolean(nav.standalone);
}

function shortSha(version: string): string {
  const plus = version.indexOf("+");
  if (plus < 0) return "";
  return version.substring(plus + 1, plus + 8);
}

/**
 * A fleet count for the About list, from GET /fleet/counts. The number once it has arrived; "Could not read"
 * when the read failed (the banner above says why); "Loading..." until then. Never a substituted 0 - a zero
 * here would read as "your fleet is empty" when the truth is "this page does not know".
 */
export function formatCount(value: number | undefined, failed: string | null): string {
  if (value !== undefined) return String(value);
  return failed !== null ? "Could not read" : "Loading...";
}

function formatTime(value: string | undefined): string {
  if (!value) return "Loading...";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}
