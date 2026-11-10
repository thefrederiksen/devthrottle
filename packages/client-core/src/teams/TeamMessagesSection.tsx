import { useCallback, useEffect, useState } from "react";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import { formatInboxTime } from "../factoryinbox/formatInboxTime";
import { teamsWithPage } from "./contributorClient";
import { forwardRequest, getTeamRequestList, type TeamRequest, type TeamRequestList } from "./requestsClient";
import { getMyTeams, type TeamSummary } from "./teamsClient";
import "../factoryinbox/factoryInbox.css";
import "./teamMessages.css";

// THE TEAM OWNER'S MESSAGES FROM CONTRIBUTORS (Team implementation piece 4.3; mockup.html, the Owner's Inbox: "messages a
// contributor writes to you land in Your inbox, labelled with their team"). Shown above Your inbox on both shells - the
// Cockpit and the phone mount this one component (rule 8).
//
// WHICH TEAMS is the Gateway's verdict: a team whose page list (GET /teams) names the "messages" entry - the teams this
// person owns. Every word, label and state on a card, which cards may be forwarded and to which factories, is the
// Gateway's (rule 7); this view lays it out and sends the Owner's choice back.
//
// THE ONE WAY OUT. Nothing here is read by an agent. "Forward to the factory" is the only path from a contributor's words
// to a factory, and it runs when the Owner presses it: the Gateway quotes the words, marks them as the contributor's, and
// leaves them in the factory's inbox in the Owner's name.

/** The page entry the Gateway names on a team whose contributors' messages this Inbox shows. */
export const MESSAGES_PAGE_ID = "messages";

/** Where a failure here is filed. One component on both shells, so one surface. */
export const TEAM_MESSAGES_SURFACE = "team-messages";

const REFRESH_MS = 30_000;

type Loaded = { kind: "loading" } | { kind: "none" } | { kind: "teams"; heading: string; lists: TeamRequestList[] };

export function TeamMessagesSection() {
  const [state, setState] = useState<Loaded>({ kind: "loading" });
  const [error, setError] = useState<string | null>(null);
  const [reads, setReads] = useState(0);
  const reload = useCallback(() => setReads((n) => n + 1), []);

  useEffect(() => {
    const controller = new AbortController();
    const background = reads > 0;
    (async () => {
      const answer = await getMyTeams(controller.signal);
      // A Gateway with Teams switched off, or a person who owns no team: there is nothing to show, and that is an answer.
      if (answer.kind === "not-offered") return { kind: "none" } as const;
      const owned: TeamSummary[] = teamsWithPage(answer.teams, MESSAGES_PAGE_ID);
      if (owned.length === 0) return { kind: "none" } as const;
      const heading = owned[0].app.pages.find((p) => p.id === MESSAGES_PAGE_ID)!.label;
      const lists = await Promise.all(owned.map((t) => getTeamRequestList(t.id, controller.signal)));
      return { kind: "teams", heading, lists } as const;
    })().then(
      (next) => {
        if (controller.signal.aborted) return;
        setState(next);
        setError(null);
        backgroundRecovered(TEAM_MESSAGES_SURFACE, "load the messages from your teams");
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setError(describeAndReport(TEAM_MESSAGES_SURFACE, "load the messages from your teams", err, background ? { background: true } : undefined));
      },
    );
    const timer = setTimeout(reload, REFRESH_MS);
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [reads, reload]);

  if (state.kind === "none" && error === null) return null;
  const messages = state.kind === "teams" ? state.lists.flatMap((l) => l.requests.map((r) => ({ list: l, request: r }))) : [];

  return (
    <section className="tm-section" data-testid="team-messages" aria-label={state.kind === "teams" ? state.heading : undefined}>
      {state.kind === "teams" && <h2 className="tm-heading">{state.heading}</h2>}
      {error !== null && (
        <div className="fi-composer-error" role="alert">
          {error}
        </div>
      )}
      {state.kind === "loading" && error === null && <p role="status">Loading...</p>}
      {messages.length > 0 && (
        <ul className="tm-list">
          {messages.map(({ list, request }) => (
            <li key={`${list.teamId}/${request.id}`}>
              <TeamMessageCard list={list} request={request} onChanged={reload} />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** One message: who wrote it on which team, their words, what has happened to it, and - while the Gateway allows it -
 *  "Forward to the factory" with the factory to pick and an optional note above the quote. */
export function TeamMessageCard({ list, request, onChanged }: { list: TeamRequestList; request: TeamRequest; onChanged: () => void }) {
  const control = list.forward;
  const [open, setOpen] = useState(false);
  const [factory, setFactory] = useState(control.factories[0]?.factory ?? "");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [shown, setShown] = useState(request);
  useEffect(() => setShown(request), [request]);

  const send = async () => {
    setBusy(true);
    setRefusal(null);
    try {
      setShown(await forwardRequest(list.teamId, shown.id, factory, note.trim()));
      setOpen(false);
      setNote("");
      onChanged();
    } catch (err) {
      setRefusal(describeAndReport(TEAM_MESSAGES_SURFACE, "forward the message to the factory", err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <article className="tm-card" data-testid="team-message" aria-label={shown.text}>
      <div className="tm-card-head">
        <span className="tm-team" data-testid="team-message-team">
          {list.teamLabel}
        </span>
        <span className="tm-from">{shown.fromLabel ?? shown.sentBy}</span>
        <span className="tm-time">{formatInboxTime(shown.sentAtUtc)}</span>
        <span className="fi-badge" data-testid="team-message-state">
          {shown.stateLabel}
        </span>
      </div>
      <blockquote className="tm-words">{shown.text}</blockquote>
      {shown.privacyNote != null && <p className="tm-note">{shown.privacyNote}</p>}
      <ol className="tm-trail" aria-label="What happened">
        {shown.trail.map((s, i) => (
          <li key={`${s.state}/${s.atUtc}/${i}`}>
            {s.sentence}
            {s.forwardedTo != null && <span className="tm-where"> - {s.forwardedTo}</span>}
            {s.reason !== null && <span className="tm-where"> - {s.reason}</span>}
            <span className="tm-time"> {formatInboxTime(s.atUtc)}</span>
          </li>
        ))}
      </ol>
      {shown.canForward === true && control.factories.length === 0 && control.emptyText !== null && (
        <p className="tm-note" data-testid="team-message-cannot-forward">
          {control.emptyText}
        </p>
      )}
      {shown.canForward === true && control.factories.length > 0 && !open && (
        <div className="fi-composer-buttons">
          <button type="button" className="fi-send tm-forward" data-testid="team-message-forward" onClick={() => setOpen(true)}>
            {control.label}
          </button>
        </div>
      )}
      {shown.canForward === true && open && (
        <div className="fi-composer" data-testid="team-message-forward-form">
          <label className="fi-composer-pick">
            {control.label}
            <select value={factory} onChange={(e) => setFactory(e.target.value)} disabled={busy} data-testid="team-message-factory">
              {control.factories.map((f) => (
                <option key={f.factory} value={f.factory}>
                  {f.name}
                </option>
              ))}
            </select>
          </label>
          <textarea
            className="fi-composer-text"
            aria-label={control.noteLabel}
            placeholder={control.noteLabel}
            rows={2}
            value={note}
            disabled={busy}
            onChange={(e) => setNote(e.target.value)}
            data-testid="team-message-note"
          />
          <p className="tm-note">{control.hint}</p>
          {refusal !== null && (
            <div className="fi-composer-error" role="alert">
              {refusal}
            </div>
          )}
          <div className="fi-composer-buttons">
            <button type="button" disabled={busy} onClick={() => { setOpen(false); setRefusal(null); }}>
              Cancel
            </button>
            <button type="button" className="fi-send" disabled={busy || factory === ""} onClick={() => void send()} data-testid="team-message-forward-send">
              {busy ? "Forwarding..." : control.label}
            </button>
          </div>
        </div>
      )}
    </article>
  );
}
