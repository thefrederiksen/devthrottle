// What every session colour means - read from the Gateway's GET /gateway/session-colours and rendered verbatim by the
// "What do the colours mean?" legend (ColourLegend.tsx). The words are written ONCE, on the Gateway, beside the fold
// that decides the colours (SessionColourLegend in CcDirector.Gateway.Contracts), where a test runs an example session
// for every sentence through the real fold. Nothing here decides what a colour means.
//
// The types are hand-written rather than taken from the generated schema, for the reason recorded on `ModelDisplay`
// in api/client.ts and followed by restartRequests.ts: the committed schema predates this route.
import { authHeaders, GatewayError, gatewayErrorMessage, gatewayFetch, type SessionDto } from "../api/client";
import { effectiveColor, stateLabel } from "./ordering";

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

/**
 * The hover text for a session's dot: the legend's name for the colour the Gateway painted, then the Gateway's own
 * label for this session when it says more - "Carrying on: Monitor fix round 2 progress".
 *
 * The hover used to be `lastStatusReason`, the DIRECTOR's reason for its local colour, written before the Wingman has
 * judged the turn. The Director only knows running or stopped, so every dot hovered "working" or "needs you" - a
 * purple "Carrying on" dot said "needs you". Both words here come from the Gateway's one fold, so the hover cannot
 * disagree with the dot. Until the legend has loaded, the hover is the stamped label alone.
 */
export function dotTitle(s: SessionDto, legend: SessionColourLegend | null): string {
  const label = stateLabel(s).trim();
  const colour = effectiveColor(s);
  // The Gateway paints "unknown" grey and its legend explains the two under one entry (SessionColourLegend.Aliases).
  const key = colour === "unknown" ? "grey" : colour;
  const title = legend?.entries.find((e) => e.colour === key)?.title.trim() ?? "";
  if (title.length === 0) return label;
  if (label.length === 0 || label.toLowerCase() === title.toLowerCase()) return title;
  return `${title}: ${label}`;
}

// ---- one read of the legend per page, shared by every surface that shows it ----

type LegendListener = () => void;

/** What a surface knows about the legend right now. */
export interface LegendState {
  legend: SessionColourLegend | null;
  /** Why the read failed, in words for the person; null while loading or once loaded. */
  error: string | null;
}

const _legendListeners = new Set<LegendListener>();
let _legendState: LegendState = Object.freeze({ legend: null, error: null });
let _legendLoading = false;

function setLegendState(next: LegendState): void {
  _legendState = Object.freeze(next);
  for (const l of _legendListeners) l();
}

function loadLegendOnce(): void {
  if (_legendLoading || _legendState.legend !== null) return;
  _legendLoading = true;
  getSessionColourLegend()
    .then((legend) => setLegendState({ legend, error: null }))
    .catch((err: unknown) => {
      // The same words the dialog shows for the same failure.
      const message =
        err instanceof MalformedColourLegendError ? err.message : gatewayErrorMessage(err, "load what the session colours mean");
      console.error("[sessionColours] the colour legend could not be read", err);
      // Not cached: the next surface that mounts asks again, so one failed read does not blank the legend for the page.
      setLegendState({ legend: null, error: message });
    })
    .finally(() => {
      _legendLoading = false;
    });
}

/** Subscribe to the shared legend read; the first subscriber starts it. */
export function subscribeLegend(listener: LegendListener): () => void {
  _legendListeners.add(listener);
  loadLegendOnce();
  return () => {
    _legendListeners.delete(listener);
  };
}

/** The current legend state (a stable reference between changes, as useSyncExternalStore requires). */
export function legendSnapshot(): LegendState {
  return _legendState;
}

/** Test hook: forget the shared legend so the next subscriber reads it again. */
export function resetLegendForTests(): void {
  _legendState = Object.freeze({ legend: null, error: null });
  _legendLoading = false;
}
