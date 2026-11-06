import { useCallback, useState } from "react";
import { Link } from "react-router-dom";
import { createSession } from "../api/client";
import { DictationDialog } from "../dictation/DictationDialog";
import { describeAndReport } from "../errors/reportClientError";
import {
  replyYourInbox,
  type YourInboxLink,
  type YourInboxMessage,
  type YourInboxReplyOffer,
} from "./yourInboxClient";
import { yourInboxChanged } from "./useYourInboxCount";
import { YOUR_INBOX_SURFACE } from "./YourInboxListView";
import "../factoryinbox/factoryInbox.css";
import "./yourInbox.css";

// THE REPLY BOX (Factory Inbox mission, step C): the same box as the Factory inbox - typed, or spoken through the same
// voice box a session uses, whose words come back into the box to be read before Send. The Gateway routes the reply
// (built in phase 3): into the factory's Factory inbox for a factory's message, into the session's own inbox while it
// runs. When the session has ended, the Gateway sends nothing and answers with its sentence and its offers - "Leave it in
// its factory", "Start a new session" - and this box shows exactly those, in the Gateway's words, and nothing else.
//
// Where the reply went is the Gateway's sentence too (the reply's label). This box prints; it routes nothing itself.

export interface YourInboxReplyComposerProps {
  message: YourInboxMessage;
  surface: "cockpit" | "mobile";
  /** The reply went: the message as the Gateway now holds it, with its replies. */
  onSent: (message: YourInboxMessage) => void;
  /** The shell's address for a link - here, the new session started from an offer. */
  hrefFor: (link: YourInboxLink) => string | null;
}

export function YourInboxReplyComposer({ message, surface, onSent, hrefFor }: YourInboxReplyComposerProps) {
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [dictating, setDictating] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [offered, setOffered] = useState<{ sentence: string; offers: YourInboxReplyOffer[] } | null>(null);
  const [started, setStarted] = useState<YourInboxLink | null>(null);

  const send = useCallback(
    async (to?: string) => {
      if (text.trim().length === 0) return;
      setSending(true);
      setStatus(null);
      setError(null);
      try {
        const outcome = await replyYourInbox(message.id, text, to);
        if (outcome.kind === "offers") {
          setOffered({ sentence: outcome.sentence, offers: outcome.offers });
          return;
        }
        setOffered(null);
        setText("");
        setStatus(outcome.reply.label);
        onSent(outcome.message);
        yourInboxChanged();
      } catch (err: unknown) {
        setError(describeAndReport(YOUR_INBOX_SURFACE, "send the reply", err));
      } finally {
        setSending(false);
      }
    },
    [message.id, text, onSent],
  );

  const take = useCallback(
    async (offer: YourInboxReplyOffer) => {
      if (offer.kind !== "new-session") {
        // Every other offer is a place the Gateway routes the same reply to: the same call, naming it.
        await send(offer.kind);
        return;
      }
      if (offer.directorId === null || offer.repoPath === null) {
        setError(describeAndReport(YOUR_INBOX_SURFACE, "start a new session with the reply", new Error("The offer named no computer or folder.")));
        return;
      }
      setSending(true);
      setError(null);
      try {
        const session = await createSession(offer.directorId, offer.repoPath, { prePrompt: offer.prompt ?? text });
        if (!session.sessionId) throw new Error("The created session had no identifier.");
        setOffered(null);
        setText("");
        setStarted({ kind: "session", label: offer.label, target: session.sessionId });
      } catch (err: unknown) {
        setError(describeAndReport(YOUR_INBOX_SURFACE, "start a new session with the reply", err));
      } finally {
        setSending(false);
      }
    },
    [send, text],
  );

  const startedHref = started === null ? null : hrefFor(started);

  return (
    <div className="fi-composer yi-reply" data-testid="yi-reply">
      <p className="yi-reply-to">{message.replyTo.label}</p>
      <textarea
        className="fi-composer-text"
        value={text}
        placeholder="Write your reply..."
        aria-label="Reply"
        rows={3}
        onChange={(e) => {
          setText(e.target.value);
          setOffered(null);
        }}
      />
      <div className="fi-composer-buttons">
        <button type="button" className="fi-mic" onClick={() => setDictating(true)}>
          Mic
        </button>
        <button
          type="button"
          className="fi-send"
          onClick={() => void send()}
          disabled={sending || text.trim().length === 0}
        >
          {sending ? "Sending..." : "Send"}
        </button>
      </div>
      {offered !== null && (
        // Not an alert: a session that has ended is not an error but a choice for the owner, so it is announced politely.
        <div className="yi-offers" data-testid="yi-offers" role="group" aria-live="polite" aria-label="Where the reply can go">
          <p className="yi-offers-sentence">{offered.sentence}</p>
          {offered.offers.map((o) => (
            <button
              key={o.kind}
              type="button"
              className="yi-offer"
              data-testid="yi-offer"
              disabled={sending}
              onClick={() => void take(o)}
            >
              {o.label}
            </button>
          ))}
        </div>
      )}
      {status !== null && (
        <p className="fi-composer-status" role="status" data-testid="yi-reply-status">
          {status}
        </p>
      )}
      {started !== null && (
        <p className="fi-composer-status" role="status" data-testid="yi-reply-started">
          {startedHref !== null ? <Link to={startedHref}>Open the new session</Link> : "The new session was started."}
        </p>
      )}
      {error !== null && (
        <div className="fi-composer-error" role="alert">
          {error}
        </div>
      )}
      {dictating && (
        <DictationDialog
          surface={surface}
          showInsert={false}
          onSend={(words) => setText((t) => joinWords(t, words))}
          onClose={() => setDictating(false)}
        />
      )}
    </div>
  );
}

function joinWords(a: string, b: string): string {
  const left = a.trim();
  const right = b.trim();
  if (left.length === 0) return right;
  if (right.length === 0) return left;
  return `${left} ${right}`;
}
