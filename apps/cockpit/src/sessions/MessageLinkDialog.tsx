import { useCallback, useEffect, useMemo, useState } from "react";
import { GatewayError, type SessionDto } from "@devthrottle/client-core/api/client";
import { useSharedRoster } from "@devthrottle/client-core/fleet/rosterStore";
import {
  listMessageLinks,
  MESSAGE_LINK_AMOUNTS,
  removeMessageLink,
  setUpMessageLink,
  type MessageLink,
  type MessageLinkAmount,
} from "@devthrottle/client-core/fleet/messageLinksClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { useDismissOnBackdrop } from "../components";

const SURFACE = "cockpit-message-link";

// "Let this session talk to..." (issue #3548). A session may message only its own owner and the sessions it
// started; the owner can let it talk to any other session as well - once, once with a reply, or as much as the
// two need. The owner sets up the link and the sessions decide what to send: nothing here approves a message.
// Every sentence after a click is the Gateway's - the link's summary, or the reason it was refused.

export interface MessageLinkDialogProps {
  session: SessionDto;
  onClose: () => void;
}

/** What the owner reads after a failure. A refusal is shown in the Gateway's own words - why THIS link cannot be set
 *  up - never the generic sentence for its status code, which for a 409 would say "reload and try again" about a
 *  pair that will be refused again. A lost request or a server fault has no such sentence and is reported. */
function failureText(action: string, err: unknown): string {
  if (err instanceof GatewayError && err.serverReason) return err.serverReason;
  return describeAndReport(SURFACE, action, err);
}

/** The name the owner knows a session by, falling back to the start of its id. */
export function sessionLabel(s: Pick<SessionDto, "sessionId" | "name">): string {
  const name = (s.name ?? "").trim();
  const id = (s.sessionId ?? "").slice(0, 8);
  return name.length > 0 ? `${name} (${id})` : id;
}

/** The sessions this one could be linked to: every other session the roster knows, by name. */
export function linkCandidates(sessions: SessionDto[] | null, sessionId: string): SessionDto[] {
  return (sessions ?? [])
    .filter((s) => (s.sessionId ?? "").length > 0 && s.sessionId !== sessionId)
    .sort((a, b) => sessionLabel(a).localeCompare(sessionLabel(b)));
}

export function MessageLinkDialog({ session, onClose }: MessageLinkDialogProps) {
  const sid = session.sessionId ?? "";
  const { sessions } = useSharedRoster();
  const candidates = useMemo(() => linkCandidates(sessions, sid), [sessions, sid]);
  const [recipient, setRecipient] = useState("");
  const [amount, setAmount] = useState<MessageLinkAmount>("once-with-reply");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [links, setLinks] = useState<MessageLink[] | null>(null);
  const dismiss = useDismissOnBackdrop(onClose);

  const loadLinks = useCallback(async () => {
    try {
      const list = await listMessageLinks();
      setLinks(list.links.filter((l) => l.status === "live" && (l.senderSessionId === sid || l.recipientSessionId === sid)));
    } catch (err) {
      setError(failureText("load the message links", err));
    }
  }, [sid]);

  useEffect(() => {
    void loadLinks();
  }, [loadLinks]);

  const nameOf = useCallback(
    (id: string) => {
      const known = (sessions ?? []).find((s) => s.sessionId === id);
      return known ? sessionLabel(known) : id.slice(0, 8);
    },
    [sessions],
  );

  const doSetUp = async () => {
    if (recipient.length === 0) return;
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      const result = await setUpMessageLink(sid, recipient, amount);
      setDone(result.link.summary);
      setRecipient("");
      await loadLinks();
    } catch (err) {
      setError(failureText("set up the message link", err));
    } finally {
      setBusy(false);
    }
  };

  const doRemove = async (link: MessageLink) => {
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      await removeMessageLink(link.linkId);
      setDone("Link removed.");
      await loadLinks();
    } catch (err) {
      setError(failureText("remove the message link", err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="session-dialog-overlay" {...dismiss}>
      <div className="session-dialog message-link-dialog" role="dialog" aria-modal="true" aria-label="Let this session talk to another">
        <h3 className="session-dialog-title">Let this session talk to...</h3>
        <div className="session-dialog-text">
          {sessionLabel(session)} may already message the session that started it and the sessions it started. Pick
          another session it may talk to, and how much. The sessions decide what to send.
        </div>

        <label className="message-link-field">
          <span>Session</span>
          <select
            className="session-dialog-input"
            value={recipient}
            onChange={(e) => setRecipient(e.target.value)}
            disabled={busy}
          >
            <option value="">Choose a session</option>
            {candidates.map((s) => (
              <option key={s.sessionId} value={s.sessionId ?? ""}>
                {sessionLabel(s)}
              </option>
            ))}
          </select>
        </label>

        <fieldset className="message-link-amounts" disabled={busy}>
          <legend>How much</legend>
          {MESSAGE_LINK_AMOUNTS.map((a) => (
            <label key={a.amount} className="message-link-amount">
              <input
                type="radio"
                name="message-link-amount"
                value={a.amount}
                checked={amount === a.amount}
                onChange={() => setAmount(a.amount)}
              />
              <span>
                <strong>{a.label}</strong> - {a.detail}
              </span>
            </label>
          ))}
        </fieldset>

        {error !== null && <div className="session-dialog-error">{error}</div>}
        {done !== null && error === null && (
          <div className="session-dialog-text" role="status">
            {done}
          </div>
        )}

        <div className="message-link-current">
          <div className="session-menu-head">This session's links</div>
          {links === null && <div className="session-dialog-text">Loading...</div>}
          {links !== null && links.length === 0 && <div className="session-dialog-text">None.</div>}
          {links !== null &&
            links.map((l) => (
              <div key={l.linkId} className="message-link-row">
                <span>
                  {l.summary || `${nameOf(l.senderSessionId)} to ${nameOf(l.recipientSessionId)}`}
                </span>
                <button type="button" className="session-dialog-btn" onClick={() => void doRemove(l)} disabled={busy}>
                  Remove
                </button>
              </div>
            ))}
        </div>

        <div className="session-dialog-actions">
          <button type="button" className="session-dialog-btn" onClick={onClose} disabled={busy}>
            Close
          </button>
          <button
            type="button"
            className="session-dialog-btn primary"
            onClick={() => void doSetUp()}
            disabled={busy || recipient.length === 0}
          >
            {busy ? "Working..." : "Set up link"}
          </button>
        </div>
      </div>
    </div>
  );
}
