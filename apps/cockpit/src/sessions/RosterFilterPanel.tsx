import { useEffect, useState } from "react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  type FacetOption,
  type SessionFilter,
  EMPTY_FILTER,
  MAIN_CHECKOUT,
  applyFilter,
  filterIsActive,
  machineFacet,
  repoFacet,
  savedFilterSelection,
  toggleValue,
} from "@devthrottle/client-core/sessions/filter";
import type { SavedFiltersState, SavedSessionFilter } from "@devthrottle/client-core/sessions/savedFilters";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { useDismissOnBackdrop } from "../components/useDismissOnBackdrop";
import { Button } from "../components/Button";
import "./rosterFilter.css";

const SURFACE = "cockpit-roster-filter";

// The Cockpit's "Filter sessions" panel (issue #4009), opened by the Filter button on the Sessions header. It is the
// phone's panel (apps/mobile SessionFilterPanel) on a desktop: a Repo and a Machine checklist, each with an "All ..."
// row and one checkbox per value carrying its live count from the UNFILTERED roster; union within a facet, AND across
// facets. It edits a DRAFT, so the list underneath does not reshuffle while you choose: Apply commits it, Clear shows
// every session, Cancel (or the backdrop, or Escape) changes nothing. No name is needed to filter.
//
// The account's saved filters sit at the top: one click applies one. "Save as filter..." names the current selection
// and gives it a colour; each saved filter can be edited (its name, colour and selection) and deleted. The colours and
// their hex are the Gateway's.

type Mode =
  | { kind: "pick" }
  | { kind: "save" }
  | { kind: "edit"; filter: SavedSessionFilter }
  | { kind: "delete"; filter: SavedSessionFilter };

export interface RosterFilterPanelProps {
  /** The whole roster, unfiltered: the facets and every count come from it. */
  sessions: SessionDto[];
  /** The selection this tab shows now - where the draft starts. */
  current: SessionFilter;
  saved: SavedFiltersState;
  onApplyQuick: (filter: SessionFilter) => void;
  onApplySaved: (name: string) => void;
  /** A saved filter was deleted; the tab clears its filter if it was showing that one. */
  onDeleted: (filter: SavedSessionFilter) => void;
  /** A saved filter was renamed or changed; the tab follows it if it was showing that one. */
  onEdited: (before: SavedSessionFilter, after: SavedSessionFilter) => void;
  onClear: () => void;
  onClose: () => void;
}

export function RosterFilterPanel({
  sessions,
  current,
  saved,
  onApplyQuick,
  onApplySaved,
  onDeleted,
  onEdited,
  onClear,
  onClose,
}: RosterFilterPanelProps) {
  const [draft, setDraft] = useState<SessionFilter>(current);
  const [mode, setMode] = useState<Mode>({ kind: "pick" });
  const [name, setName] = useState("");
  const [colour, setColour] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dismiss = useDismissOnBackdrop(busy ? undefined : onClose);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !busy) onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [busy, onClose]);

  // A value the draft ticks but the roster no longer holds (a saved filter's repo with no session right now) is still
  // listed, with a count of 0, so it can be seen and unticked.
  const repos = withSelected(repoFacet(sessions), draft.repos);
  const machines = withSelected(machineFacet(sessions), draft.machines);
  const firstColour = saved.colours[0]?.key ?? "";

  const startSave = () => {
    setError(null);
    setName("");
    setColour(firstColour);
    setMode({ kind: "save" });
  };

  const startEdit = (filter: SavedSessionFilter) => {
    setError(null);
    setName(filter.name);
    setColour(filter.colour);
    setDraft(savedFilterSelection(filter));
    setMode({ kind: "edit", filter });
  };

  // Leaving an edit puts back the selection the panel opened with, because the edit loaded the saved filter's own.
  const backToPick = () => {
    setError(null);
    if (mode.kind === "edit") setDraft(current);
    setMode({ kind: "pick" });
  };

  const submitSaveOrEdit = async () => {
    setError(null);
    setBusy(true);
    try {
      const input = { name, colour, repos: draft.repos, machines: draft.machines };
      if (mode.kind === "edit") {
        // The tab follows the edit only when it was showing that filter (the roster decides); editing another filter
        // from here never switches what this tab shows.
        const after = await saved.update(mode.filter.id, input);
        onEdited(mode.filter, after);
      } else {
        const created = await saved.create(input);
        onApplySaved(created.name);
      }
      onClose();
    } catch (err) {
      setError(describeAndReport(SURFACE, mode.kind === "edit" ? "change the saved filter" : "save the filter", err));
    } finally {
      setBusy(false);
    }
  };

  const confirmDelete = async (filter: SavedSessionFilter) => {
    setError(null);
    setBusy(true);
    try {
      await saved.remove(filter.id);
      onDeleted(filter);
      setMode({ kind: "pick" });
    } catch (err) {
      setError(describeAndReport(SURFACE, "delete the saved filter", err));
    } finally {
      setBusy(false);
    }
  };

  const naming = mode.kind === "save" || mode.kind === "edit";

  return (
    <div className="ui-modal-backdrop" {...dismiss}>
      <section className="rf-panel" role="dialog" aria-modal="true" aria-label="Filter sessions">
        <header className="rf-head">
          <h2 className="rf-title">{mode.kind === "edit" ? `Edit "${mode.filter.name}"` : "Filter sessions"}</h2>
        </header>

        <div className="rf-body">
          {mode.kind !== "edit" && (
            <SavedFilterList
              saved={saved}
              sessions={sessions}
              busy={busy}
              deleting={mode.kind === "delete" ? mode.filter : null}
              onApply={(f) => {
                onApplySaved(f.name);
                onClose();
              }}
              onEdit={startEdit}
              onAskDelete={(f) => {
                setError(null);
                setMode({ kind: "delete", filter: f });
              }}
              onConfirmDelete={(f) => void confirmDelete(f)}
              onKeep={backToPick}
            />
          )}

          {naming && (
            <div className="rf-name-form" data-testid="rf-name-form">
              <label className="rf-label" htmlFor="rf-name">
                Name
              </label>
              <input
                id="rf-name"
                className="rf-name-input"
                value={name}
                maxLength={40}
                autoFocus
                placeholder="e.g. Product"
                onChange={(e) => setName(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && !busy) void submitSaveOrEdit();
                }}
              />
              <div className="rf-label">Colour</div>
              <div className="rf-swatches" role="radiogroup" aria-label="Colour">
                {saved.colours.map((c) => (
                  <button
                    key={c.key}
                    type="button"
                    role="radio"
                    aria-checked={colour === c.key}
                    aria-label={c.label}
                    title={c.label}
                    className={`rf-swatch${colour === c.key ? " on" : ""}`}
                    style={{ backgroundColor: c.hex }}
                    onClick={() => setColour(c.key)}
                  />
                ))}
              </div>
              <div className="rf-hint">
                {mode.kind === "edit"
                  ? "Change the name, the colour, or the ticks below, then save."
                  : "Saves the repos and machines ticked below."}
              </div>
            </div>
          )}

          {error !== null && (
            <div className="rf-error" role="alert">
              {error}
            </div>
          )}

          <FilterFacet
            heading="Repo"
            options={repos}
            selected={draft.repos}
            onToggle={(v) => setDraft((d) => ({ ...d, repos: toggleValue(d.repos, v) }))}
            onAll={() => setDraft((d) => ({ ...d, repos: [] }))}
            allLabel="All repos"
          />
          <FilterFacet
            heading="Machine"
            options={machines}
            selected={draft.machines}
            onToggle={(v) => setDraft((d) => ({ ...d, machines: toggleValue(d.machines, v) }))}
            onAll={() => setDraft((d) => ({ ...d, machines: [] }))}
            allLabel="All machines"
          />
        </div>

        <footer className="rf-foot">
          {naming ? (
            <>
              <span className="rf-foot-spacer" />
              <Button variant="secondary" disabled={busy} onClick={backToPick}>
                Back
              </Button>
              <Button variant="primary" disabled={busy || name.trim().length === 0 || !filterIsActive(draft)} onClick={() => void submitSaveOrEdit()}>
                {busy ? "Saving..." : mode.kind === "edit" ? "Save changes" : "Save filter"}
              </Button>
            </>
          ) : (
            <>
              <button type="button" className="rf-save-link" disabled={!filterIsActive(draft)} onClick={startSave}>
                Save as filter...
              </button>
              <span className="rf-foot-spacer" />
              <Button variant="secondary" disabled={busy} onClick={onClose}>
                Cancel
              </Button>
              <Button
                variant="secondary"
                disabled={busy}
                onClick={() => {
                  setDraft(EMPTY_FILTER);
                  onClear();
                  onClose();
                }}
              >
                Clear
              </Button>
              <Button
                variant="primary"
                disabled={busy}
                onClick={() => {
                  onApplyQuick(draft);
                  onClose();
                }}
              >
                Apply
              </Button>
            </>
          )}
        </footer>
      </section>
    </div>
  );
}

// The saved filters at the top of the panel: colour, name, how many sessions it shows now, and Edit / Delete. A delete
// asks once, in the row, rather than through a browser dialog.
function SavedFilterList({
  saved,
  sessions,
  busy,
  deleting,
  onApply,
  onEdit,
  onAskDelete,
  onConfirmDelete,
  onKeep,
}: {
  saved: SavedFiltersState;
  sessions: SessionDto[];
  busy: boolean;
  deleting: SavedSessionFilter | null;
  onApply: (f: SavedSessionFilter) => void;
  onEdit: (f: SavedSessionFilter) => void;
  onAskDelete: (f: SavedSessionFilter) => void;
  onConfirmDelete: (f: SavedSessionFilter) => void;
  onKeep: () => void;
}) {
  return (
    <div className="rf-saved" data-testid="rf-saved">
      <h3 className="rf-facet-heading">Saved filters</h3>
      {saved.filters === null && saved.error === null && <div className="rf-dim">Loading saved filters...</div>}
      {saved.error !== null && (
        // error-reported-by: useSavedFilters
        <div className="rf-error" role="alert">
          Could not load your saved filters: {saved.error}
        </div>
      )}
      {saved.filters !== null && saved.filters.length === 0 && (
        <div className="rf-dim">None yet. Tick some repos or machines below, then "Save as filter...".</div>
      )}
      {saved.filters?.map((f) =>
        deleting?.id === f.id ? (
          <div key={f.id} className="rf-saved-row rf-saved-confirm" role="group" aria-label={`Delete ${f.name}`}>
            <span className="rf-saved-name">Delete "{f.name}"?</span>
            <Button variant="danger" disabled={busy} onClick={() => onConfirmDelete(f)}>
              {busy ? "Deleting..." : "Delete"}
            </Button>
            <Button variant="secondary" disabled={busy} onClick={onKeep}>
              Keep
            </Button>
          </div>
        ) : (
          <div key={f.id} className="rf-saved-row">
            <button type="button" className="rf-saved-apply" onClick={() => onApply(f)} title={`Show ${describe(f)}`}>
              <span className="rf-saved-dot" style={{ backgroundColor: f.colourHex }} aria-hidden="true" />
              <span className="rf-saved-name">{f.name}</span>
              <span className="rf-saved-count">{applyFilter(sessions, savedFilterSelection(f)).length}</span>
            </button>
            <button type="button" className="rf-saved-action" disabled={busy} onClick={() => onEdit(f)} aria-label={`Edit ${f.name}`}>
              Edit
            </button>
            <button type="button" className="rf-saved-action" disabled={busy} onClick={() => onAskDelete(f)} aria-label={`Delete ${f.name}`}>
              Delete
            </button>
          </div>
        ),
      )}
    </div>
  );
}

// One facet: the "All ..." row (on when nothing in the facet is ticked), then a checkbox row per value with its count.
// A repo worked on in worktrees carries a "7 worktrees" toggle that lists them under it, each with its count. That list
// is information only - no checkbox - because the filter is on the repo: worktrees come and go.
function FilterFacet({
  heading,
  options,
  selected,
  onToggle,
  onAll,
  allLabel,
}: {
  heading: string;
  options: (FacetOption & { worktrees?: FacetOption[] })[];
  selected: string[];
  onToggle: (value: string) => void;
  onAll: () => void;
  allLabel: string;
}) {
  const [expanded, setExpanded] = useState<string[]>([]);
  const allActive = selected.length === 0;
  return (
    <div className="rf-facet">
      <h3 className="rf-facet-heading">{heading}</h3>
      <button type="button" className={`rf-opt${allActive ? " on" : ""}`} onClick={onAll} aria-pressed={allActive}>
        <span className={`rf-radio${allActive ? " on" : ""}`} aria-hidden="true" />
        <span className="rf-opt-label">{allLabel}</span>
      </button>
      {options.length === 0 && <div className="rf-dim">No {heading.toLowerCase()} names in the roster yet.</div>}
      {options.map((opt) => {
        const on = selected.includes(opt.value);
        const worktrees = opt.worktrees ?? [];
        const worktreeCount = worktrees.filter((w) => w.value !== MAIN_CHECKOUT).length;
        const open = expanded.includes(opt.value);
        return (
          <div key={opt.value} className="rf-opt-group">
            <div className="rf-opt-row">
              <button type="button" className={`rf-opt${on ? " on" : ""}`} onClick={() => onToggle(opt.value)} aria-pressed={on}>
                <span className={`rf-check${on ? " on" : ""}`} aria-hidden="true">
                  {on ? "✓" : ""}
                </span>
                <span className="rf-opt-label">{opt.value}</span>
                <span className="rf-opt-count">{opt.count}</span>
              </button>
              {worktreeCount > 0 && (
                <button
                  type="button"
                  className="rf-worktree-toggle"
                  aria-expanded={open}
                  aria-label={`${open ? "Hide" : "Show"} the worktrees of ${opt.value}`}
                  onClick={() => setExpanded((e) => toggleValue(e, opt.value))}
                >
                  {open ? "▾" : "▸"} {worktreeCount} {worktreeCount === 1 ? "worktree" : "worktrees"}
                </button>
              )}
            </div>
            {open && worktreeCount > 0 && (
              <ul className="rf-worktrees" aria-label={`Worktrees of ${opt.value}`}>
                {worktrees.map((w) => (
                  <li key={w.value} className={`rf-worktree${w.value === MAIN_CHECKOUT ? " main" : ""}`}>
                    <span className="rf-opt-label">{w.value === MAIN_CHECKOUT ? "main checkout" : w.value}</span>
                    <span className="rf-opt-count">{w.count}</span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        );
      })}
    </div>
  );
}

function withSelected<T extends FacetOption>(options: T[], selected: string[]): (T | FacetOption)[] {
  const missing: FacetOption[] = selected.filter((v) => !options.some((o) => o.value === v)).map((value) => ({ value, count: 0 }));
  return missing.length === 0 ? options : [...options, ...missing].sort((a, b) => a.value.localeCompare(b.value));
}

function describe(f: SavedSessionFilter): string {
  return [...f.repos, ...f.machines].join(", ");
}
