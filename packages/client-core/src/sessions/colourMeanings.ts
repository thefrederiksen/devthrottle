import { BROKEN_HEX, dotColor } from "./ordering";

// WHAT THE SESSION COLOURS MEAN - the words behind every dot, for the "What do the colours mean?" legend that the
// Cockpit roster rail and the phone roster both mount (ColourLegend.tsx). ONE list, so the two screens explain the
// colours in the same words.
//
// IT EXPLAINS THE PALETTE; IT DECIDES NOTHING. Which colour a session wears is the Gateway's fold
// (SessionOrdering.EffectiveColor) and nothing here reads a session. Each entry names a fold colour, and its swatch
// is dotColor() of that name - the same COLORS table the legend swatches elsewhere already paint - so the dot in the
// legend is the pixel the roster paints. colourMeanings.test.ts holds this list to that table in both directions: a
// colour the palette gains without an explanation here fails, and so does an explanation for a colour it lacks.

export interface ColourLegendEntry {
  /** The Gateway's fold colour name - a key of the COLORS palette. */
  colour: string;
  /** The short name a person calls this state. */
  title: string;
  /** What the session is doing, in one or two plain sentences. */
  means: string;
  /** Whether this colour is asking for the person looking at it. */
  asksForYou: string;
}

/** Every colour a session dot can wear, most urgent first. */
export const COLOUR_LEGEND: readonly ColourLegendEntry[] = [
  {
    colour: "red",
    title: "Needs you",
    means: "The session has stopped and is waiting for you - at a prompt, on a permission, or with a question.",
    asksForYou: "Yes",
  },
  {
    colour: "blue",
    title: "Working",
    means: "The agent is running a turn right now. A working session is always blue.",
    asksForYou: "No",
  },
  {
    colour: "cyan",
    title: "Done",
    means:
      "The session stopped and the Wingman judged it finished: the work is done, or it is only reporting something and asks you nothing.",
    asksForYou: "No",
  },
  {
    colour: "purple",
    title: "Carrying on",
    means: "The session stopped, but the Wingman judged it will continue on its own. It turns red if it does not.",
    asksForYou: "No",
  },
  {
    colour: "yellow",
    title: "Wingman reading",
    means:
      "The session stopped and is being looked at before it is shown to you: the Wingman is reading it, or its voice summary is being prepared.",
    asksForYou: "Not yet",
  },
  {
    colour: "green",
    title: "Ready",
    means: "A brand-new session at its first prompt. It has not done anything yet.",
    asksForYou: "No",
  },
  {
    colour: "orange",
    title: "Transcribing",
    means: "Your dictation is still uploading or being turned into text. Wait before typing into it.",
    asksForYou: "No",
  },
  {
    colour: "supporting",
    title: "Supervised",
    means: "Stopped, but another session or a schedule is driving it, so it does not wait on you.",
    asksForYou: "No",
  },
  {
    colour: "grey",
    title: "Snoozed or exited",
    means: "You snoozed it, its agent exited, or its state could not be read. The words beside the dot say which.",
    asksForYou: "No",
  },
  {
    colour: "error",
    title: "Crashed",
    means: "The agent process died. Darker than the red that means needs you, so a crash never reads as a finish.",
    asksForYou: "Look at it",
  },
];

/**
 * Palette names that share another entry's explanation instead of having their own. "unknown" is the fold's word
 * for a state it could not read, and it paints the one grey on purpose - so "Snoozed or exited" covers it.
 */
export const SHARES_AN_ENTRY: Readonly<Record<string, string>> = { unknown: "grey" };

/** The magenta sentinel. Not a state, so it is not an entry - but a person who sees it needs to know what it is. */
export const BROKEN_NOTE = {
  hex: BROKEN_HEX,
  title: "Magenta",
  means:
    "Not a state. The screen received a colour it does not understand - usually the Gateway and this app on different versions. Reload, and report it if it stays.",
} as const;

/** Why three of the colours may never appear for an account. */
export const VERDICT_NOTE =
  "Done, Carrying on and Wingman reading appear only when the Wingman's verdicts are switched on for your account. With them off, every stopped session is red.";

/** The swatch for an entry: the palette's own hex for its colour name. */
export function swatchHex(entry: ColourLegendEntry): string {
  return dotColor(entry.colour);
}
