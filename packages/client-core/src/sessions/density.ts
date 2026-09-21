import { useSyncExternalStore } from "react";

// ONE SWITCH FOR THE WHOLE WINDOW (owner ruling, 2026-09-20). How much detail the Cockpit shows is a
// single choice with three positions, and it drives BOTH the conversation and the session cards in the
// rail, because the owner asked for the same thing twice: fewer facts on a card, and a conversation
// clean enough to screenshot. Two controls would be two things to set before every screenshot.
//
//   clean      - the conversation, and three lines per card. The screenshot setting.
//   normal     - tool calls named in the conversation; the supervision line back on the card.
//   everything - thinking and tool results too; every tag back on the card. What the Cockpit
//                showed before this switch existed, so nothing was taken away - it was put behind it.
//
// DENSITY HIDES FACTS, NEVER ALARMS. A card's warning row - a prompt that did not reach the agent, a
// snooze that ended, a session winding down - is drawn at every position. A setting that can make the
// product quieter about something going wrong is not a density setting, it is a bug, and it would make
// a screenshot taken at "clean" a lie about the fleet.

export type Density = "clean" | "normal" | "everything";

export const DENSITY_STORAGE_KEY = "cockpit.density";

const DENSITIES: readonly Density[] = ["clean", "normal", "everything"];

/** The words on the switch, in order. The value is never shown to anyone; these are. */
export const DENSITY_LABELS: ReadonlyArray<{ value: Density; label: string; title: string }> = [
  { value: "clean", label: "Clean", title: "The conversation, and three lines per session card" },
  { value: "normal", label: "Normal", title: "Tool calls named, and the supervision line on each card" },
  { value: "everything", label: "Everything", title: "Thinking, tool results, and every fact on each card" },
];

/** The three machinery flags the conversation filter carries, for a given density. */
export interface DensityFilterFlags {
  showToolCalls: boolean;
  showToolResults: boolean;
  showThinking: boolean;
}

export function filterFlagsFor(density: Density): DensityFilterFlags {
  switch (density) {
    case "everything":
      return { showToolCalls: true, showToolResults: true, showThinking: true };
    case "normal":
      return { showToolCalls: true, showToolResults: false, showThinking: false };
    default:
      return { showToolCalls: false, showToolResults: false, showThinking: false };
  }
}

/** Does this density draw the supervision line (started / open / idle / turns) on a session card? */
export function showsSupervision(density: Density): boolean {
  return density !== "clean";
}

/** Does this density draw the tag row (machine, model, changes, voice, last seen) on a session card? */
export function showsTags(density: Density): boolean {
  return density === "everything";
}

function isDensity(raw: string | null): raw is Density {
  return raw !== null && (DENSITIES as readonly string[]).includes(raw);
}

function read(): Density {
  try {
    const raw = window.localStorage.getItem(DENSITY_STORAGE_KEY);
    if (isDensity(raw)) return raw;
  } catch {
    /* localStorage unavailable - fall through to the default */
  }
  // Clean is the default on purpose. Everything the old Cockpit showed is one click away, and a person
  // opening a session for the first time should meet the conversation, not the instrumentation.
  return "clean";
}

let current: Density = "clean";
let loaded = false;
const listeners = new Set<() => void>();

function snapshot(): Density {
  if (!loaded) {
    current = read();
    loaded = true;
  }
  return current;
}

export function setDensity(next: Density): void {
  snapshot();
  if (next === current) return;
  current = next;
  try {
    window.localStorage.setItem(DENSITY_STORAGE_KEY, next);
  } catch {
    /* the choice simply will not outlive this session */
  }
  for (const listener of listeners) listener();
}

export function getDensity(): Density {
  return snapshot();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The density every surface reads. Re-renders its caller when the switch moves, wherever it moved. */
export function useDensity(): Density {
  // The server snapshot is the default rather than the stored value: this runs where there is no
  // window, and reading storage there would throw on the first render rather than on a click.
  return useSyncExternalStore(subscribe, snapshot, () => "clean" as Density);
}

/** Test-only: forget the loaded value so the next read goes back to storage. */
export function resetDensityForTests(): void {
  loaded = false;
  current = "clean";
  listeners.clear();
}
