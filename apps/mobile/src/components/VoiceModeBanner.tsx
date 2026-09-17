import { useEffect, useRef } from "react";
import { useVoiceModeAll } from "@devthrottle/client-core/voice/useVoiceModeAll";

// THE VOICE MODE BANNER (owner, 2026-07-24). While the fleet is in voice mode, this bar is on EVERY screen
// of the app and carries the switch that turns it off.
//
// It does two jobs, and the second is the important one.
//
// It says which state you are in. Voice mode used to be a state nothing displayed and nothing held - you
// worked it out by looking at the roster, or you did not know.
//
// And it puts the way out wherever you are. The only off switch used to be a checkbox on one tab of the
// roster - which, with auto-speak running, is the screen you are on for three seconds between sessions.
// Everywhere else, there was no way to stop. Making that window longer only made it a bigger window to
// catch. A control that is present on the session screen auto-speak just dropped you into is not a window
// at all, and that is the actual fix for "I try to stop it and it just keeps going".
//
// Deliberately NOT a confirmation dialog: this is the escape hatch, and an escape hatch that asks "are you
// sure?" while a voice is talking over you is not one. Turning voice mode back on is one tap on the roster.
//
// IT PUBLISHES ITS OWN HEIGHT AS --voicemode-h, and that is not a detail (owner, 2026-07-25). The session
// screens are PINNED OUT OF THE DOCUMENT FLOW - fixed to the top of the window and
// sized to the whole visible height - so nothing rendered above them in the markup can push them down.
// Shipped as a plain sticky bar, this banner simply PAINTED OVER the session screen's own header, taking
// the back arrow to the roster and the overflow menu with it: you could Respond and Snooze, and you could
// not leave. Those screens now start below this bar and lose exactly its height, which is why the height
// has to be a real measured number and not a guess - the message wraps to two lines on a narrow phone.
//
// Zero when voice mode is off, so nothing about any screen changes when the banner is not there.
export function VoiceModeBanner() {
  const voice = useVoiceModeAll();
  const barRef = useRef<HTMLDivElement | null>(null);
  const shown = voice.enabled === true;

  useEffect(() => {
    const root = document.documentElement;
    // Not rendered -> no strip to give up. Set it explicitly rather than leaving the last value behind:
    // a stale height would push every screen down by a bar that is no longer on the page.
    if (!shown) {
      root.style.setProperty("--voicemode-h", "0px");
      return;
    }
    const el = barRef.current;
    if (el === null) return;
    const apply = () => root.style.setProperty("--voicemode-h", `${Math.round(el.getBoundingClientRect().height)}px`);
    apply();
    // Measured, not assumed: the sub-line wraps to two lines on a narrow phone and the bar grows, and it
    // grows again when an error line appears under it. A hard-coded height would be right on one device.
    const observer = new ResizeObserver(apply);
    observer.observe(el);
    return () => {
      observer.disconnect();
      root.style.setProperty("--voicemode-h", "0px");
    };
  }, [shown]);

  // Null means the first read has not landed. Render nothing rather than flash a banner that may be wrong -
  // a banner that appears and vanishes on every app open teaches you to ignore it.
  if (!shown) return null;

  return (
    <div className="voicemode-banner" role="status" ref={barRef}>
      <span className="voicemode-dot" aria-hidden="true" />
      <span className="voicemode-text">
        Voice mode is on
        <span className="voicemode-sub">Every session on the Gateway narrates its turns</span>
        {voice.error !== null && <span className="voicemode-error">{voice.error}</span>}
      </span>
      <button
        type="button"
        className="voicemode-off"
        onClick={() => void voice.set(false)}
        disabled={voice.busy}
      >
        {voice.busy ? "Turning off..." : "Turn off"}
      </button>
    </div>
  );
}
