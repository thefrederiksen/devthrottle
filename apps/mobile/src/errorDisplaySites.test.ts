import { describe, expect, it } from "vitest";
import {
  describeSite,
  scanErrorDisplaySites,
  unreportedSites,
} from "@devthrottle/client-core/errors/errorDisplaySites.testkit";

// EVERY ERROR THE PHONE SHOWS IS ALSO LOGGED CENTRALLY (the Error Logging mission, issue #3675).
//
// The display sites are DERIVED from the source (errorDisplaySites.testkit.ts): every error-state setter, every
// setter shown in a role="alert" element, every setter in a catch that shows the caught error, every error
// callback, every store error field and every alert whose content is not state set in its own file - across the
// phone app and the client-core library it is built from (the Settings cards live there, one copy for both shells). Each
// one must report what it shows (describeAndReport, reportShownError, or a function that returns one of them), or
// carry a written exemption at the site. A new site that does neither fails here by file and line.
//
// A run that reads no sites fails as a broken scanner, never passes as clean code.

const ROOTS = ["apps/mobile/src", "packages/client-core/src"];

describe("every error the phone and client-core show is reported", () => {
  it("errorDisplaySites_PhoneAndClientCore_EverySiteReportsOrIsExempt", () => {
    const scan = scanErrorDisplaySites(ROOTS);
    const reported = scan.sites.filter((s) => s.reported);
    const exempt = scan.sites.filter((s) => s.exemptReason !== undefined);

    console.log(
      `[error display scan] ${ROOTS.join(" + ")}: read ${scan.filesRead} files and ${scan.sites.length} display sites - ` +
        `${reported.length} reported, ${exempt.length} exempt, ${unreportedSites(scan).length} unreported`,
    );
    for (const site of exempt) console.log(`[error display scan] exempt ${site.file}:${site.line} - ${site.exemptReason}`);

    expect(scan.filesRead, "the scan read no source files - the roots are wrong").toBeGreaterThan(0);
    expect(scan.sites.length, "the scan found no display sites - the scanner is broken, not the code clean").toBeGreaterThan(0);
    expect(unreportedSites(scan).map(describeSite)).toEqual([]);
  });
});
