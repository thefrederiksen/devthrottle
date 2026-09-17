import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { setHostSuppliedKey } from "@devthrottle/client-core/auth/hostKey";
import { DevReportConversation } from "@devthrottle/client-core/devreports/DevReportConversation";
import { DevReportList } from "@devthrottle/client-core/devreports/DevReportList";
import { DevReportViewer } from "@devthrottle/client-core/devreports/DevReportViewer";
import { HOST_KEY_WAIT_MS, HOST_READY_MESSAGE, hostBridge, isHostKeyMessage, type HostReadyMessage } from "./hostBridge";
import "./embed.css";

// ONE session's dev reports, with no Cockpit chrome, for a host application to embed
// (dev reports mission, phase 4, issue #3019). The Director loads this route in WebView2 and shows it
// in a pane; besides sign-in it is the only route in this app outside both the device-key gate and the
// app shell.
//
// It is a FRAME, not a second implementation. The list, the report frame with all of its trust rules,
// the conversation and every Gateway call are the same client-core pieces the Reports tab and the phone
// mount. This file decides two things and nothing else: how the page gets a credential, and where the
// conversation sits.
//
// THE CREDENTIAL, AND WHY THERE IS NO OTHER WAY IN. The browser profile behind a WebView2 pane has
// never enrolled and never will: this page does not sign in, does not enroll, and never carries a key in
// its own address. Its only credential is the one the host hands it over the WebView2 message bridge,
// held in memory for the life of the pane (client-core auth/hostKey.ts). So when no host answers there
// is nothing to fall back to, and the page says so - it does NOT quietly read the browser's own device
// key, which in an ordinary browser would silently turn an embed address into a second Reports page
// running as whoever happens to be signed in there.

type Phase = "waiting" | "ready" | "refused";

export function EmbedReportsView() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const [phase, setPhase] = useState<Phase>("waiting");
  const [openReportId, setOpenReportId] = useState<string | null>(null);

  // A different session never shows the previous session's open report.
  useEffect(() => setOpenReportId(null), [sessionId]);

  useEffect(() => {
    if (!sessionId) return;
    let accepted = false;

    const onHostMessage = (event: MessageEvent): void => {
      // One key per load. Once a key has been accepted nothing can replace it - a report that navigates
      // its frame to a page whose scripts DO run (the case CONTRACT.md section 4 rule 6 covers) must not
      // be able to post a key of its own and have the pane read somebody else's reports through it.
      if (accepted) return;
      // The host speaks either through its own bridge, where there is no source window, or as this
      // document. Anything posted by a frame INSIDE this page is not the host, and is refused.
      if (event.source !== null && event.source !== window) return;
      if (!isHostKeyMessage(event.data)) return;
      // A key minted for another session is refused outright rather than used on this one.
      if (event.data.sessionId !== sessionId) return;
      accepted = true;
      setHostSuppliedKey(event.data.key);
      setPhase("ready");
    };

    const bridge = hostBridge(window);
    window.addEventListener("message", onHostMessage);
    bridge?.addEventListener("message", onHostMessage);

    // Say we are up and waiting. The host cannot know when the page has mounted, so the page asks
    // first; a host that is not there simply never answers, which is the case the wait below covers.
    const ready: HostReadyMessage = { kind: HOST_READY_MESSAGE, sessionId };
    bridge?.postMessage(ready);

    const timer = window.setTimeout(
      () => setPhase((current) => (current === "ready" ? current : "refused")),
      HOST_KEY_WAIT_MS,
    );

    return () => {
      window.clearTimeout(timer);
      window.removeEventListener("message", onHostMessage);
      bridge?.removeEventListener("message", onHostMessage);
      // The pane is gone, so the host's credential goes with it.
      setHostSuppliedKey(null);
    };
  }, [sessionId]);

  if (!sessionId) {
    return (
      <div className="embed-reports embed-reports-message" data-testid="embed-reports-no-session">
        <p>This page needs a session in its address, as /embed/reports/ followed by the session identifier.</p>
      </div>
    );
  }

  if (phase === "refused") {
    return (
      <div className="embed-reports embed-reports-message" data-testid="embed-reports-no-host-key">
        <h1 className="embed-reports-message-title">No Gateway key was supplied</h1>
        <p>
          This page is only for an application that embeds it. It has no sign-in of its own: the application
          hosting it hands it a Gateway key, and this one did not do so within ten seconds.
        </p>
        <p>
          Nothing is wrong with your account. To read these reports yourself, open the session in DevThrottle
          and choose Reports.
        </p>
      </div>
    );
  }

  // Shown from the first paint, before anything at all is fetched: the page has no key yet, so there is
  // nothing it could ask the Gateway for.
  if (phase === "waiting") {
    return (
      <div className="embed-reports embed-reports-message" data-testid="embed-reports-waiting">
        <p>Loading...</p>
      </div>
    );
  }

  if (openReportId === null) {
    return (
      <div className="embed-reports" data-testid="embed-reports">
        <DevReportList sessionId={sessionId} onOpen={(report) => setOpenReportId(report.id)} />
      </div>
    );
  }

  return (
    <div className="embed-reports embed-reports-open" data-testid="embed-reports">
      <div className="embed-reports-bar">
        <button
          type="button"
          className="embed-reports-back"
          data-testid="embed-reports-back"
          onClick={() => setOpenReportId(null)}
        >
          All reports
        </button>
      </div>
      <div className="embed-reports-body">
        <DevReportViewer
          key={openReportId}
          reportId={openReportId}
          onNotFound={() => setOpenReportId(null)}
          renderConversation={(conversation) => (
            <aside className="embed-reports-conversation">
              <DevReportConversation conversation={conversation} />
            </aside>
          )}
        />
      </div>
    </div>
  );
}
