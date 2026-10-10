import { describe, expect, it } from "vitest";
import { functionBodies, reportingFunctions, scanSource, type ErrorDisplaySite } from "./errorDisplaySites.testkit";

// The scanner's own rules, each on a small source. The shells' scan tests run it over the real code; these prove
// that what it counts as a site, as reported and as exempt is what errorDisplaySites.testkit.ts says it is.

const REPORTING = new Set(["describeAndReport"]);

function scan(src: string, reporting = REPORTING): ErrorDisplaySite[] {
  return scanSource("fixture.tsx", src, reporting);
}

function bad(src: string, reporting = REPORTING): ErrorDisplaySite[] {
  return scan(src, reporting).filter((s) => !s.reported && s.exemptReason === undefined);
}

describe("errorDisplaySites scanner", () => {
  it("scanSource_UnreportedSetError_IsAFailingSiteWithItsLine", () => {
    const src = ["function Page() {", "  try { load(); } catch (err) {", "    setError(err.message);", "  }", "}"].join("\n");

    const sites = bad(src);

    expect(sites).toHaveLength(1);
    expect(sites[0]).toMatchObject({ file: "fixture.tsx", line: 3, kind: "setter", name: "setError", value: "err.message" });
    expect(sites[0].problem).toContain("describeAndReport");
  });

  it("scanSource_SetErrorThroughDescribeAndReport_IsReported", () => {
    const sites = scan(`setError(describeAndReport("page", "load the page", err, { sessionId }));`);

    expect(sites).toHaveLength(1);
    expect(sites[0].reported).toBe(true);
  });

  it("scanSource_ClearingCalls_AreNotSites", () => {
    expect(scan(`setError(null); setLoadError(false); setNoteError(""); setError(undefined);`)).toEqual([]);
  });

  it("scanSource_AnyErrorNamedSetter_IsASite", () => {
    const names = bad(`setLoadError(x); setCreateError("x"); setFailure(y); setProblem(z); setReportRefusal(w);`).map((s) => s.name);

    expect(names).toEqual(["setLoadError", "setCreateError", "setFailure", "setProblem", "setReportRefusal"]);
  });

  it("scanSource_ExemptionWithAReason_IsExemptAndKeepsTheReason", () => {
    const src = ["// error-report-exempt: the user left the path empty; nothing failed", `setCreateError("Enter a path.");`].join("\n");

    const [site] = scan(src);

    expect(site.reported).toBe(false);
    expect(site.exemptReason).toBe("the user left the path empty; nothing failed");
  });

  it("scanSource_ExemptionWithoutAReason_IsNoExemption", () => {
    const [site] = bad(`setCreateError("Enter a path."); // error-report-exempt: input`);

    expect(site.problem).toContain("written reason");
  });

  it("scanSource_ExemptionTwoLinesAbove_DoesNotCount", () => {
    const src = ["// error-report-exempt: the user left the path empty; nothing failed", "", `setCreateError("Enter a path.");`].join("\n");

    expect(bad(src)).toHaveLength(1);
  });

  it("scanSource_VariableLastAssignedFromAReportingCall_IsReported", () => {
    const src = `const message = describeAndReport("p", "load", err);\nsetError(message);`;

    expect(scan(src)[0].reported).toBe(true);
  });

  it("scanSource_VariableReassignedAfterTheReport_IsNotReported", () => {
    const src = `let message = describeAndReport("p", "load", err);\nmessage = err.message;\nsetError(message);`;

    expect(bad(src)).toHaveLength(1);
  });

  it("scanSource_WrapperReturningAReportingCall_CountsAsReporting", () => {
    const lib = `export function loadFailed(err: unknown): string {\n  return describeAndReport("p", "load", err);\n}`;
    const reporting = reportingFunctions([lib]);

    expect(reporting.has("loadFailed")).toBe(true);
    expect(scan(`setError(loadFailed(err));`, reporting)[0].reported).toBe(true);
  });

  it("reportingFunctions_FunctionThatOnlyReportsInsideACallback_IsNotReporting", () => {
    const lib = `function Big() {\n  const onClick = () => { return describeAndReport("p", "a", e); };\n  return <div />;\n}`;

    expect(reportingFunctions([lib]).has("Big")).toBe(false);
  });

  it("scanSource_CatchSetterShowingTheCaughtError_IsASiteWhateverItsName", () => {
    const src = `try { save(); } catch (e) {\n  setMsg(\`Could not save: \${errText(e)}\`);\n  setBusy(false);\n}`;

    const sites = bad(src);

    expect(sites.map((s) => [s.kind, s.name])).toEqual([["catch", "setMsg"]]);
  });

  it("scanSource_PromiseCatchHandler_IsScannedLikeACatchBlock", () => {
    const sites = bad(`load().catch((err) => setNote(err.message));`);

    expect(sites.map((s) => [s.kind, s.name])).toEqual([["catch", "setNote"]]);
  });

  it("scanSource_ErrorCallbackWithAValue_IsASite", () => {
    const sites = scan(`onError(err.message);\nhooks.onError?.(EMPTY_MESSAGE);\nonError(describeAndReport("p", "a", err));\nonError(null);`);

    expect(sites.map((s) => [s.line, s.kind, s.reported])).toEqual([
      [1, "callback", false],
      [2, "callback", false],
      [3, "callback", true],
    ]);
  });

  it("scanSource_ErrorCallbackPassedStraightToASetter_IsNotASecondSite", () => {
    expect(bad(`<Controls onError={(message) => setError(message)} />`)).toEqual([]);
  });

  it("scanSource_SetterWrapperParameter_IsNotASiteButItsCallersAre", () => {
    const src = [
      "const setActionError = useCallback((message: string) => {",
      "  setError(message);",
      "}, []);",
      "setActionError(err.message);",
    ].join("\n");

    expect(bad(src).map((s) => [s.line, s.name])).toEqual([[4, "setActionError"]]);
  });

  it("scanSource_TypeDeclarationOfACallback_IsNotASite", () => {
    expect(scan(`interface P {\n  onError(message: string): void;\n}`)).toEqual([]);
  });

  it("scanSource_SetterOfStateShownInAnAlert_IsASiteWhateverItsName", () => {
    const src = [
      "const [note, setNote] = useState<string | null>(null);",
      `setNote("Could not open the file.");`,
      "return <p role=\"alert\">{note}</p>;",
    ].join("\n");

    expect(bad(src).map((s) => [s.line, s.name])).toEqual([[2, "setNote"]]);
  });

  it("scanSource_StateShownThroughAMember_OnlyCallsSettingThatMemberAreSites", () => {
    const src = [
      "const [load, setLoad] = useState<Load>({ kind: \"loading\" });",
      `setLoad({ kind: "loaded", answer });`,
      `setLoad({ kind: "error", message: err.message });`,
      "return <p role=\"alert\">{load.message}</p>;",
    ].join("\n");

    expect(bad(src).map((s) => s.line)).toEqual([3]);
  });

  it("scanSource_AlertShowingStateSetInTheFile_IsNotAnAlertSite", () => {
    const src = "const [error, setError] = useState<string | null>(null);\nreturn <div role=\"alert\">{error}</div>;";

    expect(scan(src)).toEqual([]);
  });

  it("scanSource_AlertShowingAHookValue_NeedsAReporterNamed", () => {
    const src = "const manage = useSessionManage();\nreturn <div role=\"alert\">{manage.error}</div>;";

    const [site] = bad(src);

    expect(site).toMatchObject({ kind: "alert", line: 2 });
    expect(site.problem).toContain("error-reported-by");
  });

  it("scanSource_AlertNamingAFunctionThatReports_IsReported", () => {
    const producers = functionBodies(`function useSessionManage() {\n  setError(describeAndReport("s", "stop the session", err));\n}`);
    const src = "{/* error-reported-by: useSessionManage */}\n<div role=\"alert\">{manage.error}</div>";

    const [site] = scanSource("fixture.tsx", src, REPORTING, producers);

    expect(site.reported).toBe(true);
  });

  it("scanSource_AlertNamingAFunctionThatDoesNotReport_Fails", () => {
    const producers = functionBodies(`function useSessionManage() {\n  setError(err.message);\n}`);
    const src = "{/* error-reported-by: useSessionManage */}\n<div role=\"alert\">{manage.error}</div>";

    const [site] = scanSource("fixture.tsx", src, REPORTING, producers);

    expect(site.reported).toBe(false);
    expect(site.problem).toContain("never calls describeAndReport");
  });

  it("scanSource_AlertNamingAnUnknownFunction_Fails", () => {
    const src = "{/* error-reported-by: noSuchThing */}\n<div role=\"alert\">{manage.error}</div>";

    expect(bad(src)[0].problem).toContain("not a function in the scanned code");
  });

  it("scanSource_AlertWithFixedText_IsAnAlertSite", () => {
    const [site] = bad(`{!ok && <div className="banner" role="alert">This browser cannot store recordings.</div>}`);

    expect(site.kind).toBe("alert");
    expect(site.problem).toContain("fixed text");
  });

  it("scanSource_StringsAndTemplatesWithBrackets_DoNotBreakTheArgument", () => {
    const [site] = bad("setError(`Could not read ) the ${name ?? \"(none)\"} file`);");

    expect(site.value).toBe("`Could not read ) the ${name ?? \"(none)\"} file`");
  });

  it("scanSource_UpdaterThatOnlyRemovesAnEntry_IsNotASite", () => {
    expect(scan(`setErrors((e) => { const { [id]: _gone, ...rest } = e; return rest; });`)).toEqual([]);
  });

  it("scanSource_UpdaterThatAddsText_IsASite", () => {
    expect(bad(`setErrors((e) => ({ ...e, [id]: err.message }));`)).toHaveLength(1);
  });
});
