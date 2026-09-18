import { useEffect, useRef, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { recordingSession, useRecordingSession } from "@devthrottle/client-core/recorder/recordingSession";

// THE RECORDING BANNER (recorder-unlimited-capture mission). While a recording is live, this bar is
// on EVERY screen of the app: the capture session lives above the router precisely so navigating
// away from the Recorder page cannot stop it, and a capture that survives navigation needs an
// indicator that survives navigation with it. A glance at the top of the screen answers "is it
// still recording?" - the mission's third ask - with the live elapsed clock as proof it is not a
// stale banner. Tapping it lands on the Recorder page, where Stop is.
//
// On the Recorder page itself it renders nothing: that page's status card already shows the same
// state larger, and doubling it would burn a strip of a small screen to say one thing twice.
//
// IT PUBLISHES ITS OWN MEASURED HEIGHT AS --recbanner-h, exactly like the voice-mode banner
// publishes --voicemode-h and for the same reason: the pinned screens (.terminal-screen)
// are fixed out of the document flow and offset themselves by --topbars-h, the
// sum of every top bar. A bar that does not add its height to that sum paints OVER those screens'
// own headers (shipped and reported for the voice-mode banner on 2026-07-25 - "there's a black
// space at the top" was the over-correction; losing the header was the original sin). Zero when
// not shown, so nothing about any screen changes when no recording is running.
function formatClock(ms: number): string {
  const totalS = Math.floor(ms / 1000);
  const h = Math.floor(totalS / 3600);
  const m = Math.floor((totalS % 3600) / 60);
  const s = totalS % 60;
  const two = (n: number) => String(n).padStart(2, "0");
  return `${two(h)}:${two(m)}:${two(s)}`;
}

export function RecordingBanner() {
  const session = useRecordingSession();
  const location = useLocation();
  const navigate = useNavigate();
  const barRef = useRef<HTMLButtonElement | null>(null);
  const [clock, setClock] = useState("00:00:00");

  const capturing = session.phase === "recording" || session.phase === "paused" || session.phase === "stopping";
  // A capture that DIED (microphone suspended) must be announced everywhere too, not just on the
  // Recorder page - otherwise the red bar silently vanishes and the user in another screen assumes
  // it is still recording. The error state shows until dismissed on the Recorder page.
  const lost = !capturing && session.error !== null;
  const onRecorderPage = location.pathname === "/recorder" || location.pathname.startsWith("/recorder/");
  const shown = (capturing || lost) && !onRecorderPage;

  useEffect(() => {
    const root = document.documentElement;
    if (!shown) {
      root.style.setProperty("--recbanner-h", "0px");
      return;
    }
    const el = barRef.current;
    if (el === null) return;
    const apply = () => root.style.setProperty("--recbanner-h", `${Math.round(el.getBoundingClientRect().height)}px`);
    apply();
    const observer = new ResizeObserver(apply);
    observer.observe(el);
    return () => {
      observer.disconnect();
      root.style.setProperty("--recbanner-h", "0px");
    };
  }, [shown]);

  // The live clock is the banner's proof of life - a frozen number would be a lying indicator.
  useEffect(() => {
    if (!shown || !capturing) return;
    setClock(formatClock(recordingSession.elapsedMs()));
    const t = setInterval(() => setClock(formatClock(recordingSession.elapsedMs())), 500);
    return () => clearInterval(t);
  }, [shown, capturing]);

  if (!shown) return null;

  // Three truthful states: live (pulsing red + clock), saving (still red, the final segment may be
  // flushing), lost (amber, the capture stopped - tap for the details and the saved audio).
  const label = lost
    ? "Recording stopped - tap for details"
    : session.phase === "paused"
      ? `Recording paused ${clock}`
      : session.phase === "stopping"
        ? "Saving recording..."
        : `Recording ${clock}`;

  return (
    <button
      type="button"
      className="recbanner"
      ref={barRef}
      onClick={() => void navigate("/recorder")}
      aria-label={`${label}. Open the recorder.`}
    >
      <span
        className={`recbanner-dot${session.phase === "paused" || lost ? " recbanner-dot-paused" : ""}`}
        aria-hidden="true"
      />
      <span className="recbanner-text">{label}</span>
      <span className="recbanner-open">Open</span>
    </button>
  );
}
