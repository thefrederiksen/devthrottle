// The watch view of one factory, exactly as the Gateway serializes it (Watch your factory, version one).
//
// These types MIRROR src/CcDirector.Gateway.Contracts/FactoryWatchDtos.cs field for field, in the Gateway's web JSON
// (camelCase). The Gateway decides every verdict in the view (critical rule 7): where each station stands, which
// character stands for which seat and in what colour, what kind each step is and the words it carries. The player
// only draws it.
//
// The contract guard: FactoryWatchContractFixtureTests (Gateway.UnitTests) folds a fixed sample and requires it to
// equal factoryWatchView.fixture.json; factoryWatchTypes.test.ts requires that same file to have exactly the keys
// listed below. A field added on one side and not the other fails one of the two.

import type { FactoryWindow } from "../factoryAgentsClient";

/** How the floor is laid out (FactoryWatchLayout). */
export type FactoryWatchLayout = "workshop" | "hub" | "lines";

/** What a step is (FactoryWatchStepKind). A closed list; the player has one animation per kind. "waiting" comes only in
 * the contributor audience (piece 3): the request is waiting its turn - never drawn as a failure. */
export type FactoryWatchStepKind = "woke" | "delivered" | "talked" | "checked-nothing" | "hiccup" | "waiting";

export const FACTORY_WATCH_STEP_KINDS: readonly FactoryWatchStepKind[] = ["woke", "delivered", "talked", "checked-nothing", "hiccup", "waiting"];

/** FactoryWatchViewDto. */
export interface FactoryWatchView {
  /** The factory's id. Not for display. Empty in the contributor audience. */
  factoryId: string;
  title: string;
  layout: FactoryWatchLayout;
  /** The floor's size in tiles. */
  floorWidth: number;
  floorHeight: number;
  stations: FactoryWatchStation[];
  /** The production-line halls; empty unless the layout is "lines". */
  lines: FactoryWatchLine[];
  /** One character per seat. */
  characters: FactoryWatchCharacter[];
  /** What happened, oldest first. */
  steps: FactoryWatchStep[];
  window: FactoryWindow;
  /** Set only when there are no steps. */
  emptyText: string | null;
  /** Set when the window held more rows than one read returns. Always null in the contributor audience. */
  truncatedText: string | null;
  /** What the view rests on, in plain sentences. Always empty in the contributor audience. */
  notes: string[];
  /** The words of the big button a quiet floor opens with ("Watch what happened last"), or null when there is nothing
   * to replay. The player shows the button only when this is set, and never writes the words itself. */
  replayText: string | null;
}

/** FactoryWatchStationDto: one seat's station; position and size in floor tiles. */
export interface FactoryWatchStation {
  /** The seat's id. Not for display. */
  id: string;
  label: string;
  sub: string;
  isBoss: boolean;
  gx: number;
  gy: number;
  width: number;
  height: number;
  /** The production line the station stands in, or null. */
  line: string | null;
}

/** FactoryWatchLineDto: one production line's hall. */
export interface FactoryWatchLine {
  name: string;
  /** The hall's place in the order, from 0; the player picks a hue from it. */
  index: number;
  gx: number;
  gy: number;
  width: number;
  height: number;
}

/** FactoryWatchCharacterDto: the character that stands for one seat. */
export interface FactoryWatchCharacter {
  /** The seat it stands for. Not for display. */
  seat: string;
  name: string;
  /** "#rrggbb", decided on the Gateway. */
  colour: string;
  station: string;
  homeGx: number;
  homeGy: number;
}

/** FactoryWatchStepDto: one step on the timeline. */
export interface FactoryWatchStep {
  key: string;
  kind: FactoryWatchStepKind;
  /** The seat that acted. Not for display. */
  seat: string;
  utc: string;
  toUtc: string | null;
  /** The time as the Activity tab writes it. */
  time: string;
  /** The sentence to show. */
  words: string;
  /** How many empty checks the step stands for: at least 1 for "checked-nothing", 0 otherwise. */
  emptyChecks: number;
  /** True only for a hiccup in the owner's view: the player draws the red lamp and the sparking wires only then. A
   * hiccup with alarm false is drawn as a calm stumble with no red. Every audience sends it on every step. */
  alarm: boolean;
  /** True only for a contributor step that is not their own: drawn as a plain crate with no words. False for every
   * owner step. */
  plain: boolean;
  /** Contributor audience: true when the step is the contributor's own work. Always false in the owner audience. Not
   * read by the player, which draws from plain. */
  mine: boolean;
}

// The exact key list of each type, for the contract test. Every audience sends every field. A field missing from a
// list is a compile error naming it (the `true` below stops type-checking), so these lists ARE the types.
function keys<T>() {
  return <K extends readonly (keyof T)[]>(
    list: K,
    complete: Exclude<keyof T, K[number]> extends never ? true : { missing: Exclude<keyof T, K[number]> },
  ): readonly (keyof T)[] => {
    void complete;
    return list;
  };
}

export const FACTORY_WATCH_KEYS = {
  view: keys<FactoryWatchView>()(
    ["factoryId", "title", "layout", "floorWidth", "floorHeight", "stations", "lines", "characters", "steps", "window", "emptyText", "truncatedText", "notes", "replayText"] as const,
    true,
  ),
  station: keys<FactoryWatchStation>()(["id", "label", "sub", "isBoss", "gx", "gy", "width", "height", "line"] as const, true),
  line: keys<FactoryWatchLine>()(["name", "index", "gx", "gy", "width", "height"] as const, true),
  character: keys<FactoryWatchCharacter>()(["seat", "name", "colour", "station", "homeGx", "homeGy"] as const, true),
  step: keys<FactoryWatchStep>()(["key", "kind", "seat", "utc", "toUtc", "time", "words", "mine", "alarm", "plain", "emptyChecks"] as const, true),
  window: keys<FactoryWindow>()(["key", "label", "fromUtc", "toUtc", "fromLocal", "toLocal", "choices"] as const, true),
};
