import { useEffect } from "react";
import { Navigate, useLocation, useNavigate, useParams } from "react-router-dom";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { TabId } from "@devthrottle/client-core/settings/tabs";
import { LoadingState } from "../components";

// EVERY OLD ADDRESS KEEPS WORKING (owner, 8 Oct 2026). Account, Phone, Your Throttle, Dictionary, Transcription, Network
// and the Team page were pages of their own; they are tabs of Settings now. Each old address leads to its tab, so a
// bookmark, a link in an email or a report, or a habit still lands on what it asked for.

/** The Settings address for one tab, keeping whatever else the old address carried (Your Throttle's ?week= from a
 *  Mentor report, say). */
export function settingsAddress(tab: TabId, search: string, extra?: Record<string, string>): string {
  const params = new URLSearchParams(search);
  params.delete("tab");
  const next = new URLSearchParams({ tab, ...extra });
  params.forEach((value, key) => next.append(key, value));
  return `/settings?${next.toString()}`;
}

/** An old page address that is a tab of Settings now. */
export function SettingsRedirect({ tab }: { tab: TabId }) {
  const location = useLocation();
  return <Navigate to={settingsAddress(tab, location.search)} replace />;
}

/**
 * The old team addresses, /team/{teamId}/members and /team/{teamId}/invite. The Team page is the Members tab of
 * Settings now, for the team ON SCREEN - so the team the address names is put on screen first, then the tab opened.
 * Which teams this person is in is the Gateway's answer, so this waits for it; an address naming a team that is not
 * one of theirs says so rather than opening another team's tab.
 */
export function TeamAddressRedirect({ invite = false }: { invite?: boolean }) {
  const { teamId = "" } = useParams<{ teamId: string }>();
  const { status, teams, choose } = useCurrentTeam();
  const navigate = useNavigate();
  const isMine = teams.some((t) => t.id === teamId);

  useEffect(() => {
    if (status !== "ready" || !isMine) return;
    choose(teamId);
    navigate(`/settings?tab=members${invite ? "&view=invite" : ""}`, { replace: true });
  }, [status, isMine, teamId, invite]);

  if (status === "loading" || (status === "ready" && isMine)) return <LoadingState message="Opening the team..." />;
  return (
    <section className="pane" data-testid="team-address-not-yours">
      <h1 className="pane-title">This team is not one of yours</h1>
      <p className="pane-note">
        {status === "error"
          ? "Your teams could not be read just now. DevThrottle is trying again by itself."
          : "The address names a team you are not a member of, or Teams is not available on this Gateway."}
      </p>
    </section>
  );
}
