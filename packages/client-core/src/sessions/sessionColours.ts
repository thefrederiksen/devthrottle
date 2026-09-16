// What every session colour means - read from the Gateway's GET /gateway/session-colours and rendered verbatim by the
// "What do the colours mean?" legend (ColourLegend.tsx). The words are written ONCE, on the Gateway, beside the fold
// that decides the colours (SessionColourLegend in CcDirector.Gateway.Contracts), where a test runs an example session
// for every sentence through the real fold. Nothing here decides what a colour means.
//
// The types are hand-written rather than taken from the generated schema, for the reason recorded on `ModelDisplay`
// in api/client.ts and followed by restartRequests.ts: the committed schema predates this route.
import { authHeaders, GatewayError, gatewayFetch } from "../api/client";

export const SESSION_COLOURS_PATH = "/gateway/session-colours";

/** One colour, in the Gateway's words. */
export interface SessionColourLegendEntry {
  colour: string;
  hex: string;
  title: string;
  means: string;
  asksForYou: string;
}

/** A swatch with no fold colour behind it - the magenta sentinel. */
export interface SessionColourLegendNote {
  hex: string;
  title: string;
  means: string;
}

/** The whole legend, as the Gateway serialises SessionColourLegendDto. */
export interface SessionColourLegend {
  entries: SessionColourLegendEntry[];
  broken: SessionColourLegendNote;
  verdictNote: string;
}

const HEX_RE = /^#[0-9a-fA-F]{6}$/;

/**
 * The Gateway answered, but not with a usable legend. Its own type, so the dialog can show THIS sentence - what is
 * missing - instead of the generic line a GatewayError gets when the server sent no reason (a 200 has none).
 */
export class MalformedColourLegendError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "MalformedColourLegendError";
  }
}

/** Read the legend from the Gateway. A failed or malformed answer throws; there is no built-in copy to fall back on. */
export async function getSessionColourLegend(signal?: AbortSignal): Promise<SessionColourLegend> {
  const res = await gatewayFetch(SESSION_COLOURS_PATH, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "load what the session colours mean");
  return parseSessionColourLegend(await res.json());
}

/**
 * Check the Gateway's answer field by field. A legend with a missing sentence or an unusable swatch is refused whole,
 * rather than drawn with a gap: a legend that silently leaves a colour out tells the person that colour does not exist.
 */
export function parseSessionColourLegend(body: unknown): SessionColourLegend {
  const fail = (what: string): never => {
    throw new MalformedColourLegendError(`The Gateway's colour legend is missing ${what}.`);
  };
  const text = (value: unknown, what: string): string =>
    typeof value === "string" && value.trim() !== "" ? value : fail(what);
  const hex = (value: unknown, what: string): string =>
    typeof value === "string" && HEX_RE.test(value) ? value : fail(`a usable colour for ${what}`);

  if (typeof body !== "object" || body === null) return fail("its body");
  const raw = body as Record<string, unknown>;
  if (!Array.isArray(raw.entries) || raw.entries.length === 0) return fail("its list of colours");

  const entries = raw.entries.map((item: unknown, i: number): SessionColourLegendEntry => {
    if (typeof item !== "object" || item === null) return fail(`colour ${i + 1}`);
    const e = item as Record<string, unknown>;
    const colour = text(e.colour, `the name of colour ${i + 1}`);
    return {
      colour,
      hex: hex(e.hex, colour),
      title: text(e.title, `the title for ${colour}`),
      means: text(e.means, `what ${colour} means`),
      asksForYou: text(e.asksForYou, `whether ${colour} asks for you`),
    };
  });

  if (typeof raw.broken !== "object" || raw.broken === null) return fail("the note about magenta");
  const b = raw.broken as Record<string, unknown>;

  return {
    entries,
    broken: { hex: hex(b.hex, "magenta"), title: text(b.title, "the magenta title"), means: text(b.means, "what magenta means") },
    verdictNote: text(raw.verdictNote, "the note about the Wingman's verdicts"),
  };
}
