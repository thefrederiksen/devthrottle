// The Phone page (devthrottle_internal #1508): how you get DevThrottle onto your phone.
//
// Nothing in the Cockpit used to point at the mobile app. Every other destination was in the rail, but
// the app a person actually wants on their phone was reachable only by already knowing the address and
// typing it in - so in practice it was not reachable at all.
//
// This is a PAGE rather than a link, because the job is getting the app onto a DIFFERENT device. A link
// would open the narrow layout in this desktop browser, which is the one thing nobody wanted. So it
// offers the three ways across the gap - scan it, copy it, or open it here - and says how to install it
// once it is there.
import { useEffect, useState } from "react";
import { PageHeader } from "../components";
import { getMobileQrPng, mobileAppUrl } from "@devthrottle/client-core/account/mobileEntry";
import "./phone.css";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-phone";

export function PhoneView() {
  const url = mobileAppUrl();
  const [qr, setQr] = useState<string | null>(null);
  // The Gateway's OWN sentence for why there is no code, rendered verbatim. The one case that actually
  // happens is a Cockpit opened on localhost, where a code would encode an address no phone can reach;
  // the Gateway refuses rather than rendering one, and says which address it saw.
  const [qrProblem, setQrProblem] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    let objectUrl: string | null = null;

    getMobileQrPng(controller.signal)
      .then((blob) => {
        if (controller.signal.aborted) return;
        objectUrl = URL.createObjectURL(blob);
        setQr(objectUrl);
        setQrProblem(null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) setQrProblem(describeAndReport(SURFACE, "load the phone sign-in code", err));
      });

    return () => {
      controller.abort();
      if (objectUrl !== null) URL.revokeObjectURL(objectUrl);
    };
  }, []);

  async function copy() {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard access denied (an insecure origin, or the permission refused). The address is on
      // screen and selectable either way, so say nothing was copied rather than claiming it was.
      setCopied(false);
    }
  }

  return (
    <div className="page phone-page">
      <PageHeader
        title="Phone"
        subtitle="Open DevThrottle on your phone - the same fleet, sized for one hand."
      />

      <section className="phone-card" aria-label="Scan to open on your phone">
        <h2>Scan this with your phone camera</h2>
        {qr !== null && <img className="phone-qr" src={qr} alt={`Scannable code for ${url}`} />}
        {qr === null && qrProblem === null && <div className="phone-qr-pending">Loading...</div>}
        {qrProblem !== null && (
          <p className="phone-qr-problem" role="status">
            {qrProblem}
          </p>
        )}
        <p className="phone-note">
          Your phone opens the address below. It has to be on a network that can reach this Gateway.
        </p>
      </section>

      <section className="phone-card" aria-label="The address">
        <h2>Or type it in</h2>
        <div className="phone-url-row">
          <code className="phone-url">{url}</code>
          <button type="button" className="phone-btn" onClick={() => void copy()}>
            {copied ? "Copied" : "Copy link"}
          </button>
        </div>
        <a className="phone-btn phone-btn-wide" href={url}>
          Open the mobile view in this browser
        </a>
      </section>

      <section className="phone-card" aria-label="Installing it">
        <h2>Install it as an app</h2>
        <p className="phone-note">
          Once it is open on the phone, add it to the home screen - "Add to Home Screen" on iPhone,
          "Install app" on Android. It then opens full screen with no browser bars, keeps you signed in,
          and can record and send voice from the lock screen.
        </p>
        <p className="phone-note">
          You sign the phone in once, on devthrottle.com. It can hold more than one account, and you
          switch between them from the phone's own Account screen.
        </p>
      </section>
    </div>
  );
}
