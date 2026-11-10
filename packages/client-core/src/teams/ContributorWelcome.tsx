import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { describeAndReport } from "../errors/reportClientError";
import { enablePush } from "../push/register";
import {
  getContributorWelcome,
  markContributorWelcomeSeen,
  type ContributorWelcomePage,
} from "./contributorWelcomeClient";
import "./contributorWelcome.css";

/** Where a welcome page failure is filed. One page on both shells, so one surface. */
const SURFACE = "contributor-welcome";

// THE CONTRIBUTOR'S WELCOME (Team implementation piece 4.2): the page a contributor lands on the first time they sign in
// after joining a team - the heading, the one-line explanation, the five numbered steps, the notifications line, the
// question and "Send a request", as the owner approved them (welcome-mockup.html). One implementation: the Cockpit mounts
// it, and the phone reaches the same Cockpit page at the same address.
//
// IT RENDERS THE GATEWAY'S WORDS AND DECIDES NOTHING (rule 7). Every word is the Gateway's, and so is the button's
// address. Whether the page is shown at all, and where the app opens once it has been seen, is the Gateway's page verdict.
//
// SEEN ONCE IT IS ON SCREEN. As soon as the page has drawn, it tells the Gateway the person has seen it; from their next
// sign-in the app opens on Factories. Pressing "Send a request" needs nothing more.
//
// NOTIFICATIONS ARE ASKED FOR ONLY ON A PRESS. The notifications line is a button: pressing it asks this browser for
// permission (the product's one push path, enablePush) and shows the sentence that comes back. Nothing asks on load.
export function ContributorWelcomeView({ teamId }: { teamId: string }) {
  const [page, setPage] = useState<ContributorWelcomePage | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [seenError, setSeenError] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);
  const [pushAnswer, setPushAnswer] = useState<string | null>(null);
  const marked = useRef<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setPage(null);
    setLoadError(null);
    getContributorWelcome(teamId, controller.signal)
      .then(setPage)
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setLoadError(describeAndReport(SURFACE, "load the welcome", err));
      });
    return () => controller.abort();
  }, [teamId]);

  // Once per team on screen, the first time the page has drawn.
  useEffect(() => {
    if (page === null || marked.current === page.teamId) return;
    marked.current = page.teamId;
    markContributorWelcomeSeen(page.teamId).catch((err: unknown) => {
      setSeenError(describeAndReport(SURFACE, "remember that you saw the welcome", err));
    });
  }, [page]);

  const askForNotifications = async () => {
    if (asking) return;
    setAsking(true);
    // enablePush never throws: every outcome comes back with the sentence to show.
    const result = await enablePush();
    setPushAnswer(result.message);
    setAsking(false);
  };

  if (loadError !== null) {
    return (
      <section className="pane contributor-welcome" data-testid="contributor-welcome">
        <p className="contributor-welcome-error" role="alert">{loadError}</p>
      </section>
    );
  }
  if (page === null) {
    return (
      <section className="pane contributor-welcome" data-testid="contributor-welcome">
        <p className="contributor-welcome-loading">Loading...</p>
      </section>
    );
  }

  return (
    <section className="pane contributor-welcome" data-testid="contributor-welcome">
      <div className="contributor-welcome-body">
        <span className="contributor-welcome-pill" data-testid="contributor-welcome-team">{page.teamLabel}</span>
        <h1 className="contributor-welcome-title">{page.title}</h1>
        <p className="contributor-welcome-lead">{page.lead}</p>

        <ol className="contributor-welcome-steps" data-testid="contributor-welcome-steps">
          {page.steps.map((s) => (
            <li
              key={s.number}
              className={s.yours ? "contributor-welcome-step contributor-welcome-step-yours" : "contributor-welcome-step"}
              data-testid="contributor-welcome-step"
            >
              <div className="contributor-welcome-num" aria-hidden="true">{s.number}</div>
              <div>
                <h3 className="contributor-welcome-step-title">{s.title}</h3>
                <p className="contributor-welcome-step-text">{s.text}</p>
                {s.watch && (
                  <div className="contributor-welcome-watch" data-testid="contributor-welcome-watch">
                    <WatchDrawing />
                    <span>{page.watchLabel}</span>
                  </div>
                )}
              </div>
            </li>
          ))}
        </ol>

        <button
          type="button"
          className="contributor-welcome-notify"
          data-testid="contributor-welcome-notify"
          disabled={asking}
          onClick={() => void askForNotifications()}
        >
          {page.notifications}
        </button>
        {pushAnswer !== null && (
          <p className="contributor-welcome-notify-answer" data-testid="contributor-welcome-notify-answer" role="status">
            {pushAnswer}
          </p>
        )}

        <p className="contributor-welcome-ask">{page.question}</p>
        <div className="contributor-welcome-actions">
          <Link className="contributor-welcome-button" to={page.button.href} data-testid="contributor-welcome-request">
            {page.button.label}
          </Link>
        </div>
        {seenError !== null && (
          <p className="contributor-welcome-error" role="alert">{seenError}</p>
        )}
      </div>
    </section>
  );
}

/** The small drawing beside step four (welcome-mockup.html): two workers and a crate on a floor line. Decoration only;
 *  the words beside it are the Gateway's. */
function WatchDrawing() {
  return (
    <svg width="96" height="34" viewBox="0 0 96 34" aria-hidden="true" focusable="false">
      <rect x="0" y="28" width="96" height="3" rx="1.5" className="contributor-welcome-floor" />
      <g>
        <ellipse cx="16" cy="18" rx="10" ry="11" fill="#7c5cff" />
        <circle cx="12" cy="15" r="2.4" fill="#fff" />
        <circle cx="20" cy="15" r="2.4" fill="#fff" />
        <circle cx="12" cy="15.5" r="1.1" fill="#1b1d26" />
        <circle cx="20" cy="15.5" r="1.1" fill="#1b1d26" />
      </g>
      <g>
        <ellipse cx="48" cy="19" rx="10" ry="10" fill="#39d98a" />
        <circle cx="48" cy="15" r="3" fill="#fff" />
        <circle cx="48" cy="15.5" r="1.3" fill="#1b1d26" />
      </g>
      <rect x="66" y="16" width="16" height="12" rx="2" fill="#ff9a3c" />
      <path d="M74 18.5l1.2 2.4 2.6.3-1.9 1.8.5 2.6-2.4-1.3-2.4 1.3.5-2.6-1.9-1.8 2.6-.3z" fill="#1b1d26" />
    </svg>
  );
}
