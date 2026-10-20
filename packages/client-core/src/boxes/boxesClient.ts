// The Boxes screen's two calls (Factory Boxes mission, phase 1): the typed, same-origin read of GET /boxes - the
// desktops the account's connected Directors host - and POST /boxes/{boxId}/desktop-ticket, which answers the full
// address to open one desktop at. The shapes are docs/missions/factory-boxes-2026-10-19/phase1-hosted-desktop-list.md
// section 6 and the relay leg's ticket route (BoxDesktopEndpoints.MapTicket).
//
// CRITICAL RULE 7 - THE GATEWAY DECIDES. A box's state ("Online" in phase 1, more values later) is the Gateway's word
// and is carried verbatim; this client never turns it into a meaning of its own. The desktop's address is the
// Gateway's too: the desktop is served on a separate desktop host with a one-time ticket, so a client never builds
// that address - it opens exactly the `url` the ticket call returns.
//
// AN ANSWER THAT BREAKS THE CONTRACT IS THROWN, NEVER DRAWN AS A GUESS: a row missing its box id, a state that is not
// a string, a ticket without an address. Each would otherwise reach the screen as something plausible.
//
// Both calls live here, beside the account client, so the phone can reuse them.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** The list route (contract section 6). */
export const BOXES_PATH = "/boxes";

/** One desktop a connected Director of the account hosts (contract section 6). */
export interface BoxRow {
  boxId: string;
  displayName: string;
  directorId: string;
  /** The Director's display name from its Hello; empty when it has none. */
  directorName: string;
  machineName: string;
  /** The Gateway's word for the box's state, shown exactly as sent. */
  state: string;
  /** The desktop's path on the desktop host. NOT an address to navigate to: a box is opened by its ticket. */
  relativeDesktopPath: string;
}

/** The 200 answer of GET /boxes. An empty list is `{ count: 0, boxes: [] }`. */
export interface BoxesList {
  count: number;
  boxes: BoxRow[];
}

/** The 200 answer of POST /boxes/{boxId}/desktop-ticket: the full address to open, and when the ticket expires. */
export interface DesktopTicket {
  url: string;
  expiresAtUtc: string;
}

const UNREADABLE_LIST = "The Gateway sent a list of boxes this app cannot read";
const UNREADABLE_TICKET = "The Gateway sent a desktop ticket this app cannot read";

/** Read the desktops the account's connected Directors host. Every failure is thrown with the Gateway's sentence. */
export async function getBoxes(signal?: AbortSignal): Promise<BoxesList> {
  const res = await gatewayFetch(BOXES_PATH, {
    headers: { ...authHeaders(), Accept: "application/json" },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "read the boxes");
  return readList(await readJson(res, UNREADABLE_LIST));
}

/** Ask for a one-time ticket to open one box's desktop. Every failure is thrown with the Gateway's sentence. */
export async function getDesktopTicket(boxId: string, signal?: AbortSignal): Promise<DesktopTicket> {
  const res = await gatewayFetch(`${BOXES_PATH}/${encodeURIComponent(boxId)}/desktop-ticket`, {
    method: "POST",
    headers: { ...authHeaders(), Accept: "application/json" },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "open the desktop");
  const body = await readJson(res, UNREADABLE_TICKET);
  if (!isObject(body) || !isText(body.url) || typeof body.expiresAtUtc !== "string") {
    throw unreadable(`${UNREADABLE_TICKET}: it has no address to open.`);
  }
  return { url: body.url, expiresAtUtc: body.expiresAtUtc };
}

// The Gateway WAS reached; a body that is not JSON is its fault and is said so, never left to surface as a
// SyntaxError, which the error wording reads as "cannot reach the Gateway".
async function readJson(res: Response, what: string): Promise<unknown> {
  const text = await res.text();
  try {
    return JSON.parse(text);
  } catch {
    throw unreadable(`${what}: its answer is not valid JSON.`);
  }
}

function readList(body: unknown): BoxesList {
  if (!isObject(body) || typeof body.count !== "number" || !Array.isArray(body.boxes)) {
    throw unreadable(`${UNREADABLE_LIST}: it has no count or no list.`);
  }
  const boxes = body.boxes.map((row, index) => readRow(row, index));
  return { count: body.count, boxes };
}

function readRow(row: unknown, index: number): BoxRow {
  if (
    !isObject(row) ||
    !isText(row.boxId) ||
    !isText(row.displayName) ||
    typeof row.directorId !== "string" ||
    typeof row.directorName !== "string" ||
    typeof row.machineName !== "string" ||
    !isText(row.state) ||
    typeof row.relativeDesktopPath !== "string"
  ) {
    throw unreadable(`${UNREADABLE_LIST}: box ${index + 1} is missing a field.`);
  }
  return {
    boxId: row.boxId,
    displayName: row.displayName,
    directorId: row.directorId,
    directorName: row.directorName,
    machineName: row.machineName,
    state: row.state,
    relativeDesktopPath: row.relativeDesktopPath,
  };
}

// The sentence is put where the error wording looks for a reason, so the person reads it rather than a status line.
function unreadable(sentence: string): GatewayError {
  return new GatewayError(502, sentence, { reason: sentence, retryable: false });
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** A string with at least one character that is not a space. */
function isText(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}
