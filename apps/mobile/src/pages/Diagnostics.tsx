import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import {
  getNetDiagEcho,
  measureLatency,
  measureDownload,
  measureUpload,
  getNetworkDiag,
  postNetDiagResult,
  type NetDiagEcho,
  type ThroughputResult,
  type NetworkDiag,
  type NetDiagPeer,
} from "@devthrottle/client-core/api/client";
import { describeAndReport, describeReadAndReport } from "@devthrottle/client-core/errors/reportClientError";

// Diagnostics (Network Diagnostics mission): a phone-side connection tester. A phone cannot run
// `tailscale ping`, so this measures the phone-to-Gateway path from the phone itself - route, latency,
// throughput - and then asks the Gateway for the AUTHORITATIVE Tailscale state (direct vs DERP relay) for
// this exact connection, so the verdict distinguishes "warming up on the relay" from "genuinely slow".

const DOWNLOAD_BYTES = 4 * 1024 * 1024;
const UPLOAD_BYTES = 2 * 1024 * 1024;
const LATENCY_SAMPLES = 6;

type Phase = "idle" | "latency" | "download" | "upload" | "checking" | "done";

export function Diagnostics() {
  const [echo, setEcho] = useState<NetDiagEcho | null>(null);
  const [echoError, setEchoError] = useState<string | null>(null);

  const [phase, setPhase] = useState<Phase>("idle");
  const [latency, setLatency] = useState<number[] | null>(null);
  const [download, setDownload] = useState<ThroughputResult | null>(null);
  const [upload, setUpload] = useState<ThroughputResult | null>(null);
  const [network, setNetwork] = useState<NetworkDiag | null>(null);
  const [testError, setTestError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    getNetDiagEcho(controller.signal)
      .then((e) => {
        setEcho(e);
        setEchoError(null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) setEchoError(describeReadAndReport("mobile-diagnostics", "read the connection check", err));
      });
    return () => controller.abort();
  }, []);

  const running = phase !== "idle" && phase !== "done";

  const medianLatency = useMemo(() => (latency ? median(latency) : null), [latency]);
  // The Gateway's authoritative view of THIS phone's Tailscale path, matched by the IP the Gateway sees.
  const selfPeer = useMemo(() => findSelfPeer(network, echo), [network, echo]);
  const route = useMemo(() => describeRoute(echo, selfPeer), [echo, selfPeer]);
  const verdict = useMemo(
    () => buildVerdict(route.kind, selfPeer, medianLatency, download?.mbps ?? null),
    [route.kind, selfPeer, medianLatency, download],
  );

  async function runSpeedTest() {
    setTestError(null);
    setLatency(null);
    setDownload(null);
    setUpload(null);
    setNetwork(null);
    try {
      setPhase("latency");
      const timings = await measureLatency(LATENCY_SAMPLES);
      setLatency(timings);

      setPhase("download");
      const dl = await measureDownload(DOWNLOAD_BYTES);
      setDownload(dl);

      setPhase("upload");
      const ul = await measureUpload(UPLOAD_BYTES);
      setUpload(ul);

      // Ask the Gateway for the authoritative direct-vs-relay state. Non-fatal: if it is unavailable we
      // still show the measured numbers and fall back to the heuristic verdict.
      setPhase("checking");
      let net: NetworkDiag | null = null;
      try {
        net = await getNetworkDiag();
        setNetwork(net);
      } catch {
        /* leave network null; verdict falls back to the measured heuristic */
      }

      setPhase("done");

      // Log the result to the Gateway so an agent can read it later. Best-effort, never blocks the page.
      const self = findSelfPeer(net, echo);
      const v = buildVerdict(describeRoute(echo, self).kind, self, median(timings), dl.mbps);
      void postNetDiagResult({
        route: echo?.clientPath ?? "other",
        latencyMedianMs: median(timings),
        latencyBestMs: Math.min(...timings),
        latencySamples: timings.length,
        downloadMbps: dl.mbps,
        uploadMbps: ul.mbps,
        rating: v?.rating ?? "",
        verdict: v?.headline ?? "",
        loadedFrom: typeof window !== "undefined" ? window.location.host : "",
        surface: "mobile",
        // Actual-path tags from the authoritative self-peer, so the Gateway folds this into the right
        // home(LAN-direct)/away(relay) sub-sum - never by the front-door route.
        direct: self?.direct ?? null,
        isLanPath: pathIsLan(self?.path ?? null),
      }).catch(() => {
        /* logging is best-effort */
      });
    } catch (err) {
      setTestError(describeAndReport("mobile-diagnostics", "run the speed test", err));
      setPhase("idle");
    }
  }

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Diagnostics</h1>
      </header>

      {echoError !== null && (
        <div className="banner banner-error" role="alert">
          {echoError}
        </div>
      )}

      <section className={`diag-route diag-route-${route.kind}`} aria-label="Connection route">
        <div className="diag-route-label">Connection route</div>
        <div className="diag-route-value">{route.title}</div>
        <div className="diag-route-detail">{route.detail}</div>
      </section>

      <button type="button" className="diag-run" onClick={runSpeedTest} disabled={running}>
        {running ? phaseLabel(phase) : latency ? "Run speed test again" : "Run speed test"}
      </button>

      {testError !== null && (
        <div className="banner banner-error" role="alert">
          {testError}
        </div>
      )}

      {(latency || download || upload) && (
        <section className="diag-metrics" aria-label="Speed results">
          <Metric
            label="Latency (round trip)"
            value={medianLatency !== null ? `${formatMs(medianLatency)} ms` : pending(phase, "latency")}
            sub={latency ? `best ${formatMs(Math.min(...latency))} ms of ${latency.length}` : ""}
          />
          <Metric
            label="Download"
            value={download ? `${formatMbps(download.mbps)} Mbps` : pending(phase, "download")}
            sub={download ? `${formatMs(download.ms)} ms for ${formatMib(download.bytes)}` : ""}
          />
          <Metric
            label="Upload"
            value={upload ? `${formatMbps(upload.mbps)} Mbps` : pending(phase, "upload")}
            sub={upload ? `${formatMs(upload.ms)} ms for ${formatMib(upload.bytes)}` : ""}
          />
        </section>
      )}

      {verdict && phase === "done" && (
        <section className={`diag-verdict diag-verdict-${verdict.rating}`} aria-label="Verdict">
          <div className="diag-verdict-headline">{verdict.headline}</div>
          {verdict.advice !== "" && <p className="diag-verdict-advice">{verdict.advice}</p>}
        </section>
      )}

      {verdict && phase === "done" && verdict.showChecklist && (
        <section className="diag-checklist" aria-label="How to make it faster">
          <div className="diag-checklist-title">How to make it faster</div>
          <ul>
            <li>Keep this app open and re-run in a few seconds - a fresh connection starts on a relay and speeds up once it goes direct.</li>
            <li>In the Tailscale app, make sure this phone has granted local-network access.</li>
            <li>Do not route your home traffic through a Tailscale exit node.</li>
            <li>On your router, turn off wireless client isolation and allow UDP (enable UPnP) so a direct path can form.</li>
          </ul>
        </section>
      )}

      <section className="about-list" aria-label="Connection details">
        <DetailRow label="Loaded from" value={typeof window !== "undefined" ? window.location.host : ""} />
        <DetailRow label="Gateway sees you as" value={echo ? `${echo.clientIp ?? "?"} (${echo.clientPath})` : "Loading..."} />
        {selfPeer && (
          <DetailRow
            label="Tailscale path"
            value={selfPeer.direct === true ? `direct ${selfPeer.path ?? ""} (${fmt(selfPeer.latencyMs)} ms)` : selfPeer.direct === false ? `relayed via ${selfPeer.path ?? "DERP"}` : "unknown"}
          />
        )}
        <DetailRow label="Gateway machine" value={echo?.machineName ?? "Loading..."} />
        <DetailRow label="Gateway LAN address" value={echo?.gatewayLanIp ?? "unknown"} />
        <DetailRow label="Gateway Tailscale name" value={echo?.gatewayTailnetName ?? "unknown"} />
      </section>
    </div>
  );
}

function Metric({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <div className="diag-metric">
      <div className="diag-metric-label">{label}</div>
      <div className="diag-metric-value">{value}</div>
      {sub !== "" && <div className="diag-metric-sub">{sub}</div>}
    </div>
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="about-row">
      <span className="about-label">{label}</span>
      <span className="about-value">{value}</span>
    </div>
  );
}

type RouteKind = "lan" | "tailscale" | "local" | "other" | "unknown";

// Find the Gateway's view of THIS phone in the network diagnostic, matched by the IP the Gateway sees for
// us (echo.clientIp is our tailnet address, which appears as a peer's tailscaleIp).
function findSelfPeer(network: NetworkDiag | null, echo: NetDiagEcho | null): NetDiagPeer | null {
  if (!network || !echo?.clientIp) return null;
  return network.peers.find((p) => p.tailscaleIp === echo.clientIp) ?? null;
}

function describeRoute(echo: NetDiagEcho | null, selfPeer: NetDiagPeer | null): { kind: RouteKind; title: string; detail: string } {
  if (echo === null) {
    return { kind: "unknown", title: "Checking...", detail: "Asking the Gateway how it sees this connection." };
  }
  switch (echo.clientPath) {
    case "lan":
      return { kind: "lan", title: "Direct on your LAN", detail: "The phone is talking straight to the Gateway over the local network - the fastest path." };
    case "tailscale": {
      // With the authoritative check in hand we can say direct-over-Tailscale vs relayed.
      if (selfPeer?.direct === true)
        return { kind: "tailscale", title: "Through Tailscale (direct)", detail: `A direct peer-to-peer path over your LAN (${selfPeer.path ?? ""}). This is the good state.` };
      if (selfPeer?.direct === false)
        return { kind: "tailscale", title: "Through Tailscale (relayed)", detail: "Traffic is relaying through a distant DERP server instead of going direct - this is what makes it slow." };
      return { kind: "tailscale", title: "Through Tailscale", detail: "Traffic is riding the Tailscale network. On the same home network this should connect directly, not relay." };
    }
    case "local":
      return { kind: "local", title: "On the Gateway machine", detail: "This browser is on the same machine as the Gateway (loopback)." };
    default:
      return { kind: "other", title: "Remote / other", detail: "The Gateway sees a public or unrecognized address for this connection." };
  }
}

type Rating = "fast" | "ok" | "slow";

function buildVerdict(
  kind: RouteKind,
  selfPeer: NetDiagPeer | null,
  medianMs: number | null,
  downMbps: number | null,
): { rating: Rating; headline: string; advice: string; showChecklist: boolean } | null {
  if (medianMs === null || downMbps === null) return null;
  const rating: Rating =
    medianMs < 30 && downMbps >= 50 ? "fast" : medianMs < 100 && downMbps >= 10 ? "ok" : "slow";
  const speedWord = rating === "fast" ? "fast" : rating === "ok" ? "usable" : "slow";
  const numbers = `median ${formatMs(medianMs)} ms, ${formatMbps(downMbps)} Mbps down`;

  // Authoritative: the Gateway told us whether Tailscale is direct or relayed for this phone.
  if (selfPeer?.direct === false) {
    return {
      rating,
      headline: `Relaying through Tailscale and it is ${speedWord} (${numbers}).`,
      advice:
        "Tailscale is routing this connection through a distant relay server instead of a direct path over your LAN. That is the cause of the slowness.",
      showChecklist: true,
    };
  }
  if (selfPeer?.direct === true) {
    // Direct now. If the measured numbers were slow, the test caught the brief cold-start relay window.
    if (rating === "slow") {
      return {
        rating: "ok",
        headline: `Tailscale is now on a DIRECT LAN path (${fmt(selfPeer.latencyMs)} ms), but the speed test caught the slow start (${numbers}).`,
        advice: "A fresh connection begins on a relay and upgrades to direct after a moment. Run the test again now - it should be much faster.",
        showChecklist: false,
      };
    }
    return {
      rating,
      headline: `Direct Tailscale path over your LAN and it is ${speedWord} (${numbers}).`,
      advice: "",
      showChecklist: false,
    };
  }

  // No authoritative signal - fall back to the measured heuristic.
  if (kind === "tailscale" && rating !== "fast") {
    return {
      rating,
      headline: `Connected through Tailscale and it is ${speedWord} (${numbers}).`,
      advice: "On the same home network this is usually the brief cold-start relay before Tailscale finds the direct path. Re-run in a few seconds.",
      showChecklist: true,
    };
  }
  if (kind === "lan") {
    return {
      rating,
      headline: `Direct LAN connection and it is ${speedWord} (${numbers}).`,
      advice: rating === "slow" ? "A direct LAN path is slow here - likely weak Wi-Fi signal or a busy network." : "",
      showChecklist: false,
    };
  }
  return { rating, headline: `Connection is ${speedWord} (${numbers}).`, advice: "", showChecklist: false };
}

function phaseLabel(phase: Phase): string {
  if (phase === "latency") return "Testing latency...";
  if (phase === "download") return "Testing download...";
  if (phase === "upload") return "Testing upload...";
  if (phase === "checking") return "Checking Tailscale...";
  return "Running...";
}

function pending(phase: Phase, forStep: "latency" | "download" | "upload"): string {
  return phase === forStep ? "Testing..." : "-";
}

function median(values: number[]): number {
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
}

function fmt(ms: number | null): string {
  return ms === null ? "?" : formatMs(ms);
}

function formatMs(ms: number): string {
  return ms >= 100 ? String(Math.round(ms)) : ms.toFixed(1);
}

function formatMbps(mbps: number): string {
  return mbps >= 100 ? String(Math.round(mbps)) : mbps.toFixed(1);
}

function formatMib(bytes: number): string {
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

// True when a self-peer path (e.g. "192.168.1.15:52091") is a private LAN address; "DERP(...)" / null are not.
function pathIsLan(path: string | null): boolean {
  if (!path || path.startsWith("DERP")) return false;
  const b = path.split(":")[0].split(".");
  if (b.length !== 4) return false;
  const o0 = Number(b[0]);
  const o1 = Number(b[1]);
  if (o0 === 10) return true;
  if (o0 === 172 && o1 >= 16 && o1 <= 31) return true;
  if (o0 === 192 && o1 === 168) return true;
  if (o0 === 169 && o1 === 254) return true;
  return false;
}
