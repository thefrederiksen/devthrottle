import type { FleetMachine, FleetMachines } from "@devthrottle/client-core/fleet/machinesClient";
import type { ActionOutcome } from "./MachineActions";

// Layout helpers for the machines view (fleet maintenance, devthrottle_internal#2026). They decide HOW a Gateway
// verdict is drawn, never what it means: the tone, the label and whether an action is offered all arrive folded.

/** The status class a Gateway tone is drawn with. An unrecognised tone from a newer Gateway draws as idle. */
export function toneClass(tone: string): string {
  switch (tone) {
    case "ok":
      return "dstat-ok";
    case "warn":
      return "dstat-warn";
    case "bad":
      return "dstat-err";
    default:
      return "dstat-idle";
  }
}

/** The machine a Director runs on, by machine name as the Gateway groups it (case-insensitive). */
export function machineOf(view: FleetMachines | null, machineName: string): FleetMachine | null {
  if (view === null) return null;
  const name = machineName.trim().toLowerCase();
  return view.machines.find((m) => m.machine.toLowerCase() === name) ?? null;
}

/** Every Director's version state in the view, keyed by lower-cased Director id. */
export function versionStateByDirector(view: FleetMachines | null) {
  const map = new Map<string, FleetMachine["directors"][number]["versionState"]>();
  if (view === null) return map;
  for (const m of view.machines) for (const d of m.directors) map.set(d.directorId.toLowerCase(), d.versionState);
  return map;
}

/** Whether an action's answer is still worth showing. An update's answer is dropped once the Gateway's machine view
 *  says the machine is up to date (issue #4423): the answer was about a moment that has passed, and a red sentence
 *  beside a current row reads as a failed update. The verdict is the Gateway's `upToDate`; this only applies it. */
export function outcomeStillShown(outcome: ActionOutcome | null | undefined, machine: FleetMachine | null): boolean {
  if (outcome === null || outcome === undefined) return false;
  return !(outcome.kind === "update" && machine?.upToDate === true);
}

/** How the Machines table is ordered: as the Gateway sent it, or by Spare. */
export type SpareOrder = "none" | "most" | "least";

/** The next order when the Spare header is pressed: most room first, then least, then the Gateway's own order. */
export function nextSpareOrder(order: SpareOrder): SpareOrder {
  return order === "none" ? "most" : order === "most" ? "least" : "none";
}

/** The machines ordered by the Gateway's Spare figure. A machine whose spare capacity is not known (no `spareBytes`)
 *  goes LAST in both directions - it is never ranked as if it had no room, nor as if it had the most. Ties keep the
 *  Gateway's order. This orders rows; it decides nothing about them. */
export function sortBySpare(machines: FleetMachine[], order: SpareOrder): FleetMachine[] {
  if (order === "none") return machines;
  const known = machines.filter((m) => typeof m.capacity.spareBytes === "number");
  const unknown = machines.filter((m) => typeof m.capacity.spareBytes !== "number");
  const sign = order === "most" ? -1 : 1;
  const ranked = known
    .map((m, i) => ({ m, i }))
    .sort((a, b) => sign * ((a.m.capacity.spareBytes as number) - (b.m.capacity.spareBytes as number)) || a.i - b.i)
    .map((e) => e.m);
  return [...ranked, ...unknown];
}
