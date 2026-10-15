import { useCallback, useEffect, useState } from "react";
import {
  downloadFactoryAttachment,
  getFactoryRecordsDocument,
  getFactoryRecordsDocumentHistory,
  getFactoryRecordsJournal,
  getFactoryRecordsTab,
  type FactoryRecordsAttachmentRow,
  type FactoryRecordsDocumentHistoryView,
  type FactoryRecordsDocumentView,
  type FactoryRecordsJournalView,
  type FactoryRecordsSection,
  type FactoryRecordsTabView,
} from "@devthrottle/client-core/factory/factoryRecordsClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, EmptyState, ErrorBanner, LoadingState } from "../components";
import { ToneChip } from "./FactoryParts";

const SURFACE = "cockpit-factory-records";

// The factory page's Records tab (Factory Definition mission, phase 6, sections 5.6 and 5.7), beside Memory: what the
// factory produced, held on the Gateway - each seat's journal, the working documents with every version, and the
// attachments.
//
// Rule 7: every word is the Gateway's (FactoryRecordsTabFold). This file lays the fields out and opens what a button
// names. The tab only reads; the factory's sessions write records through the command line.

/** What the tab is showing: the three sections, or one journal, one document version, or one document's history. */
type Open =
  | { kind: "tab" }
  | { kind: "journal"; seat: string | null }
  | { kind: "document"; name: string; version: number | null }
  | { kind: "history"; name: string; before: number | null };

export interface FactoryRecordsTabProps {
  factory: string;
}

export function FactoryRecordsTab({ factory }: FactoryRecordsTabProps) {
  const [open, setOpen] = useState<Open>({ kind: "tab" });
  const back = useCallback(() => setOpen({ kind: "tab" }), []);

  return (
    <section className="fa-panel fa-rec" data-testid="fa-records">
      {open.kind === "tab" && <RecordsSections factory={factory} onOpen={setOpen} />}
      {open.kind === "journal" && <JournalPanel factory={factory} seat={open.seat} onBack={back} />}
      {open.kind === "document" && (
        <DocumentPanel factory={factory} name={open.name} version={open.version} onOpen={setOpen} onBack={back} />
      )}
      {open.kind === "history" && (
        <HistoryPanel factory={factory} name={open.name} before={open.before} onOpen={setOpen} onBack={back} />
      )}
    </section>
  );
}

/** One read of the Gateway: its answer, its failure in the reported sentence, and a way to try again. */
function useRead<T>(read: (signal: AbortSignal) => Promise<T>, action: string, key: string) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  useEffect(() => {
    const ctrl = new AbortController();
    setData(null);
    setError(null);
    read(ctrl.signal).then(setData, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, action, err));
    });
    return () => ctrl.abort();
    // `key` names everything the read depends on; the read itself is a new closure on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, nonce]);
  return { data, error, retry: () => setNonce((n) => n + 1) };
}

function SectionHead({ section }: { section: FactoryRecordsSection }) {
  return (
    <div className="fa-rec-section-head">
      <h3 className="fa-section-title">{section.heading}</h3>
      {section.emptyText === null && <span className="fa-dim">{section.summaryText}</span>}
    </div>
  );
}

function RecordsSections({ factory, onOpen }: { factory: string; onOpen: (open: Open) => void }) {
  const { data: view, error, retry } = useRead<FactoryRecordsTabView>(
    (signal) => getFactoryRecordsTab(factory, signal),
    "read this factory's records",
    factory,
  );
  if (error !== null) return <ErrorBanner message={error} onRetry={retry} />;
  if (view === null) return <LoadingState />;

  return (
    <>
      <div className="fa-rec-head">
        <h2 className="fa-section-title">{view.heading}</h2>
        <p className="fa-dim">{view.introText}</p>
      </div>

      <div className="fa-rec-section" data-testid="fa-rec-journals">
        <SectionHead section={view.journals} />
        {view.journals.emptyText !== null ? (
          <p className="fa-rec-empty">{view.journals.emptyText}</p>
        ) : (
          <div className="fa-rec-cards">
            {view.journalGroups.map((g) => (
              <div key={g.seat ?? ""} className="fa-rec-card" data-testid={`fa-rec-journal-${g.seat ?? "factory"}`}>
                <strong>{g.title}</strong>
                <p className="fa-dim">{g.summaryText}</p>
                <Button onClick={() => onOpen({ kind: "journal", seat: g.seat })}>{g.openLabel}</Button>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="fa-rec-section" data-testid="fa-rec-documents">
        <SectionHead section={view.documents} />
        {view.documents.emptyText !== null ? (
          <p className="fa-rec-empty">{view.documents.emptyText}</p>
        ) : (
          <table className="fa-table fa-rec-table">
            <thead>
              <tr>
                {view.documentColumns.map((c) => (
                  <th key={c}>{c}</th>
                ))}
                <th />
              </tr>
            </thead>
            <tbody>
              {view.documentRows.map((d) => (
                <tr key={d.name} data-testid={`fa-rec-document-${d.name}`}>
                  <td className="mono">{d.name}</td>
                  <td>{d.versionText}</td>
                  <td>{d.whoText}</td>
                  <td>{d.whenText}</td>
                  <td>{d.sizeText}</td>
                  <td className="fa-rec-actions">
                    <Button onClick={() => onOpen({ kind: "document", name: d.name, version: null })}>{d.openLabel}</Button>
                    {d.historyLabel !== null && (
                      <Button onClick={() => onOpen({ kind: "history", name: d.name, before: null })}>{d.historyLabel}</Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="fa-rec-section" data-testid="fa-rec-attachments">
        <SectionHead section={view.attachments} />
        {view.attachments.emptyText !== null ? (
          <p className="fa-rec-empty">{view.attachments.emptyText}</p>
        ) : (
          <table className="fa-table fa-rec-table">
            <thead>
              <tr>
                {view.attachmentColumns.map((c) => (
                  <th key={c}>{c}</th>
                ))}
                <th />
              </tr>
            </thead>
            <tbody>
              {view.attachmentRows.map((a) => (
                <tr key={a.id} data-testid={`fa-rec-attachment-${a.name}`}>
                  <td className="mono">{a.name}</td>
                  <td>{a.seatText}</td>
                  <td className="mono fa-rec-type">{a.contentType}</td>
                  <td>{a.sizeText}</td>
                  <td>{a.whoText}</td>
                  <td>{a.whenText}</td>
                  <td className="fa-rec-actions">
                    <DownloadButton attachment={a} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}

function DownloadButton({ attachment }: { attachment: FactoryRecordsAttachmentRow }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <>
      <Button
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError(null);
          try {
            await downloadFactoryAttachment(attachment.downloadHref);
          } catch (err) {
            setError(describeAndReport(SURFACE, "download this attachment", err));
          } finally {
            setBusy(false);
          }
        }}
      >
        {attachment.downloadLabel}
      </Button>
      {error !== null && (
        <span className="fa-rec-error" role="alert">
          {error}
        </span>
      )}
    </>
  );
}

function JournalPanel({ factory, seat, onBack }: { factory: string; seat: string | null; onBack: () => void }) {
  const { data: view, error, retry } = useRead<FactoryRecordsJournalView>(
    (signal) => getFactoryRecordsJournal(factory, seat, signal),
    "read this journal",
    `${factory}|${seat ?? ""}`,
  );
  if (error !== null) return <ErrorBanner message={error} onRetry={retry} />;
  if (view === null) return <LoadingState />;
  return (
    <div className="fa-rec-open" data-testid="fa-rec-journal">
      <div className="fa-rec-head">
        <h2 className="fa-section-title">{view.heading}</h2>
        <p className="fa-dim">{view.summaryText}</p>
        <Button onClick={onBack}>{view.closeLabel}</Button>
      </div>
      {view.emptyText !== null ? (
        <EmptyState message={view.emptyText} />
      ) : (
        <ol className="fa-rec-entries">
          {view.entries.map((e) => (
            <li key={e.id} className="fa-rec-entry" data-testid={`fa-rec-entry-${e.id}`}>
              <div className="fa-rec-entry-head">
                <strong>{e.dateText}</strong>
                <span className="fa-dim">{e.lineText}</span>
              </div>
              <pre className="fa-rec-text dt-private">{e.text}</pre>
              {e.attachments.length > 0 && (
                <ul className="fa-rec-entry-files">
                  {e.attachments.map((a) => (
                    <li key={a.id}>
                      <span className="mono">{a.name}</span> <span className="fa-dim">{a.sizeText}</span>{" "}
                      <DownloadButton attachment={a} />
                    </li>
                  ))}
                </ul>
              )}
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

function DocumentPanel({
  factory,
  name,
  version,
  onOpen,
  onBack,
}: {
  factory: string;
  name: string;
  version: number | null;
  onOpen: (open: Open) => void;
  onBack: () => void;
}) {
  const { data: view, error, retry } = useRead<FactoryRecordsDocumentView>(
    (signal) => getFactoryRecordsDocument(factory, name, version, signal),
    "read this document",
    `${factory}|${name}|${version ?? ""}`,
  );
  if (error !== null) return <ErrorBanner message={error} onRetry={retry} />;
  if (view === null) return <LoadingState />;
  return (
    <div className="fa-rec-open" data-testid="fa-rec-document">
      <div className="fa-rec-head">
        <h2 className="fa-section-title">{view.title}</h2>
        <p className="fa-dim">{view.lineText}</p>
        <div className="fa-rec-actions">
          {view.historyLabel !== null && (
            <Button onClick={() => onOpen({ kind: "history", name: view.name, before: null })}>{view.historyLabel}</Button>
          )}
          <Button onClick={onBack}>{view.closeLabel}</Button>
        </div>
      </div>
      <pre className="fa-rec-text dt-private">{view.text}</pre>
    </div>
  );
}

function HistoryPanel({
  factory,
  name,
  before,
  onOpen,
  onBack,
}: {
  factory: string;
  name: string;
  before: number | null;
  onOpen: (open: Open) => void;
  onBack: () => void;
}) {
  const { data: view, error, retry } = useRead<FactoryRecordsDocumentHistoryView>(
    (signal) => getFactoryRecordsDocumentHistory(factory, name, before, signal),
    "read this document's versions",
    `${factory}|${name}|${before ?? ""}`,
  );
  if (error !== null) return <ErrorBanner message={error} onRetry={retry} />;
  if (view === null) return <LoadingState />;
  return (
    <div className="fa-rec-open" data-testid="fa-rec-history">
      <div className="fa-rec-head">
        <h2 className="fa-section-title">{view.heading}</h2>
        <p className="fa-dim">{view.summaryText}</p>
        <Button onClick={onBack}>{view.closeLabel}</Button>
      </div>
      <table className="fa-table fa-rec-table">
        <thead>
          <tr>
            {view.columns.map((c) => (
              <th key={c}>{c}</th>
            ))}
            <th />
          </tr>
        </thead>
        <tbody>
          {view.rows.map((r) => (
            <tr key={r.version} data-testid={`fa-rec-version-${r.version}`}>
              <td>{r.title}</td>
              <td>
                <ToneChip word={r.stateWord} tone={r.stateTone} />
              </td>
              <td>{r.whoText}</td>
              <td>{r.whenText}</td>
              <td>{r.sizeText}</td>
              <td className="fa-rec-actions">
                <Button onClick={() => onOpen({ kind: "document", name: view.name, version: r.version })}>{r.openLabel}</Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="fa-rec-actions">
        {view.newestLabel !== null && (
          <Button onClick={() => onOpen({ kind: "history", name: view.name, before: null })}>{view.newestLabel}</Button>
        )}
        {view.olderLabel !== null && view.nextBefore !== null && (
          <Button onClick={() => onOpen({ kind: "history", name: view.name, before: view.nextBefore })}>{view.olderLabel}</Button>
        )}
      </div>
    </div>
  );
}
