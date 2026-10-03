import { useCallback, useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  cancelInvitation,
  getInviteOptions,
  listInvitations,
  resendInvitation,
  sendInvitation,
  type InvitationSent,
  type InviteOptions,
  type TeamInvitation,
} from "@devthrottle/client-core/teams/invitationsClient";
import { Button, ConfirmDialog, ErrorBanner, LoadingState, PageHeader } from "../components";
import "./team.css";

// Screen S2, "Invite someone" (devthrottle_internal#2301), with the team's waiting invitations beneath it so resending
// is the one action the owner decided it should be.
//
// CRITICAL RULE 7 - which roles may be chosen, the sentence beside each and whether the form can be sent at all are
// the Gateway's answers (GET /teams/{teamId}/invitations/options). This page lays them out; it never decides who may
// invite whom. Any email domain is accepted - the Gateway is the one that says whether an address is an address.

function day(utc: string): string {
  return new Date(utc).toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

export function InviteView() {
  const { teamId = "" } = useParams<{ teamId: string }>();
  const [options, setOptions] = useState<InviteOptions | null>(null);
  const [invitations, setInvitations] = useState<TeamInvitation[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<string | null>(null);
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [sent, setSent] = useState<InvitationSent | null>(null);
  const [rowBusy, setRowBusy] = useState<string | null>(null);
  const [rowNote, setRowNote] = useState<string | null>(null);
  const [confirmCancel, setConfirmCancel] = useState<TeamInvitation | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoadError(null);
    try {
      const [opts, list] = await Promise.all([getInviteOptions(teamId, signal), listInvitations(teamId, signal)]);
      setOptions(opts);
      setInvitations(list);
      // The first role the caller may choose is selected, the way the mockup shows Developer selected for a Manager.
      setRole((current) => current ?? opts.roles.find((r) => r.allowed && r.role === "Developer")?.role
        ?? opts.roles.find((r) => r.allowed)?.role ?? null);
    } catch (err) {
      if (signal?.aborted) return;
      setLoadError(gatewayErrorMessage(err, "load the invite form"));
    }
  }, [teamId]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const send = async () => {
    if (role === null) return;
    setSending(true);
    setSendError(null);
    setSent(null);
    try {
      const result = await sendInvitation(teamId, email.trim(), role);
      setSent(result);
      setEmail("");
      setInvitations(await listInvitations(teamId));
    } catch (err) {
      setSendError(gatewayErrorMessage(err, "send the invitation"));
    } finally {
      setSending(false);
    }
  };

  const resend = async (invitation: TeamInvitation) => {
    setRowBusy(invitation.id);
    setRowNote(null);
    try {
      const result = await resendInvitation(teamId, invitation.id);
      setRowNote(result.email.sent
        ? `Sent again to ${invitation.email}. It now expires on ${day(result.invitation.expiresAtUtc)}.`
        : result.email.message);
      setInvitations(await listInvitations(teamId));
    } catch (err) {
      setRowNote(gatewayErrorMessage(err, "resend the invitation"));
    } finally {
      setRowBusy(null);
    }
  };

  // Cancelling cannot be undone (its link stops working), so it is confirmed first; a failure throws into the dialog,
  // which stays open and shows it.
  const cancel = async (invitation: TeamInvitation) => {
    await cancelInvitation(teamId, invitation.id);
    setRowNote(`The invitation to ${invitation.email} is cancelled. Its link no longer works.`);
    setInvitations(await listInvitations(teamId));
  };

  if (loadError !== null) {
    return (
      <div className="team-page">
        <PageHeader title="Invite someone" />
        <ErrorBanner message={loadError} onRetry={() => void load()} />
      </div>
    );
  }
  if (options === null || invitations === null) {
    return (
      <div className="team-page">
        <PageHeader title="Invite someone" />
        <LoadingState message="Loading the invite form..." />
      </div>
    );
  }

  const waiting = invitations.filter((i) => i.state === "sent" || i.state === "expired");
  const canSend = options.blocked === null && role !== null && email.trim().length > 0 && !sending;

  return (
    <div className="team-page">
      <PageHeader title={`Invite someone to ${options.teamName}`} subtitle={`You are the team's ${options.yourRole}.`} />

      <section className="team-card" aria-label="Invite someone">
        {options.blocked !== null && <p className="team-blocked" role="note">{options.blocked}</p>}
        <label className="team-label" htmlFor="invite-email">Email</label>
        <input
          id="invite-email"
          className="team-input"
          type="email"
          autoComplete="off"
          placeholder="name@any-company.com"
          value={email}
          disabled={options.blocked !== null}
          onChange={(e) => setEmail(e.target.value)}
        />
        <div className="team-label" id="invite-role-label">Role</div>
        <div role="radiogroup" aria-labelledby="invite-role-label">
          {options.roles.map((option) => (
            <label key={option.role} className={`team-option${option.allowed ? "" : " team-option-off"}${role === option.role ? " team-option-on" : ""}`}>
              <input
                type="radio"
                name="invite-role"
                value={option.role}
                checked={role === option.role}
                disabled={!option.allowed || options.blocked !== null}
                onChange={() => setRole(option.role)}
              />
              <span>
                <span className="team-option-title">{option.role}</span>
                <span className="team-option-hint">{option.hint}</span>
              </span>
            </label>
          ))}
        </div>
        <div className="team-row">
          <Button variant="primary" disabled={!canSend} onClick={() => void send()}>
            {sending ? "Sending..." : "Send invitation"}
          </Button>
          <span className="team-hint">{options.expiryNote}</span>
        </div>
        {sendError !== null && <ErrorBanner message={sendError} />}
        {sent !== null && (
          <p className={sent.email.sent ? "team-ok" : "team-warn"} role="status">
            {sent.email.sent ? `Invitation sent to ${sent.invitation.email} as a ${sent.invitation.role}. ` : ""}
            {sent.email.message}
          </p>
        )}
      </section>

      <section className="team-card" aria-label="Waiting invitations">
        <h2 className="team-h2">Waiting invitations</h2>
        {rowNote !== null && <p className="team-hint" role="status">{rowNote}</p>}
        {waiting.length === 0 ? (
          <p className="team-hint">No invitations are waiting.</p>
        ) : (
          <table className="team-table">
            <thead>
              <tr><th>Email</th><th>Role</th><th>Invited by</th><th>Expires</th><th></th></tr>
            </thead>
            <tbody>
              {waiting.map((i) => (
                <tr key={i.id}>
                  <td>{i.email}</td>
                  <td>{i.role}</td>
                  <td>{i.invitedBy}, {day(i.sentAtUtc)}</td>
                  <td>{i.state === "expired" ? <span className="team-pill-late">Expired {day(i.expiresAtUtc)}</span> : <span className="team-pill">Expires {day(i.expiresAtUtc)}</span>}</td>
                  <td className="team-actions">
                    <Button variant="ghost" disabled={rowBusy !== null} onClick={() => void resend(i)}>Resend</Button>
                    {i.state === "sent" && (
                      <Button variant="ghost" disabled={rowBusy !== null} onClick={() => setConfirmCancel(i)}>Cancel</Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <ConfirmDialog
        open={confirmCancel !== null}
        title="Cancel this invitation?"
        message={confirmCancel === null ? "" : `The link sent to ${confirmCancel.email} will stop working. To invite them later, send a new invitation.`}
        confirmLabel="Cancel invitation"
        cancelLabel="Keep it"
        action="cancel the invitation"
        onConfirm={() => (confirmCancel === null ? undefined : cancel(confirmCancel))}
        onClose={() => setConfirmCancel(null)}
      />
    </div>
  );
}
