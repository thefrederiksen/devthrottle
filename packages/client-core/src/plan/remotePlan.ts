// "This account needs the Remote plan" (the Pricing Change mission): the Free plan keeps the Director
// only, and the hosted Gateway refuses a Free account's phone and Cockpit with HTTP 402 and
//
//   { "error": "...", "code": "remote_plan_required", "message": "<one finished sentence>", "subscribeUrl": "<the pricing page>" }
//
// in two places: POST /mobile/enroll (signing in), and ANY authenticated request once an account that
// was signed in has lost the plan.
//
// THE CLIENT IS DUMB (rule 7). The sentence and the address are the Gateway's. This module recognises
// the refusal, carries `message` and `subscribeUrl` unchanged, and holds the one app-level signal both
// shells render the same notice from. It never writes a sentence about plans and never decides what any
// other answer means: the older 402 shapes (hosted_subscription_required, an enrolment 402 with no code,
// a hosted-AI credits 402) are not this refusal and are left exactly as they were, and a 503
// entitlement_unknown is temporary and is never a plan problem.
//
// It imports nothing from the Gateway client on purpose: the client's transport choke point reports
// here, so a dependency in the other direction would be a cycle.

/** The machine-readable code the Gateway puts on the refusal. Read here and nowhere else. */
export const REMOTE_PLAN_REQUIRED_CODE = "remote_plan_required";

/** What the Gateway said, carried to the screen unchanged. */
export interface RemotePlanRequired {
  /** The Gateway's own finished sentence. Shown as it is. */
  message: string;
  /** The pricing page address the Gateway named, or null when it sent none that a browser may open. */
  subscribeUrl: string | null;
}

/**
 * Read the refusal out of an answered request. Returns null for everything that is not exactly it:
 * another status (a 503 entitlement_unknown above all), another code, no code, or a body without the
 * Gateway's sentence - with no sentence there is nothing true this client could show, so the answer
 * falls through to whatever the caller already did with it.
 */
export function readRemotePlanRequired(status: number, body: unknown): RemotePlanRequired | null {
  if (status !== 402) return null;
  if (!body || typeof body !== "object") return null;
  const b = body as { code?: unknown; message?: unknown; subscribeUrl?: unknown };
  if (b.code !== REMOTE_PLAN_REQUIRED_CODE) return null;
  if (typeof b.message !== "string" || b.message.trim() === "") return null;
  return { message: b.message, subscribeUrl: openableAddress(b.subscribeUrl) };
}

// The address goes into a link the person presses, so only a web address is carried. Anything else
// (missing, empty, another scheme) carries null and the notice then offers no button, rather than a
// button that opens something the Gateway never meant.
function openableAddress(raw: unknown): string | null {
  if (typeof raw !== "string" || raw.trim() === "") return null;
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    return null;
  }
  return url.protocol === "https:" || url.protocol === "http:" ? raw : null;
}

// The one app-level signal. It holds the latest refusal so a screen mounted after the answer arrived
// still shows it, and it is cleared only by the person asking to try again.
type Listener = () => void;
const listeners = new Set<Listener>();
let current: RemotePlanRequired | null = null;

/** The refusal in effect, or null. Stable between changes, so React can subscribe to it directly. */
export function currentRemotePlanRequired(): RemotePlanRequired | null {
  return current;
}

/** Subscribe to changes of the refusal in effect. Returns the unsubscribe function. */
export function subscribeRemotePlanRequired(listener: Listener): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** Record that the Gateway refused this account for want of the Remote plan. */
export function reportRemotePlanRequired(info: RemotePlanRequired): void {
  // The app polls, so the same refusal arrives again every few seconds until the notice has replaced
  // the screens that poll. An identical answer changes nothing and must not re-render anything.
  if (current !== null && current.message === info.message && current.subscribeUrl === info.subscribeUrl) return;
  current = info;
  notify();
}

/** Forget the refusal, because the person asked to try again. */
export function clearRemotePlanRequired(): void {
  if (current === null) return;
  current = null;
  notify();
}

function notify(): void {
  for (const listener of [...listeners]) listener();
}

/**
 * Look at one answered request and record the refusal when that is what it is. Called from the
 * client's one transport choke point (gatewayFetch) for every 402 it sees. It reads a COPY of the
 * response, so the caller still reads the body it was always going to read, and it can never turn an
 * answered request into a thrown error: a body that is missing or is not JSON is simply not the refusal.
 */
export async function noteRemotePlanRequired(res: Response): Promise<void> {
  if (res.status !== 402) return;
  let body: unknown;
  try {
    body = await res.clone().json();
  } catch {
    return;
  }
  const info = readRemotePlanRequired(res.status, body);
  if (info !== null) reportRemotePlanRequired(info);
}
