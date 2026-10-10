import { useCallback, useEffect, useState } from "react";
import {
  deleteFactoryMemoryNote,
  FactoryMemoryRefusal,
  getFactoryMemoryHistory,
  getFactoryMemoryNote,
  listFactoryMemory,
  restoreFactoryMemoryNote,
  setFactoryMemoryNote,
  type FactoryMemoryHistory,
  type FactoryMemoryList,
  type FactoryMemoryNote,
} from "@devthrottle/client-core/factory/factoryMemoryClient";
import { Button, ConfirmDialog, EmptyState, ErrorBanner, LoadingState } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-factory-memory";

// The factory page's Memory tab (Factory Memory mission, phase 3b, section 5.5). What the factory's sessions have
// learned, as small named notes, and the owner's five things to do with them: read one, correct it, delete it,
// see who wrote each version and when, and put an old version back. That last one - restore - only a person can
// do, and this tab is the only place it can be done from.
//
// Every call names the factory (?factory=<id>): a person is in no factory, so the Gateway cannot read it from
// the caller the way it does for a session.
//
// A CORRECTION NEVER SILENTLY OVERWRITES. The write sends the version it was made against. If a factory session
// wrote the note in the meantime, the Gateway refuses with the note as it stands now; the tab shows that text
// beside the owner's own, keeps his edit in the box, and lets him merge and save over the newer version - or
// take the newer version and drop his.
//
// Nothing here decides what a note means. The deleted flag, the version, the author and the refusal's reason are
// the Gateway's; the tab lays them out.

/** Who wrote a version, as the Gateway recorded it: the kind ("session" or "person") and the id. */
export function authorText(note: FactoryMemoryNote): string {
  return note.authorId ? `${note.authorKind} ${note.authorId}` : note.authorKind;
}

/** A Gateway UTC instant in the reader's own local date and time. */
export function writtenText(utc: string): string {
  return new Date(utc).toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function kilobytes(bytes: number): string {
  return `${(bytes / 1024).toFixed(1)} KB`;
}

type Mode = "read" | "edit" | "history";

export interface FactoryMemoryTabProps {
  factory: string;
}

export function FactoryMemoryTab({ factory }: FactoryMemoryTabProps) {
  const [list, setList] = useState<FactoryMemoryList | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [listNonce, setListNonce] = useState(0);
  const reloadList = useCallback(() => setListNonce((n) => n + 1), []);

  // The note that is open, as the Gateway last answered for it - which may be a delete.
  const [note, setNote] = useState<FactoryMemoryNote | null>(null);
  const [noteError, setNoteError] = useState<string | null>(null);
  const [opening, setOpening] = useState<string | null>(null);
  const [mode, setMode] = useState<Mode>("read");

  // A correction in progress: the text, and the version it is written against.
  const [draft, setDraft] = useState("");
  const [baseVersion, setBaseVersion] = useState(0);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  // The newer note a stale save was refused with - what the owner merges against.
  // A save that raced another writer: the version there now, and the reported sentence that leads the panel.
  const [conflict, setConflict] = useState<{ current: FactoryMemoryNote; lead: string } | null>(null);

  const [history, setHistory] = useState<FactoryMemoryHistory | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);

  const [confirmDelete, setConfirmDelete] = useState(false);
  const [restoreTarget, setRestoreTarget] = useState<FactoryMemoryNote | null>(null);

  // A deleted note is not listed, so it is reached by its name. This is how the owner gets to a deleted note's
  // history to restore it.
  const [findName, setFindName] = useState("");

  useEffect(() => {
    const ctrl = new AbortController();
    setListError(null);
    listFactoryMemory(factory, ctrl.signal).then(setList, (err: unknown) => {
      if (!ctrl.signal.aborted) setListError(describeAndReport(SURFACE, "read this factory's memory", err));
    });
    return () => ctrl.abort();
  }, [factory, listNonce]);

  const loadHistory = useCallback(
    async (name: string) => {
      setHistory(null);
      setHistoryError(null);
      try {
        setHistory(await getFactoryMemoryHistory(factory, name));
      } catch (err) {
        setHistoryError(describeAndReport(SURFACE, "read this note's history", err));
      }
    },
    [factory],
  );

  const openNote = useCallback(
    async (name: string) => {
      const wanted = name.trim();
      if (wanted.length === 0) return;
      setOpening(wanted);
      setNoteError(null);
      setSaveError(null);
      setConflict(null);
      setHistory(null);
      setMode("read");
      try {
        setNote(await getFactoryMemoryNote(factory, wanted));
      } catch (err) {
        setNote(null);
        setNoteError(describeAndReport(SURFACE, "open the note", err));
      } finally {
        setOpening(null);
      }
    },
    [factory],
  );

  const startEdit = useCallback(() => {
    if (note === null) return;
    setDraft(note.text ?? "");
    setBaseVersion(note.version);
    setConflict(null);
    setSaveError(null);
    setMode("edit");
  }, [note]);

  const save = useCallback(
    async (expectedVersion: number) => {
      if (note === null || saving) return;
      setSaving(true);
      setSaveError(null);
      try {
        const written = await setFactoryMemoryNote(factory, note.name, draft, expectedVersion);
        setNote(written);
        setConflict(null);
        setMode("read");
        reloadList();
      } catch (err) {
        if (err instanceof FactoryMemoryRefusal && err.outcome === "Stale" && err.current !== null) {
          // Someone wrote it since it was read. The owner's text stays in the box; the newer one is shown beside
          // it, and the next save is made against it only when he says so.
          // The save failed and is reported like any other; the panel then offers the two ways on.
          setConflict({ current: err.current, lead: describeAndReport(SURFACE, "save this note", err) });
        } else {
          setSaveError(describeAndReport(SURFACE, "save this note", err));
        }
      } finally {
        setSaving(false);
      }
    },
    [factory, note, draft, saving, reloadList],
  );

  const takeTheirs = useCallback(() => {
    if (conflict === null) return;
    setDraft(conflict.current.text ?? "");
    setBaseVersion(conflict.current.version);
    setConflict(null);
  }, [conflict]);

  const showHistory = useCallback(() => {
    if (note === null) return;
    setMode("history");
    void loadHistory(note.name);
  }, [note, loadHistory]);

  if (listError !== null) return <ErrorBanner message={listError} onRetry={reloadList} />;
  if (list === null) return <LoadingState />;

  const newest = history?.versions[0]?.version ?? null;

  return (
    <section className="fa-panel" data-testid="fa-memory">
      <div className="fa-toolbar fa-memory-head">
        <span className="fa-dim" data-testid="fa-memory-summary">
          {list.notes.length} of {list.maxNotes} notes . {kilobytes(list.bytes)} of {kilobytes(list.maxBytes)}
        </span>
        <form
          className="fa-save-form"
          onSubmit={(e) => {
            e.preventDefault();
            void openNote(findName);
          }}
        >
          <input
            type="text"
            aria-label="Open a note by name, including a deleted one"
            placeholder="Open a note by name, including a deleted one"
            value={findName}
            onChange={(e) => setFindName(e.target.value)}
          />
          <Button type="submit" disabled={findName.trim().length === 0 || opening !== null}>
            Open
          </Button>
        </form>
      </div>

      <div className="fa-map-grid fa-memory-grid">
        <div>
          {list.notes.length === 0 ? (
            <EmptyState message="This factory has no notes yet. Its sessions write them as they learn." />
          ) : (
            <table className="fa-table" data-testid="fa-memory-list">
              <thead>
                <tr>
                  <th>Note</th>
                  <th className="fa-num">Version</th>
                  <th>Last written by</th>
                  <th>When</th>
                </tr>
              </thead>
              <tbody>
                {list.notes.map((n) => (
                  <tr key={n.name} className={note?.name === n.name ? "fa-memory-row sel" : "fa-memory-row"}>
                    <td>
                      <button
                        type="button"
                        className="fa-memory-open"
                        aria-pressed={note?.name === n.name}
                        onClick={() => void openNote(n.name)}
                      >
                        {n.name}
                      </button>
                    </td>
                    <td className="fa-num">{n.version}</td>
                    <td className="mono">{authorText(n)}</td>
                    <td>{writtenText(n.writtenAtUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className="fa-memory-note" data-testid="fa-memory-note">
          {opening !== null && <LoadingState />}
          {noteError !== null && <ErrorBanner message={noteError} />}
          {opening === null && noteError === null && note === null && (
            <p className="fa-dim">Pick a note to read it, correct it, delete it or see its history.</p>
          )}
          {opening === null && note !== null && (
            <>
              <h3 className="fa-section-title">{note.name}</h3>
              <p className="fa-dim" data-testid="fa-memory-byline">
                {note.deleted
                  ? `Deleted in version ${note.version} by ${authorText(note)}, ${writtenText(note.writtenAtUtc)}. Its history holds every earlier version; restore one to bring it back.`
                  : `Version ${note.version}, written by ${authorText(note)}, ${writtenText(note.writtenAtUtc)}.`}
              </p>

              {mode === "read" && (
                <>
                  {!note.deleted && <pre className="fa-memory-text dt-private">{note.text}</pre>}
                  <div className="fa-inline-action">
                    {!note.deleted && (
                      <>
                        <Button variant="primary" onClick={startEdit}>
                          Correct
                        </Button>
                        <Button variant="danger" onClick={() => setConfirmDelete(true)}>
                          Delete
                        </Button>
                      </>
                    )}
                    <Button onClick={showHistory}>History</Button>
                  </div>
                </>
              )}

              {mode === "edit" && (
                <>
                  <textarea
                    className="fa-memory-edit"
                    aria-label={`Text of ${note.name}`}
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                    disabled={saving}
                    rows={12}
                  />
                  {conflict !== null && (
                    <div className="fa-memory-conflict" data-testid="fa-memory-conflict">
                      <p className="fa-memory-conflict-lead" role="alert">
                        {conflict.lead}
                      </p>
                      <p>
                        {conflict.current.deleted
                          ? `This note was deleted since you opened it: version ${conflict.current.version}, by ${authorText(conflict.current)}, ${writtenText(conflict.current.writtenAtUtc)}. Your text is still in the box. Saving brings the note back with it.`
                          : `This note changed since you opened it: version ${conflict.current.version}, by ${authorText(conflict.current)}, ${writtenText(conflict.current.writtenAtUtc)}. Your text is still in the box; the newer text is below. Merge what you need into the box, then save over version ${conflict.current.version}.`}
                      </p>
                      {!conflict.current.deleted && <pre className="fa-memory-text dt-private">{conflict.current.text}</pre>}
                      <div className="fa-inline-action">
                        <Button variant="primary" disabled={saving} onClick={() => void save(conflict.current.version)}>
                          {saving ? "Saving..." : `Save over version ${conflict.current.version}`}
                        </Button>
                        {!conflict.current.deleted && (
                          <Button disabled={saving} onClick={takeTheirs}>
                            Take version {conflict.current.version} and drop my edit
                          </Button>
                        )}
                      </div>
                    </div>
                  )}
                  {saveError !== null && (
                    <p className="fa-memory-error" role="alert">
                      {saveError}
                    </p>
                  )}
                  {conflict === null && (
                    <div className="fa-inline-action">
                      <Button variant="primary" disabled={saving} onClick={() => void save(baseVersion)}>
                        {saving ? "Saving..." : "Save"}
                      </Button>
                      <Button disabled={saving} onClick={() => setMode("read")}>
                        Cancel
                      </Button>
                    </div>
                  )}
                  {conflict !== null && (
                    <Button disabled={saving} onClick={() => { setConflict(null); setMode("read"); }}>
                      Cancel
                    </Button>
                  )}
                </>
              )}

              {mode === "history" && (
                <>
                  {historyError !== null && <ErrorBanner message={historyError} onRetry={() => void loadHistory(note.name)} />}
                  {history === null && historyError === null && <LoadingState />}
                  {history !== null && (
                    <ol className="fa-memory-history" data-testid="fa-memory-history">
                      {history.versions.map((v) => (
                        <li key={v.version} data-testid={`fa-memory-version-${v.version}`}>
                          <div className="fa-memory-version-head">
                            <strong>Version {v.version}</strong>
                            <span className="fa-dim">
                              {v.deleted ? "Deleted" : "Written"} by {authorText(v)}, {writtenText(v.writtenAtUtc)}
                            </span>
                            {!v.deleted && v.version !== newest && (
                              <Button onClick={() => setRestoreTarget(v)}>Restore</Button>
                            )}
                          </div>
                          {!v.deleted && <pre className="fa-memory-text dt-private">{v.text}</pre>}
                        </li>
                      ))}
                    </ol>
                  )}
                  <div className="fa-inline-action">
                    <Button onClick={() => setMode("read")}>Back to the note</Button>
                  </div>
                </>
              )}
            </>
          )}
        </div>
      </div>

      <ConfirmDialog
        open={confirmDelete && note !== null}
        title={`Delete the note '${note?.name ?? ""}'?`}
        message="The factory's sessions stop seeing it. The delete is kept as a version, so you can restore the note from its history here."
        confirmLabel="Delete"
        busyLabel="Deleting..."
        action="delete this note"
        onConfirm={async () => {
          if (note === null) return;
          setNote(await deleteFactoryMemoryNote(factory, note.name, note.version));
          reloadList();
        }}
        onClose={() => setConfirmDelete(false)}
      />

      <ConfirmDialog
        open={restoreTarget !== null && note !== null}
        title={`Put version ${restoreTarget?.version ?? ""} of '${note?.name ?? ""}' back?`}
        message="Its text becomes the note's newest version, written by you. Nothing is removed: the history keeps every version, including the one this replaces."
        confirmLabel="Restore"
        busyLabel="Restoring..."
        danger={false}
        action="restore this version"
        onConfirm={async () => {
          if (note === null || restoreTarget === null) return;
          // The version this person is LOOKING AT goes with the restore (review finding 4). An agent can write a
          // new version between the history opening and this button being pressed, and without telling the Gateway
          // which version was seen, the restore would bury that agent's lesson and nobody would know.
          setNote(await restoreFactoryMemoryNote(factory, note.name, restoreTarget.version, note.version));
          reloadList();
          await loadHistory(note.name);
        }}
        onClose={() => setRestoreTarget(null)}
      />
    </section>
  );
}
