import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { GatewayError, gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  changeMemberRole,
  getTeamPage,
  removeMember,
  type TeamPage,
  type TeamPageInvitation,
  type TeamPageMember,
} from "@devthrottle/client-core/teams/teamPageClient";
import { cancelInvitation, resendInvitation } from "@devthrottle/client-core/teams/invitationsClient";
import { Button, ConfirmDialog, ErrorBanner, LoadingState, PageHeader } from "../components";
import { TeamBillingSection } from "./TeamBillingSection";
import "./team.css";

// Screen S1, the Team page (devthrottle_internal#2303): the members with their roles and seats, the waiting
// invitations with the day each expires, and invite, resend, cancel, change role and remove. Below them, for the Owner
// and a Manager, the Billing section (Teams v1, the team bill without Stripe).
//
// CRITICAL RULE 7 - every verdict on this page is the Gateway's (GET /teams/{teamId}/page): whether a role is a
// dropdown and what it offers, whether a row offers Remove and what its confirmation says, the seat each person takes,
// the counting line, whether "Invite someone" shows, and - for a Collaborator, who has no Team page - the sentence
// saying so. The page lays them out; it never reads a role to decide what the caller may do.
//
// The team comes from the address until the Cockpit's team switcher (devthrottle_internal#2312) is on main.

function day(utc: string): string {
  return new Date(utc).toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

export function TeamPageView() {
  const { teamId = "" } = useParams<{ teamId: string }>();
  const [page, setPage] = useState<TeamPage | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [refused, setRefused] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [note, setNote] = useState<{ text: string; ok: boolean } | null>(null);
  const [confirmRemove, setConfirmRemove] = useState<TeamPageMember | null>(null);
  const [confirmCancel, setConfirmCancel] = useState<TeamPageInvitation | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoadError(null);
    try {
      setPage(await getTeamPage(teamId, signal));
    } catch (err) {
      if (signal?.aborted) return;
      // A 403 is the Gateway saying this role has no Team page; it is an answer, not a failure to retry.
      if (err instanceof GatewayError && err.status === 403) setRefused(gatewayErrorMessage(err, "load the Team page"));
      else setLoadError(gatewayErrorMessage(err, "load the Team page"));
    }
  }, [teamId]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const changeRole = async (member: TeamPageMember, role: string) => {
    if (role === member.role) return;
    setBusy(member.memberId);
    setNote(null);
    try {
      await changeMemberRole(teamId, member.memberId, role);
      setNote({ text: `${member.name} is now a ${role}.`, ok: true });
      await load();
    } catch (err) {
      setNote({ text: gatewayErrorMessage(err, "change the role"), ok: false });
    } finally {
      setBusy(null);
    }
  };

  // Removing cannot be undone from here, so it is confirmed first; a failure throws into the dialog, which stays open
  // and shows it.
  const remove = async (member: TeamPageMember) => {
    await removeMember(teamId, member.memberId);
    setNote({ text: `${member.name} has been removed from the team.`, ok: true });
    await load();
  };

  const resend = async (invitation: TeamPageInvitation) => {
    setBusy(invitation.id);
    setNote(null);
    try {
      const result = await resendInvitation(teamId, invitation.id);
      setNote(result.email.sent
        ? { text: `Sent again to ${invitation.email}. It now expires on ${day(result.invitation.expiresAtUtc)}.`, ok: true }
        : { text: result.email.message, ok: false });
      await load();
    } catch (err) {
      setNote({ text: gatewayErrorMessage(err, "resend the invitation"), ok: false });
    } finally {
      setBusy(null);
    }
  };

  const cancel = async (invitation: TeamPageInvitation) => {
    await cancelInvitation(teamId, invitation.id);
    setNote({ text: `The invitation to ${invitation.email} is cancelled. Its link no longer works.`, ok: true });
    await load();
  };

  if (refused !== null) {
    return (
      <div className="team-page">
        <PageHeader title="Team" />
        <p className="team-card team-blocked" role="note">{refused}</p>
      </div>
    );
  }
  if (loadError !== null) {
    return (
      <div className="team-page">
        <PageHeader title="Team" />
        <ErrorBanner message={loadError} onRetry={() => void load()} />
      </div>
    );
  }
  if (page === null) {
    return (
      <div className="team-page">
        <PageHeader title="Team" />
        <LoadingState message="Loading the team..." />
      </div>
    );
  }

  return (
    <div className="team-page team-page-wide">
      <PageHeader
        title={`Team ${page.teamName}`}
        subtitle={page.summary}
        actions={page.canInvite ? (
          <Link className="ui-btn ui-btn-primary team-button-link" to={`/team/${encodeURIComponent(teamId)}/invite`}>Invite someone</Link>
        ) : undefined}
      />

      {note !== null && <p className={note.ok ? "team-ok team-note" : "team-warn team-note"} role="status">{note.text}</p>}

      <section className="team-card" aria-label="Members">
        <table className="team-table">
          <thead>
            <tr><th>Person</th><th>Role</th><th>Seat</th><th></th></tr>
          </thead>
          <tbody>
            {page.members.map((m) => (
              <tr key={m.memberId}>
                <td>
                  <span className="team-person">{m.name}{m.isYou && <span className="team-you"> (you)</span>}</span>
                  {m.email !== null && m.email !== m.name && <span className="team-person-email">{m.email}</span>}
                </td>
                <td>
                  {m.canChangeRole ? (
                    <select
                      className="team-select"
                      aria-label={`Role of ${m.name}`}
                      value={m.role}
                      disabled={busy !== null}
                      onChange={(e) => void changeRole(m, e.target.value)}
                    >
                      {m.roleChoices.map((r) => <option key={r} value={r}>{r}</option>)}
                    </select>
                  ) : (
                    m.role
                  )}
                </td>
                <td><span className="team-seat">{m.seat}</span></td>
                <td className="team-actions">
                  {m.canRemove && (
                    <Button variant="ghost" disabled={busy !== null} onClick={() => setConfirmRemove(m)}>Remove</Button>
                  )}
                </td>
              </tr>
            ))}
            {page.invitations.map((i) => (
              <tr key={i.id} className="team-invited-row">
                <td>
                  <span className="team-person">{i.email}</span>
                  <span className="team-person-email">Invited by {i.invitedBy}, {day(i.sentAtUtc)}</span>
                </td>
                <td>{i.role}</td>
                <td>
                  <span className="team-seat">{i.seat}</span>{" "}
                  {i.state === "expired"
                    ? <span className="team-pill-late">Expired {day(i.expiresAtUtc)}</span>
                    : <span className="team-pill">Expires {day(i.expiresAtUtc)}</span>}
                </td>
                <td className="team-actions">
                  {i.canResend && <Button variant="ghost" disabled={busy !== null} onClick={() => void resend(i)}>Resend</Button>}
                  {i.canCancel && <Button variant="ghost" disabled={busy !== null} onClick={() => setConfirmCancel(i)}>Cancel</Button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      {page.bill !== null && (
        <TeamBillingSection
          teamId={teamId}
          bill={page.bill}
          onChanged={async (text) => {
            setNote({ text, ok: true });
            await load();
          }}
        />
      )}

      <ConfirmDialog
        open={confirmRemove !== null}
        title={confirmRemove === null ? "" : `Remove ${confirmRemove.name}?`}
        message={confirmRemove?.removeWarning ?? ""}
        confirmLabel="Remove"
        cancelLabel="Keep them"
        danger
        action="remove the member"
        onConfirm={() => (confirmRemove === null ? undefined : remove(confirmRemove))}
        onClose={() => setConfirmRemove(null)}
      />
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
