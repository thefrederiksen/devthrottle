// THE WEBSITE'S KEY TO YOUR INBOX (brief E, 8 November 2026). The Gateway folds every word of the card
// (GET/POST/DELETE /gateway/website-inbox-key); this file only carries it. Throws on a non-2xx with the
// Gateway's own sentence (no-fallback rule): the card shows it as it is.
import { authHeaders, GatewayError } from "../api/client";

/** The card, every word finished on the Gateway. `key` is present only in the answer that made it. */
export interface WebsiteInboxCard {
  title: string;
  explanation: string;
  state: "none" | "active" | "revoked";
  statusLine: string;
  mintLabel: string;
  mintNote: string | null;
  canRevoke: boolean;
  revokeLabel: string;
  key: string | null;
  keyLine: string | null;
}

const PATH = "/gateway/website-inbox-key";

async function call(method: "GET" | "POST" | "DELETE", signal?: AbortSignal): Promise<WebsiteInboxCard> {
  const res = await fetch(PATH, { method, headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (!res.ok) throw await GatewayError.from(res, method === "GET" ? "load the website key" : "change the website key");
  return (await res.json()) as WebsiteInboxCard;
}

/** Where the website's key stands. */
export const getWebsiteInboxCard = (signal?: AbortSignal) => call("GET", signal);

/** Make a new key; the answer carries it, this once. */
export const mintWebsiteInboxKey = () => call("POST");

/** Revoke the key. */
export const revokeWebsiteInboxKey = () => call("DELETE");
