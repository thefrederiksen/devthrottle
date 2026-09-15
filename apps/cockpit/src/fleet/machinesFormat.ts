import type { FleetMachine, FleetMachines } from "@devthrottle/client-core/fleet/machinesClient";

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
