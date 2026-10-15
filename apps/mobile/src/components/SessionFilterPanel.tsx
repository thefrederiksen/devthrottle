import { useState } from "react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  type FacetOption,
  type SessionFilter,
  EMPTY_FILTER,
  applyFilter,
  filterIsActive,
  machineFacet,
  repoFacet,
  savedFilterSelection,
  toggleValue,
} from "@devthrottle/client-core/sessions/filter";
import { useSavedFilters } from "@devthrottle/client-core/sessions/savedFilters";

// The full-screen "Filter sessions" panel, opened by the app-bar funnel icon. It edits a DRAFT copy of
// the active filter so the roster underneath does not reshuffle while you are still choosing; Apply
// commits the draft, Clear resets it to nothing, and the back arrow / overlay tap discards it. The
// facet lists (Machine, Repo) are built from the full, unfiltered roster so every machine and repo is
// always offered with its live count. Within a facet, "All ..." is the no-selection state; picking one
// or more specific values narrows the roster (union within a facet, AND across facets).
export function SessionFilterPanel({
  sessions,
  filter,
  onApply,
  onClose,
}: {
  sessions: SessionDto[];
  filter: SessionFilter;
  onApply: (next: SessionFilter) => void;
  onClose: () => void;
}) {
  const [draft, setDraft] = useState<SessionFilter>(filter);
  // The account's saved filters (issue #4009), named and coloured in the Cockpit. On the phone one tap applies one;
  // they are made, renamed and deleted in the Cockpit.
  const saved = useSavedFilters();
  const machines = machineFacet(sessions);
  const repos = repoFacet(sessions);

  const toggleMachine = (value: string) =>
    setDraft((d) => ({ ...d, machines: toggleValue(d.machines, value) }));
  const toggleRepo = (value: string) =>
    setDraft((d) => ({ ...d, repos: toggleValue(d.repos, value) }));

  return (
    <div className="filter-overlay" role="presentation" onClick={onClose}>
      <section
        className="filter-panel"
        role="dialog"
        aria-label="Filter sessions"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="filter-head">
          <button type="button" className="filter-back" aria-label="Close filter" onClick={onClose}>
            {"<"}
          </button>
          <h2 className="filter-title">Filter sessions</h2>
          <button
            type="button"
            className="filter-clear"
            onClick={() => setDraft(EMPTY_FILTER)}
            disabled={!filterIsActive(draft)}
          >
            Clear
          </button>
        </header>

        <div className="filter-body">
          {saved.error !== null && (
            // error-reported-by: useSavedFilters
            <div className="filter-facet-note">Could not load your saved filters: {saved.error}</div>
          )}
          {saved.filters !== null && saved.filters.length > 0 && (
            <div className="filter-facet">
              <h3 className="filter-facet-heading">Saved filters</h3>
              {saved.filters.map((f) => (
                <button key={f.id} type="button" className="filter-opt" onClick={() => onApply(savedFilterSelection(f))}>
                  <span className="filter-saved-dot" style={{ backgroundColor: f.colourHex }} aria-hidden="true" />
                  <span className="filter-opt-label">{f.name}</span>
                  <span className="filter-opt-count">{applyFilter(sessions, savedFilterSelection(f)).length}</span>
                </button>
              ))}
            </div>
          )}
          <FilterFacet
            heading="Machine"
            options={machines}
            selected={draft.machines}
            onToggle={toggleMachine}
            onAll={() => setDraft((d) => ({ ...d, machines: [] }))}
            allLabel="All machines"
          />
          <FilterFacet
            heading="Repo"
            options={repos}
            selected={draft.repos}
            onToggle={toggleRepo}
            onAll={() => setDraft((d) => ({ ...d, repos: [] }))}
            allLabel="All repos"
          />
        </div>

        <footer className="filter-foot">
          <button type="button" className="filter-apply" onClick={() => onApply(draft)}>
            Apply filter
          </button>
        </footer>
      </section>
    </div>
  );
}

// One facet block: an "All ..." reset row (selected when nothing in this facet is picked) followed by a
// checkbox row per value with its session count. Renders nothing when the roster has no values for the
// facet (e.g. no machine names yet), so an empty section never shows.
function FilterFacet({
  heading,
  options,
  selected,
  onToggle,
  onAll,
  allLabel,
}: {
  heading: string;
  options: FacetOption[];
  selected: string[];
  onToggle: (value: string) => void;
  onAll: () => void;
  allLabel: string;
}) {
  if (options.length === 0) return null;
  const allActive = selected.length === 0;
  return (
    <div className="filter-facet">
      <h3 className="filter-facet-heading">{heading}</h3>
      <button
        type="button"
        className={`filter-opt${allActive ? " filter-opt-on" : ""}`}
        onClick={onAll}
      >
        <span className={`filter-radio${allActive ? " on" : ""}`} aria-hidden="true" />
        <span className="filter-opt-label">{allLabel}</span>
      </button>
      {options.map((opt) => {
        const on = selected.includes(opt.value);
        return (
          <button
            key={opt.value}
            type="button"
            className={`filter-opt${on ? " filter-opt-on" : ""}`}
            onClick={() => onToggle(opt.value)}
            aria-pressed={on}
          >
            <span className={`filter-check-box${on ? " on" : ""}`} aria-hidden="true">
              {on ? "x" : ""}
            </span>
            <span className="filter-opt-label">{opt.value}</span>
            <span className="filter-opt-count">{opt.count}</span>
          </button>
        );
      })}
    </div>
  );
}
