import { useCallback, useEffect, useState } from "react";
import { getBoxes, getDesktopTicket, type BoxRow, type BoxesList } from "@devthrottle/client-core/boxes/boxesClient";
import { useBoxesSwitch } from "@devthrottle/client-core/boxes/useBoxesSwitch";
import { describeAndReport, reportShownError } from "@devthrottle/client-core/errors/reportClientError";
import { Button, EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import { NotFound } from "../panes/NotFound";

// THE BOXES SCREEN (Factory Boxes mission, phase 1): the desktops the account's Directors host, one row each, and an
// Open button that opens one in a new tab.
//
// THE GATEWAY RULES EVERY VERDICT (rule 7). Whether the screen exists at all is the Gateway's `boxes` on
// GET /account/status (useBoxesSwitch): off, this address is the ordinary "Page not found", exactly as an unknown
// address looks - never a teaser. Every field of a row, the state above all, is drawn exactly as GET /boxes sent it.
//
// OPENING A DESKTOP. The desktop is served on a separate desktop host and is opened with a one-time ticket, so:
//   - the new tab is opened INSIDE the click, before anything is awaited, because a browser blocks a tab opened later;
//   - the tab shows "Opening the desktop..." while POST /boxes/{boxId}/desktop-ticket is asked;
//   - the tab is sent to the `url` the Gateway answered, verbatim. The Cockpit never builds a desktop address;
//   - the tab's link back to the Cockpit (window.opener) is cut before it leaves, so the desktop's page cannot steer
//     the Cockpit's tab;
//   - a failed ticket closes the tab and shows the Gateway's sentence on the row.
// The desktop is never put in a frame here: it is on another site, and its box cookie (SameSite=Strict) is not sent
// inside a frame on a different site.

const SURFACE = "boxes";
export const OPENING_TEXT = "Opening the desktop...";

type ListRead = { kind: "loading" } | { kind: "loaded"; list: BoxesList } | { kind: "failed"; message: string };

export function BoxesView() {
  const boxes = useBoxesSwitch();
  if (boxes.state === "off") return <NotFound />;
  if (boxes.state === "asking") {
    return (
      <section className="pane boxes-page">
        <LoadingState />
      </section>
    );
  }
  if (boxes.state === "failed") {
    return (
      <section className="pane boxes-page">
        <PageHeader title="Boxes" />
        {/* error-report-exempt: useBoxesSwitch reports every failed read itself, once per attempt. */}
        <ErrorBanner message={boxes.message} />
      </section>
    );
  }
  return <BoxesPage />;
}

function BoxesPage() {
  const [read, setRead] = useState<ListRead>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setRead({ kind: "loading" });
    getBoxes(controller.signal).then(
      (list) => {
        if (!controller.signal.aborted) setRead({ kind: "loaded", list });
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setRead({ kind: "failed", message: describeAndReport(SURFACE, "read the boxes", err) });
      },
    );
    return () => controller.abort();
  }, [attempt]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  return (
    <section className="pane boxes-page">
      <PageHeader
        title="Boxes"
        subtitle="The desktops your Directors host. Open one to use it in a new tab."
        actions={
          read.kind === "loaded" ? (
            <Button onClick={reload} data-testid="boxes-refresh">
              Refresh
            </Button>
          ) : undefined
        }
      />
      {read.kind === "loading" && <LoadingState />}
      {read.kind === "failed" && <ErrorBanner message={read.message} onRetry={reload} />}
      {read.kind === "loaded" &&
        (read.list.boxes.length === 0 ? (
          <EmptyState
            title="No desktops"
            message={`No Director in your account is hosting a desktop. The Gateway lists ${read.list.count} boxes.`}
          />
        ) : (
          <ul className="boxes-list" data-testid="boxes-list">
            {read.list.boxes.map((box) => (
              <BoxRowView key={box.boxId} box={box} />
            ))}
          </ul>
        ))}
    </section>
  );
}

function BoxRowView({ box }: { box: BoxRow }) {
  const [opening, setOpening] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // No await before window.open: the click's permission to open a tab lasts only while this handler runs.
  const open = () => {
    setError(null);
    const tab = window.open("", "_blank");
    if (tab === null) {
      setError(
        reportShownError(
          SURFACE,
          "open the desktop",
          "The browser did not open a new tab, so the desktop could not be opened. Allow pop-ups for this site and press Open again.",
        ),
      );
      return;
    }
    showOpening(tab);
    setOpening(true);
    getDesktopTicket(box.boxId).then(
      (ticket) => {
        tab.opener = null;
        tab.location.href = ticket.url;
        setOpening(false);
      },
      (err: unknown) => {
        tab.close();
        setOpening(false);
        setError(describeAndReport(SURFACE, "open the desktop", err));
      },
    );
  };

  return (
    <li className="boxes-row" data-testid="box-row" data-box-id={box.boxId}>
      <div className="boxes-row-main">
        <div className="boxes-row-name" data-testid="box-name">
          {box.displayName}
        </div>
        <div className="boxes-row-id" data-testid="box-id">
          {box.boxId}
        </div>
        <dl className="boxes-row-facts">
          <div>
            <dt>Machine</dt>
            <dd data-testid="box-machine">{box.machineName}</dd>
          </div>
          {box.directorName.length > 0 && (
            <div>
              <dt>Director</dt>
              <dd data-testid="box-director">{box.directorName}</dd>
            </div>
          )}
          <div>
            <dt>State</dt>
            <dd data-testid="box-state">{box.state}</dd>
          </div>
        </dl>
        {error !== null && (
          <div className="ui-error-banner boxes-row-error" role="alert" data-testid="box-error">
            {error}
          </div>
        )}
      </div>
      <div className="boxes-row-actions">
        <Button variant="primary" onClick={open} disabled={opening} data-testid="box-open">
          {opening ? "Opening..." : "Open"}
        </Button>
      </div>
    </li>
  );
}

// The new tab is about:blank on the Cockpit's own origin until it is sent on, so it can be written to. It says what is
// happening rather than sitting blank while the ticket is asked for.
function showOpening(tab: Window) {
  tab.document.title = OPENING_TEXT;
  tab.document.body.style.cssText = "background:#0b1020;color:#e6e9f2;font:16px system-ui,sans-serif;padding:32px";
  tab.document.body.textContent = OPENING_TEXT;
}
