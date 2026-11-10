// "WATCH YOUR FACTORY" FOR A CONTRIBUTOR (version one, piece 6): the explainer card before the person's first watch, then
// the shared player - one component, mounted by the Cockpit and by the phone. Each shell supplies only its frame (the
// back link, the app bar); everything inside is here.
//
// Critical rule 7: the view is the Gateway's, and so is every verdict in it. Whether the card is shown is the view's
// `explainer` being set; its words are that object's. The Gateway remembers that the person has seen it, for them, on
// every team and surface - this component only tells it when the button was pressed.
//
// The view is read again every few seconds while the page is visible, so new steps play as they arrive (the player's
// live mode). A refused factory shows the Gateway's own sentence, whatever the reason.

import { useCallback, useState } from "react";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import { FactoryWatchPlayer } from "../factory/watch/FactoryWatchPlayer";
import { WatchExplainerCard } from "../factory/watch/WatchExplainerCard";
import type { FactoryWatchView } from "../factory/watch/factoryWatchTypes";
import { getContributorWatch, markWatchExplainerSeen, type CustomerPageRefused } from "./contributorClient";

/** How often the watch view is read again while it is on screen, so the floor is live. */
export const CONTRIBUTOR_WATCH_POLL_MS = 15_000;

const SURFACE = "contributor-watch";
const LOAD = "load the watch view of this factory";
const REMEMBER = "remember that you have read the card";

export interface ContributorWatchViewProps {
  teamId: string;
  factory: string;
  /** The shell's own class for an error banner. */
  errorClassName: string;
  /** Called with the factory's name once the view has it, so the shell can put it in its frame. */
  onTitle?: (title: string) => void;
  /** True when the shell's frame shows that name itself (the phone's app bar), so the card and the player leave their own
   *  heading out and the name is shown once. The Cockpit's breadcrumb is a way back, not a heading, so it keeps them. */
  titleInFrame?: boolean;
}

export function ContributorWatchView({ teamId, factory, errorClassName, onTitle, titleInFrame = false }: ContributorWatchViewProps) {
  const [view, setView] = useState<FactoryWatchView | CustomerPageRefused | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Pressed here: the card closes at once, whatever the next read says while the Gateway records it.
  const [pressed, setPressed] = useState(false);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const next = await getContributorWatch(teamId, factory, signal);
        setView(next);
        if (!("refused" in next)) onTitle?.(next.title);
        setError(null);
        backgroundRecovered(SURFACE, LOAD);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(SURFACE, LOAD, err, { background: true }));
      }
    },
    [teamId, factory, onTitle],
  );
  useVisiblePolling(refresh, CONTRIBUTOR_WATCH_POLL_MS);

  const watch = () => {
    setPressed(true);
    markWatchExplainerSeen(teamId, factory).catch((err: unknown) => {
      // The person is watching either way; if this did not reach the Gateway, the card comes back next time, and the
      // reason is shown and reported.
      setError(describeAndReport(SURFACE, REMEMBER, err));
    });
  };

  if (view !== null && "refused" in view) {
    return (
      <p className="contributor-watch-refused" data-testid="contributor-watch-refused">
        {view.refused}
      </p>
    );
  }

  return (
    <div className="contributor-watch" data-testid="contributor-watch">
      {error !== null && (
        <div className={errorClassName} role="alert">
          {error}
        </div>
      )}
      {view === null && error === null && <p data-testid="contributor-watch-loading">Loading...</p>}
      {view !== null &&
        (view.explainer !== null && !pressed ? (
          <WatchExplainerCard explainer={view.explainer} title={view.title} showTitle={!titleInFrame} onWatch={watch} />
        ) : (
          <FactoryWatchPlayer view={view} startPlaying={pressed} showTitle={!titleInFrame} />
        ))}
    </div>
  );
}
