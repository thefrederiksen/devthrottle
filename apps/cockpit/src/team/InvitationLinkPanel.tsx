import { useState } from "react";
import type { InvitationLink } from "@devthrottle/client-core/teams/invitationsClient";
import { Button } from "../components";

// The accept link, shown ONCE to the person who just sent or resent an invitation (Teams v1, copy the invitation link),
// so they can pass it on themselves while the invitation email cannot be sent. The link and the sentence beside it are
// the Gateway's (rule 7); this panel lays them out and copies the link. It is held only in this screen's state - it is
// not in the list of waiting invitations, and leaving the screen loses it, which is why the sentence names Resend.

export interface InvitationLinkPanelProps {
  link: InvitationLink;
}

export function InvitationLinkPanel({ link }: InvitationLinkPanelProps) {
  const [copied, setCopied] = useState<"copied" | "failed" | null>(null);

  const copy = async (url: string) => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied("copied");
    } catch {
      setCopied("failed");
    }
  };

  return (
    <div className="team-link" aria-label="Invitation link">
      {link.url !== null && (
        <div className="team-link-row">
          <input
            className="team-input team-link-url"
            type="text"
            readOnly
            aria-label="The invitation link"
            value={link.url}
            onFocus={(e) => e.currentTarget.select()}
          />
          <Button variant="primary" onClick={() => void copy(link.url!)}>Copy invitation link</Button>
        </div>
      )}
      <p className="team-hint">{link.note}</p>
      {copied === "copied" && <p className="team-ok" role="status">Copied. Paste it into a message to them.</p>}
      {copied === "failed" && (
        <p className="team-warn" role="alert">The browser would not copy it. Select the link above and copy it yourself.</p>
      )}
    </div>
  );
}
