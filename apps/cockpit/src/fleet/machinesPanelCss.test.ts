import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

// Regression test for the Directors page layout. The Machines table's machine row used the class
// `fm-machine`, which the Fleet Manager settings tab also uses for its machine picker cards. The settings
// stylesheet is loaded on every Cockpit page, so its `.fm-machine { display: flex; border-radius ... }`
// turned each machine <tr> into a floating card whose cells no longer sat under the table's columns.
// Both stylesheets use the `fm-` prefix, so no class may be defined in both.

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), "utf8");
}

function fmClasses(css: string): Set<string> {
  return new Set(css.match(/\.fm-[a-z0-9-]+/g) ?? []);
}

describe("Directors page stylesheet", () => {
  it("FmClasses_FleetAndSettingsStylesheets_ShareNoClassName", () => {
    const fleet = fmClasses(read("./fleet.css"));
    const settings = fmClasses(read("../../../../packages/client-core/src/settings/settings.css"));

    const shared = [...fleet].filter((name) => settings.has(name));

    expect(fleet.size).toBeGreaterThan(0);
    expect(settings.size).toBeGreaterThan(0);
    expect(shared).toEqual([]);
  });

  it("MachinesPanel_MachineRow_DoesNotUseTheSettingsCardClass", () => {
    const panel = read("./MachinesPanel.tsx");

    expect(panel).toContain('<tr className="fm-machine-row">');
    expect(panel).not.toMatch(/className="fm-machine"/);
  });
});
