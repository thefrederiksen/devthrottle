import { describe, expect, it } from "vitest";
import {
  describeSite,
  scanErrorDisplaySites,
  unreportedSites,
} from "@devthrottle/client-core/errors/errorDisplaySites.testkit";

// Every place the Cockpit turns an error into on-screen text is also reported to the Gateway (the Error Logging
// mission, issue #3675). The sites are DERIVED from apps/cockpit/src by the shared scanner - never a hand-kept list -
// so a new setError, a new error callback or a new role="alert" that shows an error without describeAndReport fails
// here by file and line. The only way past is a written reason at the site (error-report-exempt: <reason>).

const COCKPIT = "apps/cockpit/src";

describe("Cockpit error display sites", () => {
  it("CockpitSources_EveryErrorDisplaySite_IsReportedOrCarriesAWrittenExemption", () => {
    const scan = scanErrorDisplaySites([COCKPIT]);
    const exempt = scan.sites.filter((s) => !s.reported && s.exemptReason !== undefined);
    const failing = unreportedSites(scan);

    // The counts are printed on every run, so a run that read nothing cannot pass as a clean one.
    console.log(
      `[cockpit error display scan] files read: ${scan.filesRead}, sites read: ${scan.sites.length}, ` +
        `reported: ${scan.sites.filter((s) => s.reported).length}, exempt: ${exempt.length}, unreported: ${failing.length}`,
    );
    for (const site of exempt) console.log(`[cockpit error display scan] exempt ${site.file}:${site.line} - ${site.exemptReason}`);

    expect(scan.filesRead, "the scan read no Cockpit source files - the path is wrong, not the code clean").toBeGreaterThan(0);
    expect(scan.sites.length, "the scan found no error display sites - the scanner is broken, not the code clean").toBeGreaterThan(0);
    expect(failing.map(describeSite), "error display sites that neither report nor carry a written exemption").toEqual([]);
  });
});
