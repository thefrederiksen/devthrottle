import { useCallback, useEffect, useState } from "react";
import {
  getMicrophoneQualityDetail,
  type MicrophoneDeviceDetail,
  type MicrophoneMeasurement,
  type MicrophoneQualityDetail,
  type MicrophoneTrendPoint,
} from "./microphoneQualityClient";
import { reportShownError } from "../errors/reportClientError";
import "./microphoneQuality.css";

const LOAD_FAILED = "Couldn't load microphone quality from the Gateway.";

// "How your microphones are doing" - shared by the Cockpit's Transcription Health page and the
// Transcription settings tab on both surfaces. This is the DETAILED view (issue #2183): per
// microphone the platform it lives on, every measurement with the targets beside it, and the
// quality-over-time trend - the daily email stays a summary and points here.
//
// This is the BACKGROUND half of microphone quality: every dictation is measured automatically as it
// is sent, so this screen answers the question the on-demand Test microphone check cannot - which of
// your microphones, on which machine, is letting you down, and whether it is getting better or worse.
//
// Every verdict, every sentence of advice, the platform classification and the target figures are
// decided on the Gateway (MicrophoneQualityFold) and rendered here verbatim. This component contains
// no thresholds: it draws the numbers the Gateway sent next to the targets the Gateway sent.
//
// It moved out of apps/cockpit when Settings was unified across the two surfaces: the phone shows the
// identical panel now, so it cannot live in one app's tree. Its styling came with it and is
// self-contained (mq-* only), rather than borrowing the health page's txh-* classes as it used to -
// the phone never loads that stylesheet.

function formatDb(value: number): string {
  return `${Math.round(value)} dB`;
}

function formatShare(share: number): string {
  return `${Math.round(share * 100)}%`;
}

function formatWhen(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
}

export function MicrophoneQualityPanel() {
  const [detail, setDetail] = useState<MicrophoneQualityDetail | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setLoadError(null);
      setDetail(await getMicrophoneQualityDetail(30, signal));
    } catch (err) {
      if (signal?.aborted) return;
      setLoadError(reportShownError("microphone-quality", "load microphone quality", LOAD_FAILED, undefined, err));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  return (
    <div className="mq">
      <h2 className="mq-title">How your microphones are doing</h2>
      <p className="mq-lede">
        Every dictation you send is measured automatically, so a microphone that is quietly spoiling
        your transcripts shows up here without you having to go looking. Nothing is recorded - only how
        the audio measured.
      </p>

      {loadError ? (
        <div className="mq-load-error">
          {loadError}
          <button type="button" className="mq-retry" onClick={() => void load()}>
            Retry
          </button>
        </div>
      ) : detail === null ? (
        <div className="mq-muted">Loading...</div>
      ) : (
        <>
          <div className={`mq-banner mq-${detail.status}`}>{detail.headline}</div>
          <p className="mq-lede">{detail.detail}</p>

          {detail.devices.length > 0 && (
            <div className="mq-devices">
              {detail.devices.map((d) => (
                <DeviceCard key={d.summary.deviceId !== "" ? d.summary.deviceId : d.summary.device} device={d} />
              ))}
            </div>
          )}

          {detail.totalSamples > 0 && (
            <p className="mq-muted">
              Based on {detail.totalSamples} measured{" "}
              {detail.totalSamples === 1 ? "dictation" : "dictations"} over the last 30 days.
            </p>
          )}
        </>
      )}
    </div>
  );
}

function DeviceCard({ device }: { device: MicrophoneDeviceDetail }) {
  const s = device.summary;
  return (
    <div className={`mq-device mq-device-${s.status}`}>
      <div className="mq-device-head">
        <span className="mq-device-name">{s.device}</span>
        {/* The platform is folded on the Gateway; an unknown platform arrives as an empty label and
            renders as nothing rather than a guess. */}
        {s.platformLabel !== "" && <span className="mq-device-platform">{s.platformLabel}</span>}
        <span className="mq-device-count">
          {s.samples} {s.samples === 1 ? "dictation" : "dictations"}
        </span>
      </div>
      <p className="mq-device-advice">{s.advice}</p>
      <dl className="mq-measures">
        <div>
          <dt>Your voice</dt>
          <dd>{formatDb(s.medianSpeechLevelDb)}</dd>
          {/* The target sits beside the reading rather than in a legend, so the comparison needs no
              second look and no knowledge of what a good number is. */}
          <span className="mq-target">good is about {formatDb(s.targetSpeechLevelDb)}</span>
        </div>
        <div>
          <dt>Voice above the room</dt>
          <dd>{formatDb(s.medianSignalToNoiseDb)}</dd>
          <span className="mq-target">good is {formatDb(s.targetSignalToNoiseDb)} or more</span>
        </div>
        <div>
          <dt>Telephone quality</dt>
          <dd>{formatShare(s.narrowbandShare)}</dd>
          <span className="mq-target">of dictations</span>
        </div>
        <div>
          <dt>Distorting</dt>
          <dd>{formatShare(s.clippingShare)}</dd>
          <span className="mq-target">of dictations</span>
        </div>
      </dl>
      <TrendChart trend={device.trend} target={s.targetSignalToNoiseDb} />
      <MeasurementList measurements={device.measurements} total={device.measurementsTotal} />
    </div>
  );
}

// The quality-over-time chart: the daily median of "voice above the room", drawn against the
// Gateway's target line. One glance answers "is this microphone getting better or worse". Pure
// layout: the values and the target both arrive from the Gateway.
function TrendChart({ trend, target }: { trend: MicrophoneTrendPoint[]; target: number }) {
  // A trend needs at least two days; a single day is already told by the numbers above.
  if (trend.length < 2) return null;

  const width = 260;
  const height = 64;
  const pad = 4;
  const values = trend.map((p) => p.medianSignalToNoiseDb);
  const lo = Math.min(...values, target);
  const hi = Math.max(...values, target);
  const span = hi - lo || 1;
  const x = (i: number) => pad + (i * (width - 2 * pad)) / (trend.length - 1);
  const y = (v: number) => height - pad - ((v - lo) * (height - 2 * pad)) / span;
  const line = trend
    .map((p, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(1)},${y(p.medianSignalToNoiseDb).toFixed(1)}`)
    .join(" ");

  const first = trend[0];
  const last = trend[trend.length - 1];
  return (
    <div className="mq-trend">
      <div className="mq-trend-title">Voice above the room, day by day</div>
      <svg
        className="mq-trend-chart"
        viewBox={`0 0 ${width} ${height}`}
        preserveAspectRatio="none"
        role="img"
        aria-label={`Daily median of voice above the room, from ${formatDb(first.medianSignalToNoiseDb)} on ${first.date} to ${formatDb(last.medianSignalToNoiseDb)} on ${last.date}. Good is ${formatDb(target)} or more.`}
      >
        <line
          className="mq-trend-target"
          x1={pad}
          x2={width - pad}
          y1={y(target)}
          y2={y(target)}
          strokeDasharray="4 3"
        />
        <path className="mq-trend-line" d={line} fill="none" />
      </svg>
      <div className="mq-trend-legend">
        <span>{first.date}</span>
        <span className="mq-target">dashed line is the {formatDb(target)} target</span>
        <span>{last.date}</span>
      </div>
    </div>
  );
}

// Every measurement behind the verdict, newest first, folded open on demand - the evidence table for
// anyone who wants to see exactly which dictation measured how.
function MeasurementList({ measurements, total }: { measurements: MicrophoneMeasurement[]; total: number }) {
  const [open, setOpen] = useState(false);
  if (measurements.length === 0) return null;
  return (
    <div className="mq-history">
      <button type="button" className="mq-history-toggle" onClick={() => setOpen((v) => !v)}>
        {open
          ? "Hide the measurements"
          : `Show ${measurements.length === 1 ? "the measurement" : `all ${measurements.length} measurements`}`}
      </button>
      {open && (
        <>
          <div className="mq-history-scroll">
            <table className="mq-history-table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Length</th>
                  <th>Voice</th>
                  <th>Above room</th>
                  <th>Distorted</th>
                  <th>Verdict</th>
                </tr>
              </thead>
              <tbody>
                {measurements.map((m) => (
                  <tr key={`${m.timestampUtc}-${m.source}`}>
                    <td>{formatWhen(m.timestampUtc)}</td>
                    <td>{Math.round(m.durationSeconds)} s</td>
                    <td>{formatDb(m.speechLevelDb)}</td>
                    <td>{formatDb(m.signalToNoiseDb)}</td>
                    <td>{formatShare(m.clippedFraction)}</td>
                    {/* The rating and its issue list are the Gateway's words, printed as sent. */}
                    <td>{m.issues !== "" ? `${m.rating} (${m.issues})` : m.rating}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {total > measurements.length && (
            <p className="mq-muted">
              Showing the newest {measurements.length} of {total} measurements in the window.
            </p>
          )}
        </>
      )}
    </div>
  );
}
