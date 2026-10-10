import { describe, expect, it } from "vitest";
import { alertComponents, duplicateReportingNames, functionBodies, reportingFunctions, scanSource, type ErrorDisplaySite } from "./errorDisplaySites.testkit";

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

  it("scanSource_ElementErrorHandlerWrittenInPlace_IsASite", () => {
    const sites = bad(`<img src={url} onError={() => setFailed(true)} />`);

    expect(sites.map((s) => [s.kind, s.name])).toEqual([["callback", "onError="]]);
  });

  it("scanSource_ElementErrorHandlerThatReports_IsReported", () => {
    const [site] = scan(`<img src={url} onError={() => setFailed(reportShownError("s", "show the image", TEXT))} />`, new Set(["reportShownError"]));

    expect(site.reported).toBe(true);
  });

  it("scanSource_SetterPassedByNameAsAnErrorProp_IsNotASite", () => {
    expect(scan(`<SessionControls onError={setError} />`)).toEqual([]);
  });

  it("scanSource_ReportInsideAnOptionalCall_FailsBecauseItCanBeSkipped", () => {
    const [site] = bad(`hooks.onError?.(describeAndReport("s", "a", err));`);

    expect(site.problem).toContain("optional call");
  });

  it("scanSource_ReportedFirstThenPassedToAnOptionalCall_IsReported", () => {
    const src = `const shown = describeAndReport("s", "a", err);\nhooks.onError?.(shown);`;

    expect(scan(src)[0].reported).toBe(true);
  });

  it("scanSource_ExemptionInAJsxCommentOnAWindowsLine_ReasonStopsAtTheComment", () => {
    const src = `<p role="alert">\r\n{/* error-report-exempt: the Gateway's own refusal, nothing failed here */}\r\n{menuBlocked}</p>`;

    expect(scan(src)[0].exemptReason).toBe("the Gateway's own refusal, nothing failed here");
  });

  it("scanSource_ClearingOneEntryOfAnErrorMap_IsNotASite", () => {
    // The step 3 rulings, R7 (LinkRequests.tsx): the rest of the map is kept and the named entry emptied.
    expect(scan(`setErrors((e) => ({ ...e, [id]: '' }));`)).toEqual([]);
    expect(scan(`setErrors((e) => ({ ...e, [a]: null, b: "" }));`)).toEqual([]);
  });

  it("scanSource_SettingOneEntryOfAnErrorMapToText_IsStillASite", () => {
    expect(bad(`setErrors((e) => ({ ...e, [id]: err.message }));`)).toHaveLength(1);
    expect(bad(`setErrors((e) => ({ ...e, [id]: '', other: "It failed." }));`)).toHaveLength(1);
  });

  it("scanSource_ErrorCallbackWithTheOriginalErrorAsASecondArgument_JudgesTheShownTextOnly", () => {
    // The host shows the first argument; the second is the original error handed over beside it.
    const passed = `const shown = describeAndReport("s", "a", err);\nhooks.onError?.(shown, err);`;
    const hidden = `onError(err.message, describeAndReport("s", "a", err));`;

    expect(scan(passed)[0].reported).toBe(true);
    expect(scan(hidden)[0].reported).toBe(false);
  });

  it("scanSource_StoreErrorField_IsASiteWhateverTheStoreIsCalled", () => {
    const src = [
      `emit({ phase: "idle", error: msg });`,
      `this.update({ loadError: gatewayErrorMessage(err) });`,
      `emit({ phase: "starting", error: null });`,
      `emit({ error: describeAndReport("s", "a", err) });`,
    ].join("\n");

    expect(scan(src).map((s) => [s.line, s.reported])).toEqual([
      [1, false],
      [2, false],
      [4, true],
    ]);
  });

  it("scanSource_AlertMarkerInsideTheElement_NamesAClassThatReports", () => {
    const producers = functionBodies(
      `export class Controller {\n  load() {\n    this.update({ loadError: describeAndReport("s", "a", err) });\n  }\n}`,
    );
    const src = `<div role="alert">\n  {/* error-reported-by: Controller */}\n  {snapshot.loadError}\n</div>`;

    const [site] = scanSource("fixture.tsx", src, REPORTING, producers);

    expect(site.reported).toBe(true);
  });

  it("scanSource_AlertMarkerNamesAModuleObjectThatReports_IsReported", () => {
    const producers = functionBodies(`export const recordingSession = {\n  async start() {\n    emit({ error: reportShownError("r", "a", "m") });\n  },\n};`);
    const src = `<div role="alert">{/* error-reported-by: recordingSession */}{error}</div>`;

    const [site] = scanSource("fixture.tsx", src, new Set(["reportShownError"]), producers);

    expect(site.reported).toBe(true);
  });

  it("reportingFunctions_ComponentWhoseCallbackReports_IsNotReporting", () => {
    const lib = `function ImageFile() {\n  return (\n    <img onError={() => setFailed(describeAndReport("s", "a", e))} />\n  );\n}`;

    expect(reportingFunctions([lib]).has("ImageFile")).toBe(false);
  });
});

describe("presentational alert components are followed to their callers (the step 3 rulings, R3)", () => {
  const BANNER = [
    `export function ErrorBanner({ message, onRetry, retryLabel = "Try again" }: ErrorBannerProps) {`,
    `  return (`,
    `    <div className="ui-error-banner" role="alert">`,
    `      <span>{message}</span>`,
    `      {onRetry !== undefined && <Button onClick={onRetry}>{retryLabel}</Button>}`,
    `    </div>`,
    `  );`,
    `}`,
  ].join("\n");
  const components = alertComponents([BANNER]);
  const scanWith = (src: string) => scanSource("fixture.tsx", src, REPORTING, functionBodies(src), components);

  it("alertComponents_AlertRenderingAProp_FindsTheComponentAndOnlyTheTextProps", () => {
    // onRetry is a handler and retryLabel has a default - a label the component supplies - so neither is the error.
    expect([...components.get("ErrorBanner")!.shown]).toEqual(["message"]);
  });

  it("scanSource_TheComponentsOwnAlert_IsNotASite", () => {
    expect(scanSource("ErrorBanner.tsx", BANNER, REPORTING, [], components)).toEqual([]);
  });

  it("scanSource_UseShowingSomethingNotReported_IsAFailingSite", () => {
    const sites = scanWith(`return <ErrorBanner message={frame.error} onRetry={frame.reload} />;`);

    expect(sites).toHaveLength(1);
    expect(sites[0]).toMatchObject({ kind: "alert", name: "<ErrorBanner>", reported: false });
    expect(sites[0].problem).toContain("frame");
  });

  it("scanSource_UseShowingStateInsideATemplate_MakesItsSetterTheSite", () => {
    const src = [
      `const [error, setError] = useState<string | null>(null);`,
      `setError(describeAndReport("s", "a", err));`,
      "return <ErrorBanner message={`Could not load your account: ${error}`} />;",
    ].join("\n");

    expect(scanWith(src)).toEqual([expect.objectContaining({ kind: "setter", name: "setError", reported: true })]);
  });

  it("scanSource_UseShowingFixedText_IsAFailingSite", () => {
    expect(scanWith(`return <ErrorBanner message="The Gateway did not say how to start it." />;`)[0].reported).toBe(false);
  });

  it("scanSource_UseShowingAReportingCall_IsReported", () => {
    expect(scanWith(`return <ErrorBanner message={describeAndReport("s", "a", err)} />;`)[0].reported).toBe(true);
  });

  it("scanSource_UseShowingLocalState_MakesItsSetterTheSite", () => {
    const src = [
      `const [actionError, setActionError] = useState<string | null>(null);`,
      `setActionError(gatewayErrorMessage(err, "join the team"));`,
      `return actionError !== null && <ErrorBanner message={actionError} />;`,
    ].join("\n");

    const sites = scanWith(src);
    expect(sites).toHaveLength(1);
    expect(sites[0]).toMatchObject({ kind: "setter", name: "setActionError", reported: false });
  });

  it("alertComponents_ComponentForwardingItsPropToABanner_IsFollowedToo", () => {
    const panel = `function Panel({ error }: P) {\n  return <section><ErrorBanner message={error} /></section>;\n}`;
    const both = alertComponents([BANNER, panel]);

    expect([...both.get("Panel")!.shown]).toEqual(["error"]);
    expect(scanSource("Panel.tsx", panel, REPORTING, [], both)).toEqual([]);
    expect(scanSource("page.tsx", `<Panel error={load.error} />`, REPORTING, [], both)[0].reported).toBe(false);
  });

  it("alertComponents_AlertThatNamesItsReporter_IsNotFollowed", () => {
    const bar = `function AppBar({ manage }: P) {\n  return <div role="alert">{/* error-reported-by: useManage */}{manage.error}</div>;\n}`;

    expect(alertComponents([bar]).has("AppBar")).toBe(false);
  });
});

describe("text written into a terminal (the 3c review, finding 2)", () => {
  it("scanSource_StatusLineNamingAFailure_IsAFailingSite", () => {
    const sites = bad(`this.statusLine("cannot open stream: " + (err instanceof Error ? err.message : String(err)));`);

    expect(sites).toHaveLength(1);
    expect(sites[0]).toMatchObject({ kind: "terminal", name: "statusLine" });
  });

  it("scanSource_TermWriteNamingAFailure_IsAFailingSite", () => {
    expect(bad(`term.write("[cannot open stream: " + err.message + "]");`)).toHaveLength(1);
  });

  it("scanSource_FailureTextBuiltInAVariable_IsStillASite", () => {
    expect(bad(`const shown = "stream is down after 30 attempts";\nthis.statusLine(shown);`)).toHaveLength(1);
  });

  it("scanSource_ReportedTerminalLine_IsReported", () => {
    const src = `this.statusLine(reportShownError("terminal", "open the terminal stream", "cannot open stream", ctx, err));`;

    expect(scan(src, new Set(["describeAndReport", "reportShownError"]))[0].reported).toBe(true);
  });

  it("scanSource_ProgressLinesAndPtyBytes_AreNotSites", () => {
    const src = [
      `this.statusLine("connecting via gateway " + wsHost + "...");`,
      `this.statusLine("attempt " + (this.attempts + 1) + "...");`,
      `t.write(new Uint8Array(ev.data as ArrayBuffer));`,
    ].join("\n");

    expect(scan(src)).toEqual([]);
  });
});

describe("reporting names defined more than once (the 3c review, finding 3)", () => {
  it("duplicateReportingNames_OneReportingAndOnePlainDefinition_NamesBothFiles", () => {
    const texts = new Map([
      ["/repo/a/settings.tsx", `export function errText(e, action) { return describeAndReport("s", action, e); }`],
      ["/repo/b/send.ts", `function errText(err) { return err.message; }`],
    ]);
    const reporting = reportingFunctions([...texts.values()]);

    expect(duplicateReportingNames(texts, reporting, "/repo")).toEqual(["errText in a/settings.tsx, b/send.ts"]);
  });

  it("duplicateReportingNames_PlainHelperUnderAnotherName_IsClean", () => {
    const texts = new Map([
      ["/repo/a/settings.tsx", `export function errText(e, action) { return describeAndReport("s", action, e); }`],
      ["/repo/b/send.ts", `function messageOf(err) { return err.message; }`],
    ]);

    expect(duplicateReportingNames(texts, reportingFunctions([...texts.values()]), "/repo")).toEqual([]);
  });
});

describe("store writes and reporting wrappers (the 3c review, finding 4)", () => {
  it("scanSource_PublishWithAnErrorField_IsASite", () => {
    const sites = bad(`try { await save(); } catch (err) {\n  publishDictationStatus({ phase: "failed", error: err.message });\n}`);

    expect(sites.some((s) => s.name === "publishDictationStatus({ error })")).toBe(true);
  });

  it("scanSource_PublishWithAReportedError_IsReported", () => {
    expect(scan(`publishDictationStatus({ phase: "failed", error: describeAndReport("s", "a", err) });`)[0].reported).toBe(true);
  });

  it("reportingFunctions_WrapperReturningAVariableAssignedFromAReport_CountsAsReporting", () => {
    const src = `function failIt(message) {\n  const shown = describeAndReport("s", "a", message);\n  publish({ error: shown });\n  return shown;\n}`;

    expect(reportingFunctions([src]).has("failIt")).toBe(true);
  });
});

describe("an updater carrying a variable into an error member (the 3c review, finding 5)", () => {
  it("scanSource_UpdaterSettingTheErrorMemberToAVariable_IsASite", () => {
    const src = [
      `const [load, setLoad] = useState({ error: null });`,
      `setLoad((s) => ({ ...s, error: msg }));`,
      `return <div role="alert">{load.error}</div>;`,
    ].join("\n");

    expect(bad(src).map((s) => s.name)).toEqual(["setLoad"]);
  });

  it("scanSource_UpdaterClearingTheErrorMember_IsNotASite", () => {
    const src = [
      `const [load, setLoad] = useState({ error: null });`,
      `setLoad((s) => ({ ...s, error: null }));`,
      `return <div role="alert">{load.error}</div>;`,
    ].join("\n");

    expect(scan(src)).toEqual([]);
  });
});

describe("a terminal write's variable is resolved where it is written (the 3c re-review, weakness H)", () => {
  it("scanSource_LaterVariableNamingAFailure_IsASiteThoughAnEarlierOneIsProgress", () => {
    const src = [
      `function connect() {\n  const shown = "connecting via gateway...";\n  this.statusLine(shown);\n}`,
      `function fail() {\n  const shown = "cannot open stream";\n  this.statusLine(shown);\n}`,
    ].join("\n");

    expect(bad(src).map((s) => s.line)).toEqual([7]);
  });
});
