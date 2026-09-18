import type { DevReportRecordedItem, DevReportReply } from "./devReportsClient";
import type { DevReportAnchor, DevReportItem, DevReportNoteMode, DevReportQueuedItem, DevReportNoteModeRequest } from "./protocol";
import "./devReports.css";

// The conversation beside (Cockpit) or under (phone) a report: how a note is started, what is queued on this
// device and not sent, what the Gateway has - with its status words verbatim - and the agent's replies. The
// shell decides where it sits; this decides nothing about what a status means.
//
// THE NOTE CONTROLS ARE HERE, NOT IN THE REPORT (issue #3077). They used to be a pill floating in the corner
// of the report itself, on top of the words, carrying a queued count this panel was already showing. The
// report is a document to read; the app is where you act on it. Pressing Add a note asks the page - over the
// private port the two already share - to wait for a click, and the note box still opens against whatever is
// clicked, because that is the one part of note-taking that can only happen inside the report.

export interface DevReportConversationModel {
  /** Queued on this device, from the page's state the host holds. */
  queued: DevReportQueuedItem[];
  /** The Gateway's record of what was sent, with its status and statusLabel. */
  sent: DevReportRecordedItem[];
  /** The Gateway's record of the agent's replies. */
  replies: DevReportReply[];
  /** A send request is in flight. */
  sending: boolean;
  /** Why the last send request failed, or null. */
  sendError: string | null;
  /** Sends everything still queued. */
  send: () => void;
  /** Where note-taking stands in the page: whether it is picking, and what the reader has selected. */
  noteMode: DevReportNoteMode;
  /** Ask the page to arm a note, open one on the selection, or cancel. */
  setNoteMode: (mode: DevReportNoteModeRequest) => void;
  /** The page is connected over its port; without one, nothing here can reach the report. */
  connected: boolean;
}

function where(anchor: DevReportAnchor): string {
  const labels = [anchor.rowLabel, anchor.columnLabel, anchor.label].filter((l): l is string => Boolean(l));
  return labels.length > 0 ? `${labels.join(", ")}: "${anchor.quote}"` : `"${anchor.quote}"`;
}

function ItemWords({ item }: { item: DevReportItem }) {
  if (item.kind === "answer") {
    return (
      <>
        <div className="dev-report-item-where">{item.question}</div>
        <div className="dev-report-item-text">
          {item.optionLabel}
          {item.comment ? ` - ${item.comment}` : ""}
        </div>
      </>
    );
  }
  return (
    <>
      <div className="dev-report-item-where">{where(item.anchor)}</div>
      <div className="dev-report-item-text">{item.text}</div>
    </>
  );
}

function formatTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

export function DevReportConversation({ conversation }: { conversation: DevReportConversationModel }) {
  const { queued, sent, replies, sending, sendError, send, noteMode, setNoteMode, connected } = conversation;
  const selection = noteMode.selectionQuote;
  return (
    <div className="dev-report-conversation" data-testid="dev-report-conversation">
      <section className="dev-report-notemode">
        <button
          type="button"
          className={noteMode.picking ? "dev-report-note-start picking" : "dev-report-note-start"}
          data-testid="dev-report-add-note"
          disabled={!connected}
          onClick={() => setNoteMode(noteMode.picking ? "off" : "pick")}
        >
          {noteMode.picking ? "Cancel" : "Add a note"}
        </button>
        {selection !== null && !noteMode.picking && (
          <button
            type="button"
            className="dev-report-note-selection"
            data-testid="dev-report-note-selection"
            onClick={() => setNoteMode("selection")}
          >
            Note on &quot;{selection.length > 30 ? `${selection.slice(0, 30)}...` : selection}&quot;
          </button>
        )}
        {noteMode.picking && (
          <div className="dev-report-note-hint" data-testid="dev-report-note-hint">
            Click the paragraph, table cell or diagram part your note is about.
          </div>
        )}
      </section>
      <section>
        <h3 className="dev-report-h">Queued - not sent yet</h3>
        {queued.length === 0 && <div className="dev-report-empty">Nothing queued. Add a note above, or answer a question in the report.</div>}
        <ul className="dev-report-items">
          {queued.map((item) => (
            <li key={item.id} className="dev-report-item" data-testid="dev-report-queued-item" data-item-id={item.id}>
              <ItemWords item={item} />
              {item.statusLabel && (
                <div className="dev-report-item-status" data-testid="dev-report-item-status">
                  {item.statusLabel}
                </div>
              )}
            </li>
          ))}
        </ul>
        <div className="dev-report-send-row">
          <button
            type="button"
            className="dev-report-send"
            data-testid="dev-report-send"
            data-sending={sending ? "true" : "false"}
            disabled={sending || queued.length === 0}
            onClick={send}
          >
            {sending ? "Sending..." : queued.length > 0 ? `Send ${queued.length}` : "Send"}
          </button>
          {sendError && (
            <div className="dev-report-error" role="alert" data-testid="dev-report-send-error">
              {sendError}
            </div>
          )}
        </div>
      </section>
      <section>
        <h3 className="dev-report-h">Sent</h3>
        {sent.length === 0 && <div className="dev-report-empty">Nothing sent yet.</div>}
        <ul className="dev-report-items">
          {sent.map((item) => (
            <li key={item.id} className="dev-report-item" data-testid="dev-report-sent-item" data-item-id={item.id}>
              <ItemWords item={item} />
              <div className="dev-report-item-status" data-testid="dev-report-item-status" data-status={item.status}>
                {item.statusLabel}
              </div>
            </li>
          ))}
        </ul>
      </section>
      <section>
        <h3 className="dev-report-h">Replies from the agent</h3>
        {replies.length === 0 && <div className="dev-report-empty">No replies yet.</div>}
        <ul className="dev-report-items">
          {replies.map((reply) => (
            <li key={reply.id} className="dev-report-item" data-testid="dev-report-reply" data-reply-id={reply.id}>
              <time className="dev-report-item-where" dateTime={reply.at}>
                {formatTime(reply.at)}
              </time>
              <div className="dev-report-item-text">{reply.text}</div>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
