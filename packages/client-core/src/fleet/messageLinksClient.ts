// Message links (issue #3548): the owner lets two sessions that are not parent and child talk to each other -
// once, once with a reply, or as much as they need. The Gateway decides who may set one up and writes every
// sentence, the link's own summary and each refusal; this file only carries the request and the words.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** How much talking a link allows, as the Gateway names it. */
export type MessageLinkAmount = "once" | "once-with-reply" | "ongoing";

/** The three amounts, in the order the owner sees them, with the words for each. */
export const MESSAGE_LINK_AMOUNTS: ReadonlyArray<{ amount: MessageLinkAmount; label: string; detail: string }> = [
  { amount: "once", label: "One message", detail: "It may send one message, with no reply." },
  { amount: "once-with-reply", label: "One message and a reply", detail: "It may send one message, and the other may answer it once." },
  { amount: "ongoing", label: "As much as they need", detail: "They may message each other both ways until either session ends or you remove the link." },
];

export interface MessageLink {
  linkId: string;
  senderSessionId: string;
  recipientSessionId: string;
  amount: MessageLinkAmount | string;
  /** live, used, removed or ended. */
  status: string;
  /** The Gateway's sentence saying what this link allows. */
  summary: string;
  setUpBy: string;
  setUpAtUtc: string;
  usedAtUtc?: string | null;
  endedBy?: string | null;
  endedAtUtc?: string | null;
}

export interface MessageLinkList {
  links: MessageLink[];
  /** How far back links that stopped are still listed. */
  stoppedWithinDays: number;
}

/** GET /fleet/links: every live link, and the ones that stopped recently. */
export async function listMessageLinks(signal?: AbortSignal): Promise<MessageLinkList> {
  const res = await gatewayFetch("/fleet/links", {
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "load the message links");
  return (await res.json()) as MessageLinkList;
}

/** POST /fleet/links. A refusal throws a GatewayError whose message is the Gateway's sentence. */
export async function setUpMessageLink(
  senderSessionId: string,
  recipientSessionId: string,
  amount: MessageLinkAmount,
): Promise<{ link: MessageLink; replaced: MessageLink[] }> {
  const res = await gatewayFetch("/fleet/links", {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ senderSessionId, recipientSessionId, amount }),
  });
  if (!res.ok) throw await GatewayError.from(res, "set up the message link");
  return (await res.json()) as { link: MessageLink; replaced: MessageLink[] };
}

/** DELETE /fleet/links/{id}. Removing a link that already stopped changes nothing and is not an error. */
export async function removeMessageLink(linkId: string): Promise<MessageLink> {
  const res = await gatewayFetch(`/fleet/links/${encodeURIComponent(linkId)}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
  });
  if (!res.ok) throw await GatewayError.from(res, "remove the message link");
  return (await res.json()) as MessageLink;
}
