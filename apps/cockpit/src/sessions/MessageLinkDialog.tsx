import { useCallback, useEffect, useMemo, useState } from "react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
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
// What a link allows and whether it is still live are the Gateway's words (its summary); which two sessions it
// joins is drawn here, from the roster, because the summary does not name them. A refusal reaches the owner in
// the Gateway's words through describeAndReport, which puts the server's reason first.

export interface MessageLinkDialogProps {
  session: SessionDto;
  onClose: () => void;
}

/** The name the owner knows a session by, falling back to the start of its id. */
export function sessionLabel(s: Pick<SessionDto, "sessionId" | "name">): string {
  const name = (s.name ?? "").trim();
  const id = (s.sessionId ?? "").slice(0, 8);
  return name.length > 0 ? `${name} (${id})` : id;
}

/** The sessions this one could be linked to: every other session the roster lists (it lists running ones). */
export function linkCandidates(sessions: SessionDto[] | null, sessionId: string): SessionDto[] {
  return (sessions ?? [])
    .filter((s) => (s.sessionId ?? "").length > 0 && s.sessionId !== sessionId)
    .sort((a, b) => sessionLabel(a).localeCompare(sessionLabel(b)));
}

/** One line for a link: which sessions it joins, in which direction, then the Gateway's summary. An ongoing link
 *  carries both ways, so it is "A and B"; a one-time link goes from its sender, so it is "A to B". */
export function linkLine(link: MessageLink, nameOf: (id: string) => string): string {
  const a = nameOf(link.senderSessionId);
  const b = nameOf(link.recipientSessionId);
  const who = link.amount === "ongoing" ? `${a} and ${b}` : `${a} to ${b}`;
  return `${who}: ${link.summary}`;
}

export function MessageLinkDialog({ session, onClose }: MessageLinkDialogProps) {
  const sid = session.sessionId ?? "";
  const { sessions } = useSharedRoster();
  const candidates = useMemo(() => linkCandidates(sessions, sid), [sessions, sid]);
  const [recipient, setRecipient] = useState("");
  const [amount, setAmount] = useState<MessageLinkAmount>("once-with-reply");
  const [busy, setBusy] = useState(false);
  // What the last click did, and why it failed - kept apart from the list's own load failure, so a reload that
  // fails after a set up that worked can never make the owner read the set up as failed and do it again.
  const [actionError, setActionError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [links, setLinks] = useState<MessageLink[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const dismiss = useDismissOnBackdrop(onClose);

  const nameOf = useCallback(
    (id: string) => {
      const known = (sessions ?? []).find((s) => s.sessionId === id);
      return known ? sessionLabel(known) : id.slice(0, 8);
    },
    [sessions],
  );

  const loadLinks = useCallback(async () => {
    try {
      const list = await listMessageLinks();
      setLinks(list.links.filter((l) => l.status === "live" && (l.senderSessionId === sid || l.recipientSessionId === sid)));
      setListError(null);
    } catch (err) {
      setListError(describeAndReport(SURFACE, "load the message links", err));
    }
  }, [sid]);

  useEffect(() => {
    void loadLinks();
  }, [loadLinks]);

  const doSetUp = async () => {
    if (recipient.length === 0) return;
    setBusy(true);
    setActionError(null);
    setDone(null);
    try {
      const result = await setUpMessageLink(sid, recipient, amount);
      setDone(`Set up. ${linkLine(result.link, nameOf)}`);
      setRecipient("");
    } catch (err) {
      setActionError(describeAndReport(SURFACE, "set up the message link", err));
    } finally {
      setBusy(false);
    }
    await loadLinks();
  };

  const doRemove = async (link: MessageLink) => {
    setBusy(true);
    setActionError(null);
    setDone(null);
    try {
      // The Gateway answers with the link as it now stands: "Removed." when this click removed it, or "Used."
      // when a message used it up a moment before. Its summary says which.
      const after = await removeMessageLink(link.linkId);
      setDone(linkLine(after, nameOf));
    } catch (err) {
      setActionError(describeAndReport(SURFACE, "remove the message link", err));
    } finally {
      setBusy(false);
    }
    await loadLinks();
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

        {actionError !== null && <div className="session-dialog-error">{actionError}</div>}
        {done !== null && (
          <div className="session-dialog-text" role="status">
            {done}
          </div>
        )}

        <div className="message-link-current">
          <div className="session-menu-head">This session's links</div>
          {listError !== null && <div className="session-dialog-error">{listError}</div>}
          {links === null && listError === null && <div className="session-dialog-text">Loading...</div>}
          {links !== null && links.length === 0 && <div className="session-dialog-text">None.</div>}
          {links !== null &&
            links.map((l) => (
              <div key={l.linkId} className="message-link-row">
                <span>{linkLine(l, nameOf)}</span>
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
