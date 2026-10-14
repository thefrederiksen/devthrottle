import { useCallback, useEffect, useState } from "react";
import {
  bringBackFactoryBrief,
  FactoryBriefRefusal,
  getFactoryBriefEditor,
  getFactoryBriefHistory,
  getFactoryBriefVersion,
  publishFactoryBrief,
  saveFactoryBriefDraft,
  type FactoryBriefAction,
  type FactoryBriefEditorView,
  type FactoryBriefHistoryView,
  type FactoryBriefVersion,
  type FactoryBriefVersionView,
} from "@devthrottle/client-core/factory/factoryBriefClient";
import type { FactorySeatBrief } from "@devthrottle/client-core/factory/factoriesScreenClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ConfirmDialog, ErrorBanner, LoadingState } from "../components";
import { ToneChip } from "./FactoryParts";

const SURFACE = "cockpit-factory-seat-brief";

// "What this seat does" on a seat's page (Factory Seats mission, phase 2b): the seat's brief, read from the Gateway,
// with its editor and its history. A seat with a published brief shows the text, its version, who published it and
// when. A seat with only drafts says so. A seat with no brief on the Gateway shows where its brief file is and
// offers to write the brief here.
//
// Rule 7: every word is the Gateway's (FactoryBriefPanelFold). This file lays the fields out, keeps what the owner
// is typing, and sends back what a button named. It never decides what a version's state means or which version a
// button may act on - a button exists because the Gateway sent it.
//
// NOTHING TYPED IS LOST, AND NOTHING IS OVERWRITTEN SILENTLY. A save states the version the text was started from.
// When a session saved a newer version meanwhile, the Gateway refuses and sends that version: it is shown beside the
// owner's own text, which stays in the box, and the next save is made after the newer version only when the owner
// says so.

type Mode = "read" | "edit" | "history";

export interface FactorySeatBriefPanelProps {
  factory: string;
  seat: string;
  brief: FactorySeatBrief;
  /** Something was saved, published or brought back: the Gateway's sentence for the page, which then reloads. */
  onChanged: (text: string) => void;
}

export function FactorySeatBriefPanel({ factory, seat, brief, onChanged }: FactorySeatBriefPanelProps) {
  const [mode, setMode] = useState<Mode>("read");

  return (
    // The editor and the history need the page's whole width; the brief itself sits beside the other panels.
    <section className={mode === "read" ? "fa-panel" : "fa-panel fa-seat-wide"} data-testid="fa-seat-brief">
      <h2 className="fa-section-title">{brief.heading}</h2>

      {mode === "read" && (
        <>
          {brief.versionText !== null && (
            <p className="fa-dim" data-testid="fa-seat-brief-version">
              {brief.versionText}
            </p>
          )}
          {brief.text !== null && (
            <pre className="fa-memory-text dt-private" data-testid="fa-seat-brief-text">
              {brief.text}
            </pre>
          )}
          {brief.draftText !== null && (
            <p className="fa-brief-draft" data-testid="fa-seat-brief-draft">
              {brief.draftText}
            </p>
          )}
          {brief.waitingText !== null && (
            <div className="fa-warn" role="status" data-testid="fa-seat-brief-waiting">
              {brief.waitingText}
            </div>
          )}
          {brief.pathText !== null && (
            <dl className="fa-seat-facts">
              <dt>{brief.pathLabel}</dt>
              <dd className="mono dt-private" data-testid="fa-seat-brief-path">
                {brief.pathText}
              </dd>
              <dt>{brief.computerLabel}</dt>
              <dd>{brief.computerText}</dd>
            </dl>
          )}
          {brief.note !== null && <p className="fa-dim">{brief.note}</p>}
          <div className="fa-inline-action">
            <Button variant="primary" data-testid="fa-seat-brief-edit" onClick={() => setMode("edit")}>
              {brief.editLabel}
            </Button>
            {brief.historyLabel !== null && (
              <Button variant="secondary" data-testid="fa-seat-brief-history" onClick={() => setMode("history")}>
                {brief.historyLabel}
              </Button>
            )}
          </div>
        </>
      )}

      {mode === "edit" && (
        <BriefEditor
          factory={factory}
          seat={seat}
          onClose={() => setMode("read")}
          onDone={(text) => {
            setMode("read");
            onChanged(text);
          }}
        />
      )}

      {mode === "history" && <BriefHistory factory={factory} seat={seat} onClose={() => setMode("read")} onChanged={onChanged} />}
    </section>
  );
}

/** The newer version a stale save was refused with, and the reported sentence that leads the panel showing it. */
interface Conflict {
  lead: string;
  current: FactoryBriefVersion;
}

// The editor. It starts from what the Gateway says it starts from, and remembers which version that was: a save
// states it, so the Gateway can refuse when it is no longer the newest.
function BriefEditor({
  factory,
  seat,
  onClose,
  onDone,
}: {
  factory: string;
  seat: string;
  onClose: () => void;
  onDone: (text: string) => void;
}) {
  const [view, setView] = useState<FactoryBriefEditorView | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  const [text, setText] = useState("");
  // The version the text in the box was started from, and that version's own text.
  const [base, setBase] = useState<{ version: number; text: string }>({ version: 0, text: "" });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState<Conflict | null>(null);
  const [confirming, setConfirming] = useState(false);

  useEffect(() => {
    const ctrl = new AbortController();
    setLoadError(null);
    getFactoryBriefEditor(factory, seat, ctrl.signal).then(
      (loaded) => {
        setView(loaded);
        setText(loaded.text);
        setBase({ version: loaded.expectedVersion, text: loaded.text });
      },
      (err: unknown) => {
        if (!ctrl.signal.aborted) setLoadError(describeAndReport(SURFACE, "open the brief's editor", err));
      },
    );
    return () => ctrl.abort();
  }, [factory, seat, nonce]);

  /** True when the Gateway refused because a newer version exists; that version is then shown beside the text. */
  const showIfStale = useCallback((err: unknown, action: string): boolean => {
    if (!(err instanceof FactoryBriefRefusal) || err.outcome !== "Stale" || err.current === null) return false;
    setConflict({ lead: describeAndReport(SURFACE, action, err), current: err.current });
    return true;
  }, []);

  const save = useCallback(
    async (expectedVersion: number) => {
      if (view === null || saving) return;
      setSaving(true);
      setError(null);
      try {
        await saveFactoryBriefDraft(factory, seat, text, expectedVersion);
        onDone(view.savedText);
      } catch (err) {
        if (!showIfStale(err, "save the draft")) setError(describeAndReport(SURFACE, "save the draft", err));
      } finally {
        setSaving(false);
      }
    },
    [factory, seat, text, view, saving, onDone, showIfStale],
  );

  // Publish: a changed text is saved as a new version first, and that version is published. A text that was not
  // changed is the version the editor started from, and that one is published as it is - if it cannot be, the
  // Gateway says why and the confirm shows it.
  const publish = useCallback(async () => {
    if (view === null) return;
    let version = base.version;
    if (text !== base.text) {
      let saved: FactoryBriefVersion;
      try {
        saved = await saveFactoryBriefDraft(factory, seat, text, base.version);
      } catch (err) {
        // A newer version exists: nothing is published, the confirm closes, and the editor shows the two texts.
        if (showIfStale(err, "save the draft before publishing it")) return;
        throw err;
      }
      // The text is saved now. If the publish below is refused, a second press publishes this version and does
      // not save the same text again.
      setBase({ version: saved.version, text });
      version = saved.version;
    }
    await publishFactoryBrief(factory, seat, version);
    onDone(view.publish.doneText);
  }, [factory, seat, text, base, view, onDone, showIfStale]);

  const takeTheirs = useCallback(() => {
    if (conflict === null) return;
    setText(conflict.current.text);
    setBase({ version: conflict.current.version, text: conflict.current.text });
    setConflict(null);
  }, [conflict]);

  // error-reported-by: describeAndReport above
  if (loadError !== null) {
    return (
      <>
        <ErrorBanner message={loadError} onRetry={() => setNonce((n) => n + 1)} />
        <div className="fa-inline-action">
          <Button variant="secondary" onClick={onClose}>
            Close
          </Button>
        </div>
      </>
    );
  }
  if (view === null) return <LoadingState />;

  return (
    <div className="fa-brief-editor" data-testid="fa-brief-editor">
      <h3 className="fa-seat-subtitle">{view.heading}</h3>
      <p data-testid="fa-brief-start">{view.startText}</p>
      {view.waitingText !== null && (
        <div className="fa-warn" role="status" data-testid="fa-brief-waiting">
          {view.waitingText}
        </div>
      )}

      {conflict !== null && (
        <div className="fa-memory-conflict" data-testid="fa-brief-conflict">
          <p className="fa-memory-conflict-lead" role="alert">
            {conflict.lead}
          </p>
          <p>{view.conflictText}</p>
        </div>
      )}

      <div className={conflict === null ? "fa-brief-sides" : "fa-brief-sides two"}>
        <div className="fa-brief-side">
          {conflict !== null && <div className="fa-brief-side-label">{view.yoursLabel}</div>}
          <textarea
            className="fa-memory-edit dt-private"
            aria-label={view.textLabel}
            data-testid="fa-brief-text"
            value={text}
            onChange={(e) => setText(e.target.value)}
            disabled={saving}
            rows={18}
          />
        </div>
        {conflict !== null && (
          <div className="fa-brief-side">
            <div className="fa-brief-side-label">{view.theirsLabel}</div>
            <pre className="fa-memory-text fa-brief-theirs dt-private" data-testid="fa-brief-theirs">
              {conflict.current.text}
            </pre>
          </div>
        )}
      </div>
      <p className="fa-dim">{view.limitText}</p>

      {error !== null && (
        <p className="fa-memory-error" role="alert" data-testid="fa-brief-error">
          {error}
        </p>
      )}

      {conflict === null ? (
        <div className="fa-inline-action">
          <Button variant="secondary" data-testid="fa-brief-save" disabled={saving} onClick={() => void save(base.version)}>
            {saving ? view.saveBusyLabel : view.saveLabel}
          </Button>
          <Button variant="primary" data-testid="fa-brief-publish" disabled={saving} onClick={() => setConfirming(true)}>
            {view.publish.label}
          </Button>
          <Button variant="secondary" disabled={saving} onClick={onClose}>
            {view.cancelLabel}
          </Button>
        </div>
      ) : (
        <div className="fa-inline-action">
          <Button
            variant="primary"
            data-testid="fa-brief-save-over"
            disabled={saving}
            onClick={() => void save(conflict.current.version)}
          >
            {saving ? view.saveBusyLabel : view.saveOverLabel}
          </Button>
          <Button variant="secondary" data-testid="fa-brief-take-theirs" disabled={saving} onClick={takeTheirs}>
            {view.takeTheirsLabel}
          </Button>
          <Button variant="secondary" disabled={saving} onClick={onClose}>
            {view.cancelLabel}
          </Button>
        </div>
      )}

      <BriefConfirm action={confirming ? view.publish : null} what="publish this brief" onConfirm={publish} onClose={() => setConfirming(false)} />
    </div>
  );
}

/** The shared confirm, with the Gateway's own title, sentences and labels. A refusal stays in it, in the Gateway's words. */
function BriefConfirm({
  action,
  what,
  onConfirm,
  onClose,
}: {
  action: FactoryBriefAction | null;
  what: string;
  onConfirm: () => Promise<void>;
  onClose: () => void;
}) {
  return (
    <ConfirmDialog
      open={action !== null}
      title={action?.confirmTitle ?? ""}
      message={
        <div className="fa-confirm-lines" data-testid="fa-confirm-lines">
          {(action?.confirmLines ?? []).map((line, i) => (
            <p key={i}>{line}</p>
          ))}
        </div>
      }
      confirmLabel={action?.confirmLabel}
      busyLabel={action?.busyLabel}
      danger={false}
      action={what}
      onConfirm={onConfirm}
      onClose={onClose}
    />
  );
}

// The history: one page at a time, newest first. Each version says its state, who wrote it, when and its size, and
// carries the buttons the Gateway gave it - Open on every one, Publish on a draft that can be published, Bring back
// on a version older than the published one.
function BriefHistory({
  factory,
  seat,
  onClose,
  onChanged,
}: {
  factory: string;
  seat: string;
  onClose: () => void;
  onChanged: (text: string) => void;
}) {
  // The version this page starts below; null is the newest page.
  const [before, setBefore] = useState<number | null>(null);
  const [nonce, setNonce] = useState(0);
  const [view, setView] = useState<FactoryBriefHistoryView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [opening, setOpening] = useState<number | null>(null);
  const [opened, setOpened] = useState<FactoryBriefVersionView | null>(null);
  const [openError, setOpenError] = useState<string | null>(null);
  const [pending, setPending] = useState<{ kind: "publish" | "bring-back"; action: FactoryBriefAction } | null>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    setView(null);
    getFactoryBriefHistory(factory, seat, before, ctrl.signal).then(setView, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, "read the brief's history", err));
    });
    return () => ctrl.abort();
  }, [factory, seat, before, nonce]);

  const open = useCallback(
    async (version: number) => {
      setOpening(version);
      setOpenError(null);
      setOpened(null);
      try {
        setOpened(await getFactoryBriefVersion(factory, seat, version));
      } catch (err) {
        setOpenError(describeAndReport(SURFACE, "open this version", err));
      } finally {
        setOpening(null);
      }
    },
    [factory, seat],
  );

  const act = useCallback(async () => {
    if (pending === null) return;
    const { kind, action } = pending;
    if (kind === "publish") await publishFactoryBrief(factory, seat, action.version);
    else await bringBackFactoryBrief(factory, seat, action.version, action.expectedVersion);
    // Done: the page shows the Gateway's sentence and reloads, and the history starts again from its newest page.
    onChanged(action.doneText);
    setOpened(null);
    setBefore(null);
    setNonce((n) => n + 1);
  }, [factory, seat, pending, onChanged]);

  return (
    <div className="fa-brief-history" data-testid="fa-brief-history">
      {/* error-reported-by: describeAndReport above */}
      {error !== null && <ErrorBanner message={error} onRetry={() => setNonce((n) => n + 1)} />}
      {view === null && error === null && <LoadingState />}
      {view !== null && (
        <>
          <h3 className="fa-seat-subtitle">{view.heading}</h3>
          <p data-testid="fa-brief-history-summary">
            {view.summaryText}
            {view.pageText !== null && <> {view.pageText}</>}
          </p>
          {view.waitingText !== null && (
            <div className="fa-warn" role="status" data-testid="fa-brief-waiting">
              {view.waitingText}
            </div>
          )}
          {view.emptyText !== null && <p className="fa-dim">{view.emptyText}</p>}
          {view.rows.length > 0 && (
            <div className="fa-scroll">
              <table className="fa-table fa-brief-versions" data-testid="fa-brief-versions">
                <thead>
                  <tr>
                    {view.columns.map((c) => (
                      <th key={c}>{c}</th>
                    ))}
                    <th aria-hidden="true" />
                  </tr>
                </thead>
                <tbody>
                  {view.rows.map((row) => (
                    <tr key={row.version} data-testid={`fa-brief-version-${row.version}`}>
                      <td>{row.title}</td>
                      <td>
                        <ToneChip word={row.stateWord} tone={row.stateTone} />
                      </td>
                      <td className="mono dt-private">{row.whoText}</td>
                      <td>{row.whenText}</td>
                      <td>{row.sizeText}</td>
                      <td>
                        <div className="fa-inline-action">
                          <Button
                            variant="secondary"
                            data-testid={`fa-brief-open-${row.version}`}
                            disabled={opening !== null}
                            onClick={() => void open(row.version)}
                          >
                            {row.openLabel}
                          </Button>
                          {row.publish !== null && (
                            <Button
                              variant="primary"
                              data-testid={`fa-brief-publish-${row.version}`}
                              onClick={() => setPending({ kind: "publish", action: row.publish! })}
                            >
                              {row.publish.label}
                            </Button>
                          )}
                          {row.bringBack !== null && (
                            <Button
                              variant="secondary"
                              data-testid={`fa-brief-back-${row.version}`}
                              onClick={() => setPending({ kind: "bring-back", action: row.bringBack! })}
                            >
                              {row.bringBack.label}
                            </Button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {opening !== null && <LoadingState />}
          {/* error-reported-by: describeAndReport above */}
          {openError !== null && <ErrorBanner message={openError} />}
          {opened !== null && (
            <div className="fa-brief-opened" data-testid="fa-brief-opened">
              <h3 className="fa-seat-subtitle">{opened.title}</h3>
              <p className="fa-dim">{opened.lineText}</p>
              <pre className="fa-memory-text dt-private">{opened.text}</pre>
              <div className="fa-inline-action">
                <Button variant="secondary" onClick={() => setOpened(null)}>
                  {opened.closeLabel}
                </Button>
              </div>
            </div>
          )}

          <div className="fa-inline-action">
            {view.newestLabel !== null && (
              <Button variant="secondary" data-testid="fa-brief-newest" onClick={() => setBefore(null)}>
                {view.newestLabel}
              </Button>
            )}
            {view.olderLabel !== null && view.nextBefore !== null && (
              <Button variant="secondary" data-testid="fa-brief-older" onClick={() => setBefore(view.nextBefore)}>
                {view.olderLabel}
              </Button>
            )}
            <Button variant="secondary" data-testid="fa-brief-history-close" onClick={onClose}>
              {view.closeLabel}
            </Button>
          </div>
        </>
      )}
      {view === null && error !== null && (
        <div className="fa-inline-action">
          <Button variant="secondary" onClick={onClose}>
            Close
          </Button>
        </div>
      )}

      <BriefConfirm
        action={pending?.action ?? null}
        what={pending?.kind === "bring-back" ? "bring this version back" : "publish this version"}
        onConfirm={act}
        onClose={() => setPending(null)}
      />
    </div>
  );
}
