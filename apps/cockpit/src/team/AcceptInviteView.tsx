import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { rememberTeamOnThisBrowser } from "@devthrottle/client-core/teams/CurrentTeam";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  acceptInvitation,
  declineInvitation,
  openInvitation,
  type TeamInvitation,
} from "@devthrottle/client-core/teams/invitationsClient";
import { Button, ConfirmDialog, ErrorBanner, LoadingState } from "../components";
import "./team.css";

// Screen S3, "Accepting an invitation" (devthrottle_internal#2301). The link in the invitation email lands here:
// <gateway>/invite/{token}. A browser with no account, or not signed in, never reaches this component - the Cockpit's
// sign-in gate sends it to /signin?next=/invite/{token}, where a new person signs up, and the round trip comes back to
// this same address with the invitation intact (routes.tsx, RequireDeviceKey).
//
// CRITICAL RULE 7 - whether the invitation can be answered, and why not, are the Gateway's answers (canRespond,
// refusal). This page never decides that an invitation has expired.

type Answered = { kind: "accepted" | "declined"; invitation: TeamInvitation };

export function AcceptInviteView() {
  const { token = "" } = useParams<{ token: string }>();
  const [invitation, setInvitation] = useState<TeamInvitation | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [answered, setAnswered] = useState<Answered | null>(null);
  const [confirmDecline, setConfirmDecline] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    openInvitation(token, controller.signal)
      .then(setInvitation)
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setLoadError(gatewayErrorMessage(err, "open the invitation"));
      });
    return () => controller.abort();
  }, [token]);

  const accept = async () => {
    setBusy(true);
    setActionError(null);
    try {
      const joined = await acceptInvitation(token);
      // The person who just joined lands in that team: the next shell opens on it (devthrottle_internal#2306, review
      // finding F1) - for a Collaborator, on the three pages and nothing else.
      rememberTeamOnThisBrowser(joined.teamId);
      setAnswered({ kind: "accepted", invitation: joined });
    } catch (err) {
      setActionError(gatewayErrorMessage(err, "join the team"));
    } finally {
      setBusy(false);
    }
  };

  // Declining cannot be undone - the invitation can no longer be accepted - so it is confirmed first; a failure throws
  // into the dialog, which stays open and shows it.
  const decline = async () => {
    setAnswered({ kind: "declined", invitation: await declineInvitation(token) });
  };

  if (loadError !== null) {
    return (
      <div className="team-page team-accept">
        <h1 className="team-accept-title">This invitation cannot be opened</h1>
        <ErrorBanner message={loadError} />
      </div>
    );
  }
  if (invitation === null) {
    return (
      <div className="team-page team-accept">
        <LoadingState message="Opening the invitation..." />
      </div>
    );
  }

  if (answered !== null) {
    const i = answered.invitation;
    return (
      <div className="team-page team-accept">
        <h1 className="team-accept-title">
          {answered.kind === "accepted" ? `You joined the ${i.teamName} team` : `You declined the invitation to ${i.teamName}`}
        </h1>
        <p className="team-accept-sub">
          {answered.kind === "accepted"
            ? `You are a ${i.role} on ${i.teamName}.`
            : `${i.invitedBy} can send you a new invitation if you change your mind.`}
        </p>
        <Link className="team-link" to="/">Go to DevThrottle</Link>
      </div>
    );
  }

  if (!invitation.canRespond) {
    return (
      <div className="team-page team-accept">
        <h1 className="team-accept-title">This invitation cannot be used</h1>
        <p className="team-accept-sub">{invitation.refusal}</p>
        {invitation.signedInAs !== null && <p className="team-hint">You're signed in as {invitation.signedInAs}.</p>}
      </div>
    );
  }

  return (
    <div className="team-page team-accept">
      <h1 className="team-accept-title">{invitation.invitedBy} invited you to the {invitation.teamName} team</h1>
      <p className="team-accept-sub">
        as a {invitation.role}.{" "}
        {invitation.paidBy !== null ? `${invitation.paidBy} pays for your seat.` : "A Collaborator seat is free."}
      </p>
      <div className="team-row">
        <Button variant="primary" disabled={busy} onClick={() => void accept()}>
          {busy ? "Working..." : "Join the team"}
        </Button>
        <Button variant="secondary" disabled={busy} onClick={() => setConfirmDecline(true)}>Decline</Button>
      </div>
      {actionError !== null && <ErrorBanner message={actionError} />}
      <p className="team-hint">You're signed in as {invitation.signedInAs}.</p>
      <ConfirmDialog
        open={confirmDecline}
        title="Decline this invitation?"
        message={`You will not join ${invitation.teamName}, and this invitation cannot be accepted afterwards. ${invitation.invitedBy} can send a new one.`}
        confirmLabel="Decline"
        cancelLabel="Go back"
        action="decline the invitation"
        onConfirm={decline}
        onClose={() => setConfirmDecline(false)}
      />
    </div>
  );
}
