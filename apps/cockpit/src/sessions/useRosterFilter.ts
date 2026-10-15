import { useCallback, useMemo } from "react";
import { useSearchParams } from "react-router-dom";
import {
  type FilterQuery,
  type ResolvedFilter,
  type SessionFilter,
  filterIsActive,
  parseFilterQuery,
  resolveFilterQuery,
  writeFilterQuery,
} from "@devthrottle/client-core/sessions/filter";
import {
  type SavedFiltersState,
  type SavedSessionFilter,
  useSavedFilters,
} from "@devthrottle/client-core/sessions/savedFilters";

// The Sessions filter for THIS browser tab (issue #4009). The filter is read from, and written to, the tab's own
// address - `?filter=<saved name>` or `?repo=a,b&machine=x` - never shared storage, so two Cockpit tabs each keep their
// own filter, and a refresh or a bookmark keeps it. Writing REPLACES the address entry: changing the filter is like
// switching a view, not a place to go back to. Every other address parameter (the session screen's tab and report) is
// kept as it is.
export interface RosterFilter {
  /** What the address asks for. */
  query: FilterQuery;
  /** That ask resolved against the saved filters: none, loading, missing, or an active selection. */
  resolved: ResolvedFilter<SavedSessionFilter>;
  /** The account's saved filters, and the writes on them. */
  saved: SavedFiltersState;
  /** Apply a quick (unsaved) selection; an empty selection clears the filter. */
  applyQuick: (filter: SessionFilter) => void;
  /** Apply a saved filter by its name. */
  applySaved: (name: string) => void;
  /** Show every session. */
  clear: () => void;
}

export function useRosterFilter(): RosterFilter {
  const [searchParams, setSearchParams] = useSearchParams();
  const saved = useSavedFilters();
  const query = useMemo(() => parseFilterQuery(searchParams), [searchParams]);
  const resolved = resolveFilterQuery(query, saved.filters);

  const setQuery = useCallback(
    (next: FilterQuery) => setSearchParams((current) => new URLSearchParams(writeFilterQuery(current, next)), { replace: true }),
    [setSearchParams],
  );
  const applyQuick = useCallback(
    (filter: SessionFilter) => setQuery(filterIsActive(filter) ? { kind: "quick", filter } : { kind: "none" }),
    [setQuery],
  );
  const applySaved = useCallback((name: string) => setQuery({ kind: "saved", name }), [setQuery]);
  const clear = useCallback(() => setQuery({ kind: "none" }), [setQuery]);

  return { query, resolved, saved, applyQuick, applySaved, clear };
}
