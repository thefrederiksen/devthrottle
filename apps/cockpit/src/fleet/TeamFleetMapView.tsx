import { useEffect, useState } from "react";
import { GatewayError, gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  getTeamFleetMap,
  type TeamFleetMap,
  type TeamFleetMapDirector,
  type TeamFleetMapLayout,
  type TeamFleetMapSession,
  type TeamSessionStatus,
} from "@devthrottle/client-core/teams/teamFleetMapClient";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import "./teamFleetMap.css";

// THE FLEET MAP FOR ONE TEAM (devthrottle_internal#2312, screens D4 and D5). Shown in place of the person's own fleet
// when a team is picked in the switcher.
//
// WHAT IS ON IT IS THE GATEWAY'S (rule 7). The Owner and a Manager are sent every Director on the team, grouped by
// person; a Developer is sent only their own Directors; a Collaborator is refused. This page never filters by role -
// it lays out what arrives, in the layouts the Gateway offers, with the Gateway's own sentences.
//
// NOTHING OPENS. Every session here is a name and a status word, and nothing more: no link, no button, no click
// handler. The answer carries no session id, so there is nothing to open a session WITH. Owner decision, 3 Oct 2026:
// "after the fact to start with" - no screen, no transcript, no typing.

/** How often the map is re-read while it is on screen. */
export const TEAM_MAP_POLL_MS = 10_000;

const LAYOUT_LABELS: Record<TeamFleetMapLayout, string> = {
  "by-person": "By person",
  "by-director": "By director",
  "by-repository": "By repository",
  "by-mission": "By mission",
};

const STATUS_CLASS: Record<TeamSessionStatus, string> = {
  working: "tmap-dot-working",
  waiting: "tmap-dot-waiting",
  done: "tmap-dot-done",
};

type Load =
  | { kind: "loading" }
  | { kind: "map"; map: TeamFleetMap; error: string | null }
  | { kind: "refused"; reason: string }
  | { kind: "failed"; error: string };

export function TeamFleetMapView({ team }: { team: TeamSummary }) {
  const [load, setLoad] = useState<Load>({ kind: "loading" });
  const [layout, setLayout] = useState<TeamFleetMapLayout | null>(null);

  useEffect(() => {
    setLoad({ kind: "loading" });
    setLayout(null);
    let live = true;
    let controller: AbortController | null = null;
    const read = () => {
      controller?.abort();
      controller = new AbortController();
      getTeamFleetMap(team.id, controller.signal).then(
        (map) => {
          if (live) setLoad({ kind: "map", map, error: null });
        },
        (err: unknown) => {
          if (!live || (err instanceof Error && err.name === "AbortError")) return;
          if (err instanceof GatewayError && err.status === 403) {
            setLoad({ kind: "refused", reason: gatewayErrorMessage(err) });
            return;
          }
          const message = gatewayErrorMessage(err, "read the team's Fleet Map");
          // Keep the last map on screen and say it could not be refreshed, rather than blanking it.
          setLoad((prev) => (prev.kind === "map" ? { ...prev, error: message } : { kind: "failed", error: message }));
        },
      );
    };
    read();
    const timer = window.setInterval(read, TEAM_MAP_POLL_MS);
    return () => {
      live = false;
      window.clearInterval(timer);
      controller?.abort();
    };
  }, [team.id]);

  return (
    <div className="fmap tmap" data-testid="team-fleet-map">
      <header className="fmap-head">
        <h1 className="fmap-title">Fleet Map - {team.name}</h1>
        {load.kind === "map" && load.map.layouts.length > 1 && (
          <div className="fmap-pivot" role="group" aria-label="Lay the team's fleet out">
            {load.map.layouts.map((key) => {
              const on = (layout ?? load.map.layouts[0]) === key;
              return (
                <button
                  key={key}
                  type="button"
                  className={on ? "fmap-pivot-btn on" : "fmap-pivot-btn"}
                  aria-pressed={on}
                  onClick={() => setLayout(key)}
                >
                  {LAYOUT_LABELS[key]}
                </button>
              );
            })}
          </div>
        )}
      </header>

      {load.kind === "loading" && <div className="fmap-empty">Loading the team's fleet...</div>}
      {load.kind === "refused" && (
        <div className="fmap-empty" data-testid="team-fleet-map-refused">
          {load.reason}
        </div>
      )}
      {load.kind === "failed" && <div className="fmap-error">{load.error}</div>}
      {load.kind === "map" && (
        <>
          <p className="tmap-summary">{load.map.summary}</p>
          {load.error !== null && <div className="fmap-error">{load.error}</div>}
          {load.map.people.length === 0 ? (
            <div className="fmap-empty">{load.map.emptyText}</div>
          ) : (
            <Layout map={load.map} layout={layout ?? load.map.layouts[0]} />
          )}
        </>
      )}
    </div>
  );
}

function Layout({ map, layout }: { map: TeamFleetMap; layout: TeamFleetMapLayout }) {
  switch (layout) {
    case "by-person":
      return <ByPerson map={map} />;
    case "by-director":
      return <ByDirector map={map} />;
    case "by-repository":
      return <ByGroup map={map} kind="Repository" testId="team-fleet-map-by-repository" keyOf={(s) => s.repository} />;
    case "by-mission":
      return <ByGroup map={map} kind="Mission" testId="team-fleet-map-by-mission" keyOf={(s) => s.mission} />;
  }
}

/**
 * D4, a Developer's own Directors by repository or by mission: one lane per repository (or mission), each session in
 * it with the Director it runs on. Only offered when every session carries the field (the reader checks), so keyOf
 * always has an answer here.
 */
function ByGroup({
  map,
  kind,
  testId,
  keyOf,
}: {
  map: TeamFleetMap;
  kind: string;
  testId: string;
  keyOf: (session: TeamFleetMapSession) => string | undefined;
}) {
  const groups = new Map<string, { session: TeamFleetMapSession; director: TeamFleetMapDirector }[]>();
  for (const person of map.people) {
    for (const director of person.directors) {
      for (const session of director.sessions) {
        const key = keyOf(session);
        if (key === undefined) throw new Error(`The team Fleet Map offered By ${kind.toLowerCase()} for a session without one.`);
        const list = groups.get(key) ?? [];
        list.push({ session, director });
        groups.set(key, list);
      }
    }
  }
  const lanes = [...groups.entries()].sort(([a], [b]) => a.localeCompare(b, undefined, { sensitivity: "base" }));
  if (lanes.length === 0) return <div className="fmap-empty">No sessions running</div>;
  return (
    <div className="tmap-lanes" data-testid={testId}>
      {lanes.map(([key, entries]) => (
        <section key={key} className="fmap-lane tmap-lane" aria-label={key}>
          <div className="fmap-lane-head">
            <span className="fmap-lane-k">{kind}</span>
            <span className="fmap-lane-t">{key}</span>
          </div>
          <ul className="tmap-sessions">
            {entries.map(({ session, director }, i) => (
              <li key={`${director.name}|${session.name}|${i}`} className="tmap-session">
                <span className={`tmap-dot ${STATUS_CLASS[session.status]}`} aria-hidden="true" />
                <span className="tmap-session-name">{session.name}</span>
                <span className="tmap-owner">{director.name}</span>
                <span className="tmap-session-status">{session.status}</span>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

/** D5: one lane per person, their Directors inside it. */
function ByPerson({ map }: { map: TeamFleetMap }) {
  return (
    <div className="tmap-lanes" data-testid="team-fleet-map-by-person">
      {map.people.map((person) => (
        <section key={person.person} className="fmap-lane tmap-lane" aria-label={person.person}>
          <div className="fmap-lane-head">
            <span className="fmap-lane-k">Person</span>
            <span className="fmap-lane-t">
              {person.person}
              {person.isYou && <span className="tmap-you"> (you)</span>}
            </span>
          </div>
          {person.directors.map((director, i) => (
            <DirectorBlock key={`${director.name}|${director.machine}|${i}`} director={director} />
          ))}
        </section>
      ))}
    </div>
  );
}

/** D4: one lane per Director. */
function ByDirector({ map }: { map: TeamFleetMap }) {
  const directors = map.people.flatMap((person) => person.directors.map((director) => ({ person, director })));
  return (
    <div className="tmap-lanes" data-testid="team-fleet-map-by-director">
      {directors.map(({ person, director }, i) => (
        <section key={`${person.person}|${director.name}|${director.machine}|${i}`} className="fmap-lane tmap-lane" aria-label={director.name}>
          <div className="fmap-lane-head">
            <span className="fmap-lane-k">Director</span>
            <span className="fmap-lane-t">{director.name}</span>
            <span className="fmap-lane-sub">{director.machine}</span>
            {map.scope === "everyone" && <span className="tmap-owner">{person.person}</span>}
          </div>
          <SessionList director={director} />
        </section>
      ))}
    </div>
  );
}

function DirectorBlock({ director }: { director: TeamFleetMapDirector }) {
  return (
    <div className="tmap-director">
      <div className="tmap-director-name">{director.name}</div>
      <div className="tmap-director-machine">{director.machine}</div>
      <SessionList director={director} />
    </div>
  );
}

/** A Director's sessions: a dot, a name and a status word. Plain text - nothing here opens. */
function SessionList({ director }: { director: TeamFleetMapDirector }) {
  if (director.sessions.length === 0) return <div className="tmap-none">No sessions running</div>;
  return (
    <ul className="tmap-sessions">
      {director.sessions.map((session, i) => (
        <li key={`${session.name}|${i}`} className="tmap-session">
          <span className={`tmap-dot ${STATUS_CLASS[session.status]}`} aria-hidden="true" />
          <span className="tmap-session-name">{session.name}</span>
          <span className="tmap-session-status">{session.status}</span>
        </li>
      ))}
    </ul>
  );
}
