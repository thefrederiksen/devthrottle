import { useEffect, useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import {
  deleteTeam,
  leaveTeam,
  renameTeam,
  type TeamPageManage,
} from "@devthrottle/client-core/teams/teamPageClient";
import { Button, ConfirmDialog } from "../components";

// Rename, delete and leave a team (Teams v1, the owner's 8 October ruling, "finish the first version"), on the Members
// tab. The Owner gets "The team" card: the name, Rename, and a red "Delete this team" that stays disabled, with the
// Gateway's sentence saying why, while anyone else is in the team; its confirmation asks for the team's name typed
// exactly. Every other member gets "Leave this team", confirmed with the Gateway's sentence saying what leaving does.
//
// CRITICAL RULE 7 - whether each control shows, whether Delete can be used, why not, and what each confirmation says
// are the Gateway's (the page's `manage`). The card lays them out; it never reads a role. Comparing what was typed with
// the name on screen only enables the button - the Gateway checks the name again and refuses a mismatch itself.

/** Where a person lands once the team is gone from their menu: their own account's Settings. */
export const AFTER_LEAVING_ADDRESS = "/settings?tab=account";

export interface TeamManageSectionProps {
  teamId: string;
  teamName: string;
  manage: TeamPageManage;
  /** Reload the page after a rename, so the card and the summary show the Gateway's new answer. */
  onRenamed: (note: string) => Promise<void>;
}

export function TeamManageSection({ teamId, teamName, manage, onRenamed }: TeamManageSectionProps) {
  const { choose, refresh } = useCurrentTeam();
  const navigate = useNavigate();
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState(teamName);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [typed, setTyped] = useState("");
  const [leaveOpen, setLeaveOpen] = useState(false);

  useEffect(() => setName(teamName), [teamName]);

  // Nothing to offer: a member the Gateway gives neither control (none today, but the card never guesses).
  if (!manage.canRename && !manage.canLeave) return null;

  const saveName = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await renameTeam(teamId, name);
    } catch (err) {
      setError(gatewayErrorMessage(err, "rename the team"));
      setBusy(false);
      return;
    }
    try {
      // The menu's team block and the "you" card read the team list, so read it again for the new name to show at once.
      await refresh();
    } catch (err) {
      setError(`The team is renamed, but your teams could not be read again: ${gatewayErrorMessage(err, "read your teams")} Reload the page to see the new name in the menu.`);
    }
    setRenaming(false);
    setBusy(false);
    await onRenamed(`The team is now called ${name.trim()}.`);
  };

  // After the team is gone for this person, the own account goes on screen and the team list is read again, so the team
  // leaves the menu at once. The change has happened by then, so a failed read is said plainly - never thrown into the
  // dialog, where Confirm would offer to do it a second time.
  const leaveTheTeamBehind = async (done: string) => {
    choose(null);
    try {
      await refresh();
    } catch (err) {
      setError(`${done} But your teams could not be read again: ${gatewayErrorMessage(err, "read your teams")} Reload the page.`);
      return;
    }
    navigate(AFTER_LEAVING_ADDRESS);
  };

  const confirmDelete = async () => {
    await deleteTeam(teamId, typed);
    await leaveTheTeamBehind(`${teamName} is deleted.`);
  };

  const confirmLeave = async () => {
    await leaveTeam(teamId);
    await leaveTheTeamBehind(`You have left ${teamName}.`);
  };

  return (
    <section className="team-card team-manage" aria-label="The team">
      <h2 className="team-h2">The team</h2>

      {manage.canRename ? (
        renaming ? (
          <form className="team-manage-rename" onSubmit={(e) => void saveName(e)}>
            <label className="team-label" htmlFor="team-manage-name">Team name</label>
            <input
              id="team-manage-name"
              className="team-input"
              value={name}
              maxLength={100}
              autoFocus
              disabled={busy}
              onChange={(e) => setName(e.target.value)}
            />
            <div className="team-row">
              <Button variant="primary" type="submit" disabled={busy}>{busy ? "Saving..." : "Save name"}</Button>
              <Button variant="ghost" disabled={busy} onClick={() => { setRenaming(false); setName(teamName); setError(null); }}>Cancel</Button>
            </div>
          </form>
        ) : (
          <div className="team-manage-name-row">
            <span className="team-manage-name" data-testid="team-manage-name">{teamName}</span>
            <Button variant="ghost" onClick={() => { setRenaming(true); setError(null); }}>Rename</Button>
          </div>
        )
      ) : (
        <div className="team-manage-name-row">
          <span className="team-manage-name" data-testid="team-manage-name">{teamName}</span>
        </div>
      )}

      {manage.canRename && (
        <div className="team-manage-danger">
          <Button variant="danger" disabled={!manage.canDelete} onClick={() => { setTyped(""); setDeleteOpen(true); }}>
            Delete this team
          </Button>
          {manage.deleteBlocked !== null && <p className="team-hint" data-testid="team-delete-blocked">{manage.deleteBlocked}</p>}
        </div>
      )}

      {manage.canLeave && (
        <div className="team-manage-danger">
          <Button variant="danger" onClick={() => setLeaveOpen(true)}>Leave this team</Button>
        </div>
      )}

      {error !== null && <p className="team-warn" role="alert">{error}</p>}

      <ConfirmDialog
        open={deleteOpen && manage.canDelete}
        title={`Delete ${teamName}?`}
        message={(
          <>
            <p className="team-manage-warning">{manage.deleteWarning}</p>
            <label className="team-label" htmlFor="team-manage-confirm-name">
              To confirm, type the team&apos;s name: <strong>{teamName}</strong>
            </label>
            <input
              id="team-manage-confirm-name"
              className="team-input"
              value={typed}
              autoComplete="off"
              onChange={(e) => setTyped(e.target.value)}
            />
          </>
        )}
        confirmLabel="Delete this team"
        cancelLabel="Keep the team"
        confirmDisabled={typed.trim() !== teamName}
        danger
        action="delete the team"
        onConfirm={confirmDelete}
        onClose={() => setDeleteOpen(false)}
      />
      <ConfirmDialog
        open={leaveOpen && manage.canLeave}
        title={`Leave ${teamName}?`}
        message={manage.leaveWarning ?? ""}
        confirmLabel="Leave this team"
        cancelLabel="Stay in the team"
        danger
        action="leave the team"
        onConfirm={confirmLeave}
        onClose={() => setLeaveOpen(false)}
      />
    </section>
  );
}
