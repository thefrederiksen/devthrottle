// Client-side roster filtering by machine and repo (the mobile "Filter sessions" panel). The roster
// can grow large across a fleet of machines and repositories; this lets the phone narrow it to one
// machine and/or one set of repositories. The model is deliberately simple and pure so it is fully
// unit-tested and the UI (the full-screen panel) stays a thin renderer over it.
import type { SessionDto } from "../api/client";
import { classify, machineCanBeActedOn, repoLeaf } from "./ordering";

// The active selection. An empty list for a facet means "all" - no restriction on that facet - so the
// default (no filter) is two empty lists. Selections combine across facets (machine AND repo) and
// union within a facet (machine A OR machine B).
export interface SessionFilter {
  machines: string[];
  repos: string[];
}

export const EMPTY_FILTER: SessionFilter = { machines: [], repos: [] };

// One selectable value in a facet, with the count of sessions that carry it in the full roster. The
// count is always computed from the UNFILTERED roster so the panel shows every machine/repo you could
// pick and how many sessions each holds, regardless of what is currently selected.
export interface FacetOption {
  value: string;
  count: number;
}

// The machine a session runs on, trimmed. Empty string when the Gateway did not stamp a machine name
// (older sessions); such a session is grouped under "(unknown)" in the panel so it stays selectable.
export function machineName(s: SessionDto): string {
  return (s.machineName ?? "").trim();
}

// True when the filter restricts the roster at all (either facet has a selection). The app bar shows
// the "filter active" state and the summary strip appears only when this is true.
export function filterIsActive(f: SessionFilter): boolean {
  return f.machines.length > 0 || f.repos.length > 0;
}

// Keep a session when it matches EVERY active facet: its machine is among the selected machines (or no
// machine is selected) AND its repo is among the selected repos (or no repo is selected).
export function sessionMatchesFilter(s: SessionDto, f: SessionFilter): boolean {
  if (f.machines.length > 0 && !f.machines.includes(machineName(s))) return false;
  if (f.repos.length > 0 && !f.repos.includes(repoLeaf(s))) return false;
  return true;
}

// The roster narrowed to the sessions that match the filter, preserving input order (the caller has
// already ordered it). An inactive filter returns the roster unchanged.
export function applyFilter(sessions: SessionDto[], f: SessionFilter): SessionDto[] {
  if (!filterIsActive(f)) return sessions;
  return sessions.filter((s) => sessionMatchesFilter(s, f));
}

// The distinct machine values in the roster with per-value counts, sorted by name. Built from the full
// roster so the panel always offers every machine, whatever is selected.
export function machineFacet(sessions: SessionDto[]): FacetOption[] {
  return countBy(sessions, machineName);
}

// The distinct repo leaves in the roster with per-value counts, sorted by name.
export function repoFacet(sessions: SessionDto[]): FacetOption[] {
  return countBy(sessions, repoLeaf);
}

// A short human summary of what is selected, for the app-bar summary strip - e.g.
// "SOREN_NORTH, devthrottle". Machines first, then repos, in selection order. Empty when inactive.
export function filterSummary(f: SessionFilter): string {
  return [...f.machines, ...f.repos].filter((v) => v.length > 0).join(", ");
}

// Toggle one value in a facet list immutably: add it if absent, remove it if present. Used by the
// panel's checkboxes without the panel owning any set logic.
export function toggleValue(values: string[], value: string): string[] {
  return values.includes(value) ? values.filter((v) => v !== value) : [...values, value];
}

// Drop any selected value that no longer exists in the live roster, so a filter pinned to a machine or
// repo that has gone away does not silently hide the whole list forever. Returns the same reference
// when nothing changed so callers can skip a needless state update.
export function pruneFilter(f: SessionFilter, sessions: SessionDto[]): SessionFilter {
  const machines = new Set(sessions.map(machineName));
  const repos = new Set(sessions.map(repoLeaf));
  const keptMachines = f.machines.filter((m) => machines.has(m));
  const keptRepos = f.repos.filter((r) => repos.has(r));
  if (keptMachines.length === f.machines.length && keptRepos.length === f.repos.length) return f;
  return { machines: keptMachines, repos: keptRepos };
}

// Count sessions by a string key, dropping empty keys, sorted alphabetically by value.
function countBy(sessions: SessionDto[], key: (s: SessionDto) => string): FacetOption[] {
  const counts = new Map<string, number>();
  for (const s of sessions) {
    const value = key(s);
    if (value.length === 0) continue;
    counts.set(value, (counts.get(value) ?? 0) + 1);
  }
  return [...counts.entries()]
    .map(([value, count]) => ({ value, count }))
    .sort((a, b) => a.value.localeCompare(b.value));
}

// ---------------------------------------------------------------------------------------------------------------------
// The Cockpit's filter (issue #4009). The phone keeps its filter in localStorage, which is right for one phone. The
// Cockpit is opened in several browser tabs at once - one for the product repos, one for a client's - so its filter
// lives in each tab's ADDRESS: `?filter=<saved name>` for a saved filter, `?repo=a,b&machine=x` for a quick one. Two
// tabs never overwrite each other, and a refresh or a bookmark keeps the filter. What a filter MATCHES is still the one
// rule above, so the phone and the Cockpit cannot disagree.

// The address parameters the filter owns. Every other parameter (the session screen's `tab`, `report`) is left alone.
export const FILTER_PARAM = "filter";
export const REPO_PARAM = "repo";
export const MACHINE_PARAM = "machine";

// A saved filter, as far as the matching rule needs it: a name and the values it selects. The Gateway's full record
// (sessions/savedFilters.ts) carries this shape and more.
export interface NamedFilter {
  name: string;
  repos: string[];
  machines: string[];
}

// What the address asks for: nothing, a saved filter by name, or a quick (unsaved) selection.
export type FilterQuery =
  | { kind: "none" }
  | { kind: "saved"; name: string }
  | { kind: "quick"; filter: SessionFilter };

// Read the filter out of an address's query string. A saved name wins over quick values if both are present, because
// a saved filter is the more specific ask. Values are comma-separated and a parameter may also repeat; blanks and
// duplicates are dropped, first order kept.
export function parseFilterQuery(search: string | URLSearchParams): FilterQuery {
  const params = typeof search === "string" ? new URLSearchParams(search) : search;
  const name = (params.get(FILTER_PARAM) ?? "").trim();
  if (name.length > 0) return { kind: "saved", name };
  const filter: SessionFilter = { repos: listParam(params, REPO_PARAM), machines: listParam(params, MACHINE_PARAM) };
  return filterIsActive(filter) ? { kind: "quick", filter } : { kind: "none" };
}

// Write a filter into a copy of the given query string, replacing whatever filter it held and keeping every other
// parameter. Returns the new query string without its leading "?" (empty when nothing is left).
export function writeFilterQuery(search: string | URLSearchParams, query: FilterQuery): string {
  const params = new URLSearchParams(typeof search === "string" ? search : search.toString());
  params.delete(FILTER_PARAM);
  params.delete(REPO_PARAM);
  params.delete(MACHINE_PARAM);
  if (query.kind === "saved") {
    params.set(FILTER_PARAM, query.name);
  } else if (query.kind === "quick") {
    if (query.filter.repos.length > 0) params.set(REPO_PARAM, query.filter.repos.join(","));
    if (query.filter.machines.length > 0) params.set(MACHINE_PARAM, query.filter.machines.join(","));
  }
  // URLSearchParams escapes the comma; the address reads better with it bare, and a comma is a legal query character.
  return params.toString().replace(/%2C/gi, ",");
}

// Only the filter's part of a query string, with its "?" - what a link to another session in the same tab carries so
// the filter survives the click. Empty when no filter is set.
export function filterSearchOf(search: string | URLSearchParams): string {
  const qs = writeFilterQuery("", parseFilterQuery(search));
  return qs.length > 0 ? `?${qs}` : "";
}

// The saved filter with this name, ignoring case (names are unique ignoring case on the Gateway), or null.
export function findSavedFilter<T extends NamedFilter>(saved: readonly T[], name: string): T | null {
  const key = name.trim().toLowerCase();
  return saved.find((f) => f.name.trim().toLowerCase() === key) ?? null;
}

// The selection a saved filter stands for.
export function savedFilterSelection(saved: NamedFilter): SessionFilter {
  return { repos: [...saved.repos], machines: [...saved.machines] };
}

// The address's filter resolved against the saved filters:
//   none     - no filter; every session shows.
//   loading  - a saved name was asked for and the saved filters have not arrived yet. Nothing is shown as "all".
//   missing  - a saved name that no longer exists. Shown as a clear message, never silently as every session.
//   active   - a selection to apply, with the saved filter it came from when it came from one.
export type ResolvedFilter<T extends NamedFilter = NamedFilter> =
  | { status: "none" }
  | { status: "loading"; name: string }
  | { status: "missing"; name: string }
  | { status: "active"; filter: SessionFilter; saved: T | null };

export function resolveFilterQuery<T extends NamedFilter>(query: FilterQuery, saved: readonly T[] | null): ResolvedFilter<T> {
  if (query.kind === "none") return { status: "none" };
  if (query.kind === "quick") return { status: "active", filter: query.filter, saved: null };
  if (saved === null) return { status: "loading", name: query.name };
  const found = findSavedFilter(saved, query.name);
  return found ? { status: "active", filter: savedFilterSelection(found), saved: found } : { status: "missing", name: query.name };
}

// How many values a selection ticks, across both facets - the "n" in the "Filter (n)" button.
export function selectedValueCount(f: SessionFilter): number {
  return f.repos.length + f.machines.length;
}

// The sessions OUTSIDE the filter that are waiting on the person, by the same rule as the app badge: needs you, on a
// machine that can be acted on. A filter must never silently hide a stuck session, so the Cockpit names these in a
// red line above the filtered list. Input order is kept.
export function outsideNeedingYou(sessions: SessionDto[], f: SessionFilter): SessionDto[] {
  if (!filterIsActive(f)) return [];
  return sessions.filter((s) => !sessionMatchesFilter(s, f) && classify(s) === "needsYou" && machineCanBeActedOn(s));
}

// The "New session" picker's repositories with the filter's repos first (in the filter's order), then the rest in
// their given order. A repository is matched by the last part of its path, which is what the filter holds. A filter
// repo the picker does not know is not invented. `pathOf` reads an item's path (the identity for plain strings).
export function reposFilterFirst<T>(items: readonly T[], f: SessionFilter, pathOf: (item: T) => string): T[] {
  if (f.repos.length === 0) return [...items];
  const first: T[] = [];
  for (const wanted of f.repos) {
    for (const item of items) if (leafName(pathOf(item)) === wanted && !first.includes(item)) first.push(item);
  }
  return [...first, ...items.filter((item) => !first.includes(item))];
}

// What an empty filtered list says: "No sessions in devthrottle, website on DEVLINUX right now".
export function emptyFilterMessage(f: SessionFilter): string {
  const parts = ["No sessions"];
  if (f.repos.length > 0) parts.push(`in ${f.repos.join(", ")}`);
  if (f.machines.length > 0) parts.push(`on ${f.machines.join(", ")}`);
  parts.push("right now");
  return parts.join(" ");
}

// The browser tab's title with a saved filter's name in front, so several Cockpit tabs can be told apart at a glance.
export function filteredDocumentTitle(baseTitle: string, savedName: string | null): string {
  return savedName ? `${savedName} - ${baseTitle}` : baseTitle;
}

// True when two selections pick the same values in each facet, in any order.
export function sameSelection(a: SessionFilter, b: SessionFilter): boolean {
  const same = (x: string[], y: string[]) => x.length === y.length && x.every((v) => y.includes(v));
  return same(a.repos, b.repos) && same(a.machines, b.machines);
}

function listParam(params: URLSearchParams, key: string): string[] {
  const out: string[] = [];
  for (const raw of params.getAll(key)) {
    for (const part of raw.split(",")) {
      const v = part.trim();
      if (v.length > 0 && !out.includes(v)) out.push(v);
    }
  }
  return out;
}

function leafName(path: string): string {
  const parts = path.trim().split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path.trim();
}
