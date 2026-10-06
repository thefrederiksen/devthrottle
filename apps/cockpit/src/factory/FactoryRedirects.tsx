import { Navigate, useLocation, useParams } from "react-router-dom";

// The Factory Agents area became Factories (Factories screen mission). Every old /factory-agents address - a
// bookmark, a link in an old report, a session's factory agent chip - lands on its new equivalent, keeping its query.

/** The old factory page took its tab in the query (?tab=map|agents|memory); the new one takes it in the path. */
const OLD_FACTORY_TAB: Record<string, string> = { agents: "seats", memory: "memory" };

function withQuery(path: string, params: URLSearchParams): string {
  const s = params.toString();
  return s.length === 0 ? path : `${path}?${s}`;
}

/** /factory-agents?... -> /factories?... (the "All factory agents" tab is gone; it opens the Factories tab). */
export function OldFactoriesListRedirect() {
  const params = new URLSearchParams(useLocation().search);
  if (params.get("tab") === "agents") params.delete("tab");
  return <Navigate to={withQuery("/factories", params)} replace />;
}

/** /factory-agents/waiting?... -> /factories/waiting?... */
export function OldFactoryWaitingRedirect() {
  const params = new URLSearchParams(useLocation().search);
  return <Navigate to={withQuery("/factories/waiting", params)} replace />;
}

/** /factory-agents/<factory>?tab=... -> /factories/<factory>[/<tab>] */
export function OldFactoryPageRedirect() {
  const { factory = "" } = useParams();
  const params = new URLSearchParams(useLocation().search);
  const tab = OLD_FACTORY_TAB[params.get("tab") ?? ""];
  params.delete("tab");
  const page = `/factories/${encodeURIComponent(factory)}`;
  return <Navigate to={withQuery(tab === undefined ? page : `${page}/${tab}`, params)} replace />;
}

/** /factory-agents/<factory>/<agent> -> /factories/<factory>/agents/<agent> */
export function OldFactoryAgentRedirect() {
  const { factory = "", agent = "" } = useParams();
  const params = new URLSearchParams(useLocation().search);
  return (
    <Navigate
      to={withQuery(`/factories/${encodeURIComponent(factory)}/agents/${encodeURIComponent(agent)}`, params)}
      replace
    />
  );
}
