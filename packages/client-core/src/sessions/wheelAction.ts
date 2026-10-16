import type { SessionDto } from "../api/client";

// THE TERMINAL TAB'S WHEEL BUTTON, AS THE GATEWAY RULED IT (mission #4070, phase 4).
//
// An API-mode session's Terminal tab shows "Take the wheel" while the agent's protocol process holds the
// conversation, and "Hand back" while the user holds it in the agent's real terminal. WHICH button, its words,
// whether it can be pressed and why not are all decided ONCE on the Gateway and read here verbatim (CLAUDE.md
// rule 7). Nothing in this file or in the button looks at SessionDto.connectionMode or SessionDto.wheelHolder:
// a terminal-mode session shows no button because the Gateway's verdict says "none", never because a client
// checked the mode.
//
// THE ONE PLACE THE GATEWAY'S FIELD NAMES ARE READ. Every surface (the Cockpit, the phone) takes its button
// from readWheelAction and nothing else, so when the names on the wire change, this reader is the only edit.

/** Which verb the button calls. The Gateway names it; the client only maps it to the route. */
export type WheelVerb = "take" | "handBack";

/** The Terminal tab's wheel button, exactly as the Gateway ruled it. */
export interface WheelAction {
  /** Which verb pressing it calls: take the wheel, or hand it back. */
  verb: WheelVerb;
  /** The button's words, verbatim ("Take the wheel", "Hand back"). */
  label: string;
  /** False when the button is shown but cannot be pressed now. */
  enabled: boolean;
  /** Why it cannot be pressed, verbatim; null when it is enabled. */
  disabledReason: string | null;
}

// PROVISIONAL WIRE SHAPE. The Gateway Tech Lead is adding this verdict to the API-mode display
// (ApiModeSessionDisplay) and the final names have not been sent yet. Until they are, this reader is written
// against the shape below; only this block and readWheelAction change when they arrive.
interface WheelActionWire {
  kind?: string | null;
  label?: string | null;
  enabled?: boolean | null;
  disabledReason?: string | null;
}

const WIRE_VERBS: Readonly<Record<string, WheelVerb>> = { take: "take", handBack: "handBack" };
const WIRE_NONE = "none";

function wireOf(session: SessionDto): WheelActionWire | null | undefined {
  const display = session.apiMode as (typeof session.apiMode & { terminalWheel?: WheelActionWire | null }) | null | undefined;
  return display?.terminalWheel;
}

/**
 * The wheel button for this session, or null when the Gateway says there is none: no API-mode verdict at all (a
 * terminal session, or an older Gateway), a verdict whose button is "none", or no session row yet.
 *
 * A verdict this client cannot read - a verb it does not know, or a button with no words - is a broken contract
 * between the Gateway and this build, so it THROWS rather than inventing a button: a client that guessed here is
 * exactly what rule 7 forbids. The caller shows that failure; it never shows a guessed button.
 */
export function readWheelAction(session: SessionDto | null | undefined): WheelAction | null {
  if (!session) return null;
  const wire = wireOf(session);
  if (!wire) return null;
  const kind = wire.kind ?? "";
  if (kind === WIRE_NONE) return null;
  const verb = WIRE_VERBS[kind];
  if (!verb) {
    throw new Error(
      `The Gateway sent a Terminal tab wheel button this app does not know ("${kind}"), so no button is shown. `
        + "Update the app or the Gateway so they agree.",
    );
  }
  const label = (wire.label ?? "").trim();
  if (!label) {
    throw new Error("The Gateway sent a Terminal tab wheel button with no words on it, so no button is shown.");
  }
  const enabled = wire.enabled === true;
  const reason = (wire.disabledReason ?? "").trim();
  return { verb, label, enabled, disabledReason: enabled ? null : reason || null };
}
