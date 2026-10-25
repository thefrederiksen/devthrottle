import { describe, expect, it } from "vitest";
import type { FleetMachine, FleetMachines } from "@devthrottle/client-core/fleet/machinesClient";
import { machineOf, outcomeStillShown, toneClass, versionStateByDirector } from "./machinesFormat";

const action = { offered: false, label: "", reason: null };
const version = { label: "", tone: "idle" as const, behind: false };

function machine(name: string, directorIds: string[]): FleetMachine {
  return {
    machine: name,
    reach: "Connected",
    reachLabel: "Launcher connected",
    reachTone: "ok",
    launcherVersionState: version,
    canReportUpdateStatus: true,
    upToDate: false,
    update: action,
    restart: action,
    start: action,
    directors: directorIds.map((id) => ({
      directorId: id,
      name: id,
      sessions: 0,
      stateLabel: "Running",
      stateTone: "ok",
      versionState: { label: "behind", tone: "warn", behind: true },
    })),
  };
}

const view: FleetMachines = {
  newestRelease: { label: "2.1.4", version: "2.1.4" },
  highlights: [],
  machines: [machine("SORENLAPTOP", ["136AF82D"]), machine("SOREN_NORTH", ["a41c09e2"])],
};

describe("machinesFormat", () => {
  it("draws each Gateway tone with its status class", () => {
    expect(toneClass("ok")).toBe("dstat-ok");
    expect(toneClass("warn")).toBe("dstat-warn");
    expect(toneClass("bad")).toBe("dstat-err");
    expect(toneClass("idle")).toBe("dstat-idle");
  });

  it("finds a Director's machine regardless of case", () => {
    expect(machineOf(view, "sorenlaptop")?.machine).toBe("SORENLAPTOP");
    expect(machineOf(view, "NOPE")).toBeNull();
    expect(machineOf(null, "SORENLAPTOP")).toBeNull();
  });

  it("keys version states by lower-cased Director id", () => {
    const map = versionStateByDirector(view);
    expect(map.get("136af82d")?.behind).toBe(true);
    expect(map.size).toBe(2);
  });

  // Issue #4423: Update now worked, both versions came back current, and the red "too old" answer stayed beside the
  // green row. The update's answer goes once the Gateway says the machine is up to date.
  it("drops an update's answer once the Gateway says the machine is up to date", () => {
    const failed = { text: "the launcher is too old", tone: "bad" as const, kind: "update" as const };
    const behind = machine("STUDIO_MAC", ["d1"]);
    const current = { ...behind, upToDate: true };

    expect(outcomeStillShown(failed, behind)).toBe(true);
    expect(outcomeStillShown(failed, current)).toBe(false);
  });

  it("keeps a restart's or a start's answer whatever the versions say", () => {
    const current = { ...machine("STUDIO_MAC", ["d1"]), upToDate: true };

    expect(outcomeStillShown({ text: "The Director restarted.", tone: "ok", kind: "restart" }, current)).toBe(true);
    expect(outcomeStillShown({ text: "asked to start", tone: "ok", kind: "start" }, current)).toBe(true);
    expect(outcomeStillShown(null, current)).toBe(false);
  });
});
