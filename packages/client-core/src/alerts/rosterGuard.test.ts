// @vitest-environment node
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

// THE ROSTER GUARD (the Alerts mission, goal 1). Nothing that asks the owner for something renders inside the session
// list, on the phone or in the Cockpit. A restart request, a secret transfer and a message request each used to be a card
// stacked above the sessions, and each new feature copied the one before. They live in the Alerts tray now; this fails
// the moment a request panel or card is mounted in either roster again.
//
// It reads the two roster files as text. A request card of any kind is named for what it asks - a Request, a Transfer,
// an Approval - and drawn as a Panel or a Card; and the three old cards came from three shared modules. Either one in a
// roster file is the pattern this mission ended.

const REPO = join(__dirname, "..", "..", "..", "..");

const ROSTERS = ["apps/mobile/src/pages/Home.tsx", "apps/cockpit/src/sessions/SessionRoster.tsx"];

/** A JSX element named for a request and drawn as a panel or a card: <RestartRequestsPanel />, <SecretTransferCard ...>. */
const REQUEST_ELEMENT = /<\s*([A-Z]\w*(?:Request|Transfer|Approval)s?(?:Panel|Card)s?)\b/g;

/** The modules the three old cards were mounted from. */
const OLD_MODULES = ["restart/RestartRequestsPanel", "secrets/SecretTransfers", "fleet/LinkRequestsPanel"];

/** Every request panel or card a roster file mounts or imports, as "file: what". Empty when the roster is clean. */
export function requestPanelsIn(file: string, text: string): string[] {
  const found: string[] = [];
  for (const m of text.matchAll(REQUEST_ELEMENT)) found.push(`${file}: <${m[1]}>`);
  for (const mod of OLD_MODULES) {
    if (text.includes(`client-core/${mod}"`)) found.push(`${file}: imports ${mod}`);
  }
  return found;
}

describe("the roster guard", () => {
  it("SessionLists_MountNoRequestPanel_OnThePhoneOrInTheCockpit", () => {
    const found = ROSTERS.flatMap((file) => requestPanelsIn(file, readFileSync(join(REPO, file), "utf8")));
    expect(found).toEqual([]);
  });

  // The guard's own instrument: it must see each shape it exists to catch, or an empty result proves nothing.
  it("requestPanelsIn_SeesEveryShapeOfRequestCard", () => {
    const text = [
      'import { RestartRequestsPanel } from "@devthrottle/client-core/restart/RestartRequestsPanel";',
      "<RestartRequestsPanel />",
      '<SecretTransfersPanel where="phone" />',
      "<LinkRequestsPanel />",
      "<SecretTransferCard transfer={t} />",
      "<DeployApprovalPanel />",
    ].join("\n");
    expect(requestPanelsIn("x.tsx", text)).toEqual([
      "x.tsx: <RestartRequestsPanel>",
      "x.tsx: <SecretTransfersPanel>",
      "x.tsx: <LinkRequestsPanel>",
      "x.tsx: <SecretTransferCard>",
      "x.tsx: <DeployApprovalPanel>",
      "x.tsx: imports restart/RestartRequestsPanel",
    ]);
  });

  it("requestPanelsIn_LeavesTheRowBadgesAlone", () => {
    // The amber per-session icons are row status, not banners (section 5.3): they stay in the roster.
    expect(requestPanelsIn("x.tsx", "<RosterSecretBadge sessionId={sid} /><RosterLinkBadge sessionId={sid} />")).toEqual([]);
  });
});
