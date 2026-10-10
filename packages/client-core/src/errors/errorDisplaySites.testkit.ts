// TEST SUPPORT ONLY - never imported by product code (it reads source files from disk).
//
// Every place a browser shell turns an error into on-screen text, DERIVED FROM THE CODE (the Error Logging
// mission, issue #3675). The mission's rule is that an error a user sees is also logged centrally, and the one
// act that does both is describeAndReport (reportClientError.ts). A hand-kept list of display sites goes stale
// the day someone adds one, so the sites are worked out here from the source files themselves, and a scan test
// in each shell fails on any site that neither reports nor carries a written exemption. The phone and
// client-core scan, and the Cockpit's, are built on this one file.
//
// WHAT IS A DISPLAY SITE (a "site"):
//
//   setter    A call of an error-state setter with a value: setError(...), setLoadError(...), any set...Error,
//             set...Failure, set...Problem or set...Refusal. Also any setter whose state is rendered inside a
//             role="alert" element in the same file (`const [note, setNote] = useState` and `{note}` in an
//             alert make setNote a display setter). And a store's error field set through emit, set..., update..., publish...
//             or patch...: `emit({ phase: "idle", error: msg })`. Clearing calls - null, undefined, false, "" - are
//             not sites.
//   catch     Inside a catch block or a .catch(...) handler, a call of any setter whose value uses the caught
//             error: `catch (err) { setStatus(err.message) }` shows the error whatever the setter is called.
//   callback  A call of an error callback with a value: onError(...), hooks.onError?.(...), onSomethingError(...).
//             The callback hands the text to a host that displays it, so the text must already be reported.
//             Also an element's own error handler written in place: `<img onError={() => setFailed(true)} />`.
//   alert     A role="alert" element whose content is NOT state set in the same file - a prop, a hook's field,
//             or fixed text. The scan cannot follow the value to where it was made, so the element must name
//             the function that reports it (see the markers below), and the scan checks that function reports.
//   terminal  Text written into a terminal that names a failure: `this.statusLine("cannot open stream: ...")`,
//             `term.write("[stream closed: ...]")`. PTY bytes and progress lines ("connecting...") are not sites.
//
// WHAT COUNTS AS REPORTED:
//
//   - The value contains a call of a REPORTING FUNCTION: describeAndReport, or reportShownError (its sibling for a
//     failure the client found with no error object), or any function whose returned value is a reporting call (followed until nothing new is found), so a wrapper such as
//     `function loadFailed(err) { return describeAndReport(...) }` reports too.
//   - The value is a variable assigned from a reporting call in the same file:
//     `const message = describeAndReport(...); setError(message);`.
//   - The value is the parameter of an error callback or an error setter being passed straight through:
//     `onError={(message) => setError(message)}`, `const setActionError = useCallback((message) => setError(message))`
//     - the sites are the callers of that callback or setter, each scanned on its own.
//
// THE ONLY TWO MARKERS, written as a comment on the site's line or on the line directly above it (an alert's
// marker may also sit inside the element, as a {/* ... */} comment):
//
//   error-report-exempt: <reason>   An input check the user caused (an empty field, a bad format) - nothing
//                                   failed, so there is nothing to log. The reason is required and is printed.
//   error-reported-by: <function>   On an alert site only: the function (a hook, a helper) that reports the
//                                   value this element shows. The scan checks that function exists in the
//                                   scanned code and calls a reporting function; a name that does not is a
//                                   failure, never a pass.
//
// A regex scan, not a type checker: it is written for our own source and errs towards counting a site.

import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";

export type ErrorDisplaySiteKind = "setter" | "catch" | "callback" | "alert" | "terminal";

export interface ErrorDisplaySite {
  /** Repository-relative path, forward slashes. */
  file: string;
  line: number;
  kind: ErrorDisplaySiteKind;
  /** The setter or callback called, or "role=alert". */
  name: string;
  /** The value shown (the call's argument, or the alert's content), whitespace collapsed. */
  value: string;
  /** True when the value is reported. */
  reported: boolean;
  /** The written reason, when the site is exempt. */
  exemptReason?: string;
  /** Why an unreported site failed, for the failure message. */
  problem?: string;
}

export interface ErrorDisplayScan {
  /** How many source files were read. */
  filesRead: number;
  sites: ErrorDisplaySite[];
  /** Every function name that counts as reporting. */
  reportingFunctions: Set<string>;
}

/** The show-and-report act for a caught error. */
export const REPORTING_ROOT = "describeAndReport";
/** The two functions that show and report in one act (reportClientError.ts). Every reporting function leads back
 *  to one of them. */
export const REPORTING_ROOTS = [REPORTING_ROOT, "reportShownError"];

/** The repository root, from this file's own location: <root>/packages/client-core/src/errors/. */
export function repositoryRoot(): string {
  return join(__dirname, "..", "..", "..", "..");
}

/**
 * Scan the given source roots (paths relative to the repository root, e.g. "apps/mobile/src") for every
 * error display site. client-core's own source is always read for reporting functions, because the shared
 * wrappers live there, but its sites are only listed when it is one of the roots.
 */
export function scanErrorDisplaySites(roots: string[], root = repositoryRoot()): ErrorDisplayScan {
  const scanned = roots.flatMap((r) => sourceFiles(join(root, r)));
  const library = sourceFiles(join(root, "packages", "client-core", "src"));
  const all = [...new Set([...scanned, ...library])];
  const texts = new Map(all.map((f) => [f, readFileSync(f, "utf8")]));
  const reporting = reportingFunctions([...texts.values()]);
  const twice = duplicateReportingNames(texts, reporting, root);
  if (twice.length > 0) {
    throw new Error(`a reporting function is known by its name, and these names are defined more than once, so a call of the plain one would count as reported - rename one: ${twice.join("; ")}`);
  }
  const producers = functionBodies([...texts.values()].join("\n"));
  const components = alertComponents([...texts.values()]);
  const sites = scanned.flatMap((f) => scanSource(relative(root, f).split(sep).join("/"), texts.get(f)!, reporting, producers, components));
  return { filesRead: scanned.length, sites, reportingFunctions: reporting };
}

/**
 * Every reporting name defined in more than one place, as "name in fileA, fileB" (the 3c review, finding 3: one
 * reporting errText and two plain ones made every errText call count as reported). Exported for unit tests.
 */
export function duplicateReportingNames(texts: Map<string, string>, reporting: Set<string>, root = repositoryRoot()): string[] {
  const definedIn = new Map<string, string[]>();
  for (const [file, text] of texts) {
    for (const name of new Set(functionBodies(text).map((f) => f.name))) {
      if (reporting.has(name)) definedIn.set(name, [...(definedIn.get(name) ?? []), relative(root, file).split(sep).join("/")]);
    }
  }
  return [...definedIn].filter(([, files]) => files.length > 1).map(([name, files]) => `${name} in ${files.join(", ")}`);
}

/** The sites that neither report nor carry a written exemption. */
export function unreportedSites(scan: ErrorDisplayScan): ErrorDisplaySite[] {
  return scan.sites.filter((s) => !s.reported && s.exemptReason === undefined);
}

/** One line per site, for a failure message: "apps/mobile/src/pages/Home.tsx:215 setError(err.message) - ...". */
export function describeSite(site: ErrorDisplaySite): string {
  const value = site.value.length > 100 ? `${site.value.slice(0, 97)}...` : site.value;
  const shown = site.kind === "alert" ? `${site.name === "role=alert" ? `role="alert"` : site.name} showing ${value}` : `${site.name}(${value})`;
  return `${site.file}:${site.line} [${site.kind}] ${shown}${site.problem ? ` - ${site.problem}` : ""}`;
}

/**
 * Every reporting function in the given sources: describeAndReport, and every function whose returned value
 * contains a call of a reporting function, followed until nothing new is found. Exported for unit tests.
 */
export function reportingFunctions(sources: string[]): Set<string> {
  const functions = sources.flatMap((s) => functionBodies(s));
  const found = new Set(REPORTING_ROOTS);
  let grew = true;
  while (grew) {
    grew = false;
    for (const f of functions) {
      if (found.has(f.name)) continue;
      // `return describeAndReport(...)`, or `const shown = reportShownError(...); ...; return shown;`.
      const reports = (r: string) => {
        if (callsAny(r, found)) return true;
        const name = /^\s*([A-Za-z_$][\w$]*)\s*$/.exec(r)?.[1];
        const made = name && new RegExp(`(?:const|let)\\s+${escape(name)}\\s*(?::[^=]+)?=\\s*([^;]*)`).exec(f.body);
        return Boolean(made) && callsAny(made![1], found);
      };
      if (f.returned.some(reports)) {
        found.add(f.name);
        grew = true;
      }
    }
  }
  return found;
}

/**
 * The sites in one source file. Exported so the scanner's own rules can be tested on small sources.
 * `producers` are the functions an error-reported-by marker may name (defaults to this file's own), and
 * `components` the presentational alert components whose uses are alerts (defaults to this file's own).
 */
export function scanSource(
  file: string,
  src: string,
  reporting: Set<string>,
  producers: FunctionBody[] = functionBodies(src),
  components: Map<string, AlertComponent> = alertComponents([src]),
): ErrorDisplaySite[] {
  const lines = src.split("\n");
  const lineOf = (at: number) => src.slice(0, at).split("\n").length;
  const assignments = assignmentsOf(src, reporting);
  const states = useStatePairs(src);
  const sites = new Map<number, ErrorDisplaySite>();

  // A comment in the value is not the value: `err.message // the caller runs describeAndReport() first` reports nothing.
  const valueReported = (raw: string, at: number): boolean => {
    const value = withoutComments(raw);
    return callsAny(value, reporting) || lastAssignmentReports(assignments, value.trim(), at) || isPassThrough(src, at, value.trim());
  };

  const addCall = (at: number, kind: ErrorDisplaySiteKind, name: string, value: string) => {
    if (sites.has(at) || isClearing(value) || !carriesText(value, kind)) return;
    const line = lineOf(at);
    const marker = markerFor(lines, line);
    const reported = valueReported(value, at);
    sites.set(at, {
      file,
      line,
      kind,
      name,
      value: collapse(value),
      reported,
      ...(marker?.kind === "exempt" ? { exemptReason: marker.text } : {}),
      ...(reported || marker?.kind === "exempt" ? {} : { problem: problemFor(marker) }),
    });
  };

  // Display setters: by name, and by being rendered inside an alert.
  // A setter whose state an alert reads only through members (`{load.message}`) shows text only when it is
  // given one of those members; `setLoad({ kind: "loading" })` shows nothing.
  // A presentational component's own alert shows what its callers hand it: the callers' uses are the sites.
  const presenters = componentDefinitions(src).filter((d) => components.has(d.name));
  const alerts = [...alertElements(src), ...componentUses(src, components)].filter(
    (a) => !presenters.some((d) => a.at > d.start && a.at < d.end && shownProps(d, a.content).size > 0),
  );
  const displaySetters = new Map<string, Set<string> | null>();
  for (const [value, setter] of states) {
    if (ERROR_SETTER.test(setter)) displaySetters.set(setter, null);
    for (const a of alerts) {
      if (!a.roots.has(value) || displaySetters.get(setter) === null) continue;
      const members = a.roots.get(value)!;
      const known = displaySetters.get(setter);
      displaySetters.set(setter, members === null ? null : new Set([...(known ?? []), ...members]));
    }
  }
  for (const m of src.matchAll(/(?<![\w$.])(set[A-Z][\w$]*)\s*\(/g)) {
    const name = m[1];
    if (!ERROR_SETTER.test(name) && !displaySetters.has(name)) continue;
    if (isDeclaration(src, m.index!)) continue;
    const open = m.index! + m[0].length - 1;
    const close = matchingClose(src, open);
    if (close < 0) continue;
    const value = src.slice(open + 1, close);
    const members = displaySetters.get(name);
    // A flag (`setSaving(true)`) shows no error text of its own unless its name says it is an error.
    if (!ERROR_SETTER.test(name) && /^\s*true\s*$/.test(value)) continue;
    if (members && /^\s*\{/.test(value) && ![...members].some((k) => new RegExp(`(?<![\\w$.])${escape(k)}\\s*[:,}]`).test(value))) continue;
    addCall(m.index!, "setter", name, value);
  }

  // Error callbacks.
  for (const m of src.matchAll(/(?<![\w$])(on[A-Z]?[\w$]*Error)\s*(?:\?\.)?\s*\(/g)) {
    if (isDeclaration(src, m.index!)) continue;
    const open = m.index! + m[0].length - 1;
    const close = matchingClose(src, open);
    if (close < 0) continue;
    // The text shown is the FIRST argument; a second (`onError(shown, err)`, the original error) is not displayed.
    const value = src.slice(open + 1, Math.min(expressionEnd(src, open + 1), close));
    addCall(m.index!, "callback", m[1], value);
    // `hooks.onError?.(describeAndReport(...))` reports nothing when there is no callback: an optional call never
    // evaluates its argument. Report first, then hand the result over.
    const site = sites.get(m.index!);
    if (site && m[0].includes("?.") && callsAny(value, reporting)) {
      site.reported = false;
      site.problem = "the report is inside an optional call, which skips its argument when there is no callback - report first, then pass the result";
    }
  }

  // Text written into a terminal that names a failure (the 3c review, finding 2).
  for (const m of src.matchAll(/(?<![\w$])(statusLine|write)\s*\(/g)) {
    if (m[1] === "write" && src[m.index! - 1] !== ".") continue;
    if (isDeclaration(src, m.index!)) continue;
    const open = m.index! + m[0].length - 1;
    const close = matchingClose(src, open);
    if (close < 0) continue;
    const value = src.slice(open + 1, close);
    if (namesAFailure(value, src, m.index!)) addCall(m.index!, "terminal", m[1], value);
  }

  // Any setter inside a catch that shows the caught error.
  for (const c of catchBodies(src)) {
    const body = src.slice(c.start, c.end);
    const uses = new RegExp(`(?<![\\w$.])${c.variable}(?![\\w$])`);
    for (const m of body.matchAll(/(?<![\w$.])(set[A-Z][\w$]*)\s*\(/g)) {
      const at = c.start + m.index!;
      const open = at + m[0].length - 1;
      const close = matchingClose(src, open);
      if (close < 0) continue;
      const value = src.slice(open + 1, close);
      if (uses.test(value)) addCall(at, "catch", m[1], value);
    }
  }

  // An element's own load failure handled in place: `<img onError={() => setFailed(true)} />`. The handler is the
  // site unless a site already counted sits inside it (`onError={() => setError(describeAndReport(...))}`).
  // A setter passed by name (`onError={setError}`) is not a handler written here - its caller is the site.
  for (const m of src.matchAll(/(?<![\w$])(on[A-Z]?[\w$]*Error)=\{\s*(?=(?:async\s*)?\(|[A-Za-z_$][\w$]*\s*=>)/g)) {
    const open = m.index! + m[1].length + 1;
    const close = matchingClose(src, open);
    if (close < 0 || [...sites.keys()].some((at) => at > open && at < close)) continue;
    const handler = src.slice(open + 1, close);
    const line = lineOf(m.index!);
    const marker = markerFor(lines, line);
    const reported = callsAny(handler, reporting);
    sites.set(m.index!, {
      file,
      line,
      kind: "callback",
      name: `${m[1]}=`,
      value: collapse(handler),
      reported,
      ...(marker?.kind === "exempt" ? { exemptReason: marker.text } : {}),
      ...(reported || marker?.kind === "exempt" ? {} : { problem: problemFor(marker) }),
    });
  }

  // A store's error field: `emit({ phase: "idle", error: msg })`, `setState({ ...s, error: mapError(err) })`. A call
  // already counted whole by a rule above is not counted again. A store's own method counts too: `this.update({ ... })`,
  // and so does a publish to a shared status store: `publishDictationStatus({ phase: "failed", error })`.
  for (const m of src.matchAll(/(?<![\w$])(emit|set[A-Z][\w$]*|update[A-Z]?[\w$]*|patch[A-Z]?[\w$]*|publish[A-Z][\w$]*)\s*\(\s*\{/g)) {
    if (sites.has(m.index!)) continue;
    const open = m.index! + m[0].length - 1;
    const close = matchingClose(src, open);
    if (close < 0) continue;
    for (const p of objectProperties(src, open, close)) {
      if (ERROR_KEY.test(p.key)) addCall(p.at, "setter", `${m[1]}({ ${p.key} })`, p.value);
    }
  }

  // Alerts whose content is not state set in this file.
  const stateValues = new Set(states.keys());
  for (const a of alerts) {
    const outside = [...a.roots.keys()].filter((r) => !stateValues.has(r));
    if (a.roots.size > 0 && outside.length === 0) continue;
    const line = lineOf(a.at);
    const marker = markerFor(lines, line, a.content);
    // Reported where it is shown: `<ErrorBanner message={describeAndReport(...)} />`, or `message={shown}` just after
    // `const shown = describeAndReport(...)`.
    const whole = /^\s*\{\s*([A-Za-z_$][\w$]*)\s*\}\s*$/.exec(a.content)?.[1];
    let reported = callsAny(a.content, reporting) || (whole !== undefined && lastAssignmentReports(assignments, whole, a.at));
    let problem: string | undefined;
    if (reported) problem = undefined;
    else if (marker?.kind === "reported-by") {
      const producer = producers.find((p) => p.name === marker.text);
      if (!producer) problem = `error-reported-by names "${marker.text}", which is not a function in the scanned code`;
      else if (!callsAny(producer.body, reporting)) problem = `error-reported-by names "${marker.text}", which never calls ${REPORTING_ROOT}`;
      else reported = true;
    } else if (marker?.kind !== "exempt") {
      problem = `this ${a.name ?? "alert"} shows ${outside.length > 0 ? outside.join(", ") : "fixed text"}, not state set in this file; report it where it is made, or name the function that reports it with error-reported-by`;
    }
    sites.set(a.at, {
      file,
      line,
      kind: "alert",
      name: a.name ?? "role=alert",
      value: collapse(a.content),
      reported,
      ...(marker?.kind === "exempt" ? { exemptReason: marker.text } : {}),
      ...(problem ? { problem } : {}),
    });
  }

  return [...sites.values()].sort((a, b) => a.line - b.line);
}

// ---- the rules' pieces ----------------------------------------------------------------------------------

/** Words that make a line of terminal text a failure rather than progress. */
const FAILURE_WORDS = /\b(?:cannot|can't|could not|failed|failure|error|lost|down|closed|refused|unreachable|offline|denied)\b/i;

/** Whether a terminal write names a failure: its literal text says so, it shows a caught error's message, or a
 *  variable it writes was built from such text (`const shown = "cannot open stream: " + ...; statusLine(shown)`).
 *  The variable is the NEAREST declaration of that name before the write (`at`), not the first in the file - an
 *  earlier `shown` of progress text must not hide a later one naming a failure (the 3c re-review, weakness H). */
function namesAFailure(value: string, src: string, at: number): boolean {
  const literals = [...value.matchAll(/"((?:[^"\\\n]|\\.)*)"|'((?:[^'\\\n]|\\.)*)'|`((?:[^`\\]|\\.)*)`/g)].map((l) => l[1] ?? l[2] ?? l[3]);
  if (literals.some((l) => FAILURE_WORDS.test(l)) || /\.message\b/.test(value)) return true;
  const before = src.slice(0, at);
  return [...value.matchAll(/(?<![\w$.])([A-Za-z_$][\w$]*)(?![\w$]*\s*\()/g)].some(([, name]) => {
    const made = [...before.matchAll(new RegExp(`(?:const|let|var)\\s+${escape(name)}\\s*(?::[^=]+)?=\\s*([^;]*)`, "g"))].pop();
    return made !== undefined && namesAFailure(made[1], "", 0);
  });
}

/** A setter that holds an error by its name. */
const ERROR_SETTER = /^set[\w$]*(?:Error|Err|Failure|Problem|Refusal)[\w$]*$/;

/** A store field that holds an error by its name. */
const ERROR_KEY = /^(?:error|[\w$]*Error|failure|problem)$/;

/** The top-level properties of the object literal between `open` and `close`: `{ a: 1, b, ...c }` gives a and b. */
function objectProperties(src: string, open: number, close: number): { key: string; value: string; at: number }[] {
  const out: { key: string; value: string; at: number }[] = [];
  let i = open + 1;
  while (i < close) {
    while (i < close && /[\s,]/.test(src[i])) i++;
    if (i >= close) break;
    // A comment between properties - an error-report-exempt marker above `error:` - is skipped, never parsed as a
    // property: read as one, its apostrophes opened a string that swallowed the property it marks.
    if (src.startsWith("//", i)) {
      const end = src.indexOf("\n", i);
      i = end < 0 ? close : end + 1;
      continue;
    }
    if (src.startsWith("/*", i)) {
      const end = src.indexOf("*/", i + 2);
      i = end < 0 ? close : end + 2;
      continue;
    }
    if (src.startsWith("...", i)) {
      i = expressionEnd(src, i + 3) + 1;
      continue;
    }
    const key = /^(?:"([^"]*)"|'([^']*)'|([A-Za-z_$][\w$]*))\s*/.exec(src.slice(i, close));
    if (!key) {
      i = expressionEnd(src, i) + 1;
      continue;
    }
    const name = key[1] ?? key[2] ?? key[3];
    const after = i + key[0].length;
    if (src[after] === ":") {
      const start = after + 1;
      const end = Math.min(expressionEnd(src, start), close);
      out.push({ key: name, value: src.slice(start, end), at: i });
      i = end + 1;
    } else {
      // Shorthand `{ error }` - the value is the variable of that name. A method or anything else is skipped.
      if (src[after] === "," || after >= close) out.push({ key: name, value: name, at: i });
      i = expressionEnd(src, after) + 1;
    }
  }
  return out;
}

function isClearing(value: string): boolean {
  return /^\s*(?:null|undefined|false|""|''|``|\[\]|\{\})?\s*$/.test(value) || clearsMapEntries(value);
}

/** `setErrors((e) => ({ ...e, [id]: "" }))` clears one entry of an error map: an updater whose object keeps the rest
 *  (spread) and sets every entry it names to a clearing value shows nothing new (the step 3 rulings, R7). */
function clearsMapEntries(value: string): boolean {
  const head = /^\s*(?:async\s*)?\(?[\w$\s,]*\)?\s*=>\s*\(\s*\{/.exec(value);
  if (!head) return false;
  const open = head[0].length - 1;
  const close = matchingClose(value, open);
  if (close < 0 || !/^\s*\)\s*$/.test(value.slice(close + 1))) return false;
  let entries = 0;
  for (let i = open + 1; i < close; ) {
    while (i < close && /[\s,]/.test(value[i])) i++;
    if (i >= close) break;
    const end = Math.min(expressionEnd(value, i), close);
    const entry = value.slice(i, end);
    i = end + 1;
    if (/^\.\.\./.test(entry)) continue;
    const pair = /^(?:\[[^\]]*\]|[\w$]+|"[^"]*"|'[^']*')\s*:([\s\S]*)$/.exec(entry);
    if (!pair || !/^\s*(?:null|undefined|false|""|''|``)\s*$/.test(pair[1])) return false;
    entries++;
  }
  return entries > 0;
}

/** An updater function (`setErrors((e) => ...)`) only shows text when it builds some; a pure reshuffle - removing
 *  one entry - is not a display. Any other value is a display. */
function carriesText(value: string, kind: ErrorDisplaySiteKind): boolean {
  if (kind === "catch") return true;
  if (!/^\s*(?:async\s*)?\(?[\w$\s,{}:]*\)?\s*=>/.test(value)) return true;
  const body = value.replace(/^\s*(?:async\s*)?\(?[\w$\s,{}:]*\)?\s*=>/, "");
  // `(s) => ({ ...s, error: msg })` puts a variable's text into an error member (the 3c review, finding 5).
  const errorMember = [...body.matchAll(/(?<![\w$])(error|[\w$]*Error|failure|problem)\s*:\s*([^,}]+)/g)].some(
    ([, , v]) => !/^\s*(?:null|undefined|false|""|''|``)\s*$/.test(v),
  );
  return errorMember || /["'`]|\.message\b|[\w$]\s*\(/.test(body);
}

/** `function setError(` and a type's `onError(message: string): void` declare a name; they do not call it. */
function isDeclaration(src: string, at: number): boolean {
  if (/function\s*$/.test(src.slice(Math.max(0, at - 20), at))) return true;
  const open = src.indexOf("(", at);
  const close = matchingClose(src, open);
  return close > 0 && /^\s*:\s*[\w$<>[\]| ]+\s*[;,}]/.test(src.slice(close + 1, close + 60)) && /^\s*[\w$]+\s*:/.test(src.slice(open + 1, close));
}

function callsAny(text: string, names: Set<string>): boolean {
  for (const name of names) {
    if (new RegExp(`(?<![\\w$.])${escape(name)}\\s*\\(`).test(text)) return true;
  }
  return false;
}

/** Every assignment in the file, by variable: where it is and whether its value is a reporting call. */
function assignmentsOf(src: string, reporting: Set<string>): Map<string, { at: number; reports: boolean }[]> {
  const out = new Map<string, { at: number; reports: boolean }[]>();
  for (const m of src.matchAll(/(?<![\w$.])([A-Za-z_$][\w$]*)\s*=(?!=|>)\s*/g)) {
    const start = m.index! + m[0].length;
    const end = expressionEnd(src, start);
    const list = out.get(m[1]) ?? [];
    list.push({ at: m.index!, reports: callsAny(src.slice(start, end), reporting) });
    out.set(m[1], list);
  }
  return out;
}

/** A variable is reported when the LAST assignment to it before the site is a reporting call:
 *  `const message = describeAndReport(...); setError(message);`. */
function lastAssignmentReports(assignments: Map<string, { at: number; reports: boolean }[]>, value: string, at: number): boolean {
  if (!/^[A-Za-z_$][\w$]*$/.test(value)) return false;
  const before = (assignments.get(value) ?? []).filter((a) => a.at < at);
  return before.length > 0 && before[before.length - 1].reports;
}

/** `onError={(message) => setError(message)}`, `onError: (message) => { setError(message) }` and
 *  `const setActionError = useCallback((message) => { setError(message) })`: the value is the parameter of an
 *  error callback or an error setter, reported (or not) where THAT is called - and each such call is a site. */
function isPassThrough(src: string, at: number, value: string): boolean {
  if (!/^[A-Za-z_$][\w$]*$/.test(value)) return false;
  const before = src.slice(Math.max(0, at - 300), at);
  const named = "(?:on[A-Z]?[\\w$]*Error|set[\\w$]*(?:Error|Err|Failure|Problem|Refusal)[\\w$]*)";
  const re = new RegExp(
    `${named}\\s*[:=]\\s*(?:useCallback\\(\\s*)?\\{?\\s*(?:async\\s*)?\\(?\\s*([A-Za-z_$][\\w$]*)|function\\s+${named}\\s*\\(\\s*([A-Za-z_$][\\w$]*)`,
    "g",
  );
  let last: string | null = null;
  for (const m of before.matchAll(re)) last = m[1] ?? m[2];
  return last === value;
}

/** Every `const [value, setValue] = useState` in the file, value to setter. */
function useStatePairs(src: string): Map<string, string> {
  const out = new Map<string, string>();
  for (const m of src.matchAll(/\[\s*([A-Za-z_$][\w$]*)\s*,\s*(set[A-Z][\w$]*)\s*\]\s*=\s*(?:React\.)?useState\b/g)) out.set(m[1], m[2]);
  return out;
}

// ---- presentational alert components (the step 3 rulings, R3) -------------------------------------------
//
// `<ErrorBanner message={error} />` shows `error` in ErrorBanner's own role="alert". The component cannot know
// whether its prop was reported; its CALLER can. So a component whose alert renders one of its props is followed
// to its callers: each `<ErrorBanner message={...}>` is an alert site of its own, judged by the same rules as an
// alert written inline, and the alert inside the component is not a site.

/** A component whose role="alert" renders some of its own props. */
export interface AlertComponent {
  name: string;
  /** The props the alert shows as text, by the name a caller writes. */
  shown: Set<string>;
}

interface ComponentDefinition {
  name: string;
  /** Local name in the body -> prop name a caller writes. A prop with a default value is left out: it is a label
   *  the component supplies (`retryLabel = "Try again"`), not the error a caller hands it. */
  props: Map<string, string>;
  start: number;
  end: number;
}

/** Every function component in some source that destructures its props: `function Name({ a, b })` and
 *  `const Name = ({ a, b }: Props) =>`. */
function componentDefinitions(src: string): ComponentDefinition[] {
  const out: ComponentDefinition[] = [];
  for (const m of src.matchAll(/(?<![\w$])(?:function\s+([A-Z][\w$]*)\s*\(|(?:const|let)\s+([A-Z][\w$]*)\s*(?::[^=\n]+)?=\s*(?:\(|function\s*\())\s*\{/g)) {
    const name = m[1] ?? m[2];
    const open = m.index! + m[0].length - 1;
    const close = matchingClose(src, open);
    if (close < 0) continue;
    const props = new Map<string, string>();
    for (let i = open + 1; i < close; ) {
      while (i < close && /[\s,]/.test(src[i])) i++;
      if (i >= close) break;
      const end = Math.min(expressionEnd(src, i), close);
      const entry = /^([\w$]+)\s*(?::\s*([\w$]+))?\s*(=)?/.exec(src.slice(i, end).trim());
      if (entry && !entry[3]) props.set(entry[2] ?? entry[1], entry[1]);
      i = end + 1;
    }
    // The body: after an arrow's `=>` when one comes before the next `{`, otherwise the function's `{`.
    const arrow = m[2] !== undefined ? src.indexOf("=>", close + 1) : -1;
    const brace = src.indexOf("{", close + 1);
    let at: number;
    if (arrow >= 0 && (brace < 0 || arrow < brace)) at = arrow + 2 + src.slice(arrow + 2).search(/\S/);
    else if (brace >= 0) at = brace;
    else continue;
    const bodyEnd = src[at] === "{" || src[at] === "(" ? matchingClose(src, at) : expressionEnd(src, at);
    if (bodyEnd < 0) continue;
    out.push({ name, props, start: m.index!, end: bodyEnd });
  }
  return out;
}

/** The props an alert in this component renders as text: `{message}`, `{message.text}`, or inside a template. */
function shownProps(definition: ComponentDefinition, content: string): Set<string> {
  const shown = new Set<string>();
  for (const [local, prop] of definition.props) {
    const name = escape(local);
    // `(?<!=)`: `onClick={onRetry}` is an attribute, not text.
    if (new RegExp(`(?<!=)\\{\\s*${name}(?:\\s*\\??\\.\\s*[\\w$]+)*\\s*\\}|\\$\\{\\s*${name}(?![\\w$])`).test(content)) shown.add(prop);
  }
  return shown;
}

/**
 * Every presentational alert component in the given sources, followed through components that hand their own prop
 * to one (`<Panel error={error} />` where Panel renders `<ErrorBanner message={error} />`) until nothing new is found.
 */
export function alertComponents(sources: string[]): Map<string, AlertComponent> {
  const found = new Map<string, AlertComponent>();
  const defined = sources.map((src) => ({ src, definitions: componentDefinitions(src), alerts: alertElements(src) }));
  for (const { src, definitions, alerts } of defined) {
    const lines = src.split("\n");
    for (const d of definitions) {
      for (const a of alerts) {
        if (a.at < d.start || a.at > d.end) continue;
        // An alert that already names its reporter, or is exempt, answers for every caller: nothing to follow.
        if (markerFor(lines, src.slice(0, a.at).split("\n").length, a.content) !== null) continue;
        const shown = shownProps(d, a.content);
        if (shown.size === 0) continue;
        const known = found.get(d.name);
        found.set(d.name, { name: d.name, shown: new Set([...(known?.shown ?? []), ...shown]) });
      }
    }
  }
  let grew = true;
  while (grew) {
    grew = false;
    for (const { src, definitions } of defined) {
      for (const d of definitions) {
        for (const use of componentUses(src, found)) {
          if (use.at < d.start || use.at > d.end) continue;
          const shown = shownProps(d, use.content);
          const known = found.get(d.name);
          const added = [...shown].filter((p) => !known?.shown.has(p));
          if (added.length === 0) continue;
          found.set(d.name, { name: d.name, shown: new Set([...(known?.shown ?? []), ...added]) });
          grew = true;
        }
      }
    }
  }
  return found;
}

/** Each use of a presentational alert component, as an alert element whose content is the shown props' values:
 *  `<ErrorBanner message={error} onRetry={retry} />` gives the content `{error}`. A use passing none of them is
 *  not a display. */
function componentUses(src: string, components: Map<string, AlertComponent>): AlertElement[] {
  const out: AlertElement[] = [];
  if (components.size === 0) return out;
  const names = [...components.keys()].map(escape).join("|");
  for (const m of src.matchAll(new RegExp(`<(${names})(?![\\w$.])`, "g"))) {
    const end = jsxTagEnd(src, m.index!);
    if (end < 0) continue;
    const tag = src.slice(m.index!, end);
    const values: string[] = [];
    for (const prop of components.get(m[1])!.shown) {
      const attr = new RegExp(`(?<![\\w$-])${escape(prop)}=(?=[{"'])`).exec(tag);
      if (!attr) continue;
      const at = attr.index + attr[0].length;
      if (tag[at] === "{") {
        const close = matchingClose(tag, at);
        if (close > 0) values.push(tag.slice(at, close + 1));
      } else {
        const close = tag.indexOf(tag[at], at + 1);
        if (close > 0) values.push(tag.slice(at + 1, close));
      }
    }
    if (values.length === 0) continue;
    const content = values.join("");
    out.push({ at: m.index!, content, roots: jsxRoots(content), name: `<${m[1]}>` });
  }
  return out;
}

interface AlertElement {
  at: number;
  /** The component a use was found through (`<ErrorBanner>`); absent for a role="alert" written here. */
  name?: string;
  content: string;
  /** The root identifiers of every {expression} in the content, each with the members read from it, or null
   *  when it is used whole: `{manage.error}` gives manage -> {error}, `{error}` gives error -> null. */
  roots: Map<string, Set<string> | null>;
}

function alertElements(src: string): AlertElement[] {
  const out: AlertElement[] = [];
  for (const m of src.matchAll(/role=(?:"alert"|\{\s*"alert"\s*\})/g)) {
    const tagStart = src.lastIndexOf("<", m.index!);
    const tag = /^<([A-Za-z][\w.]*)/.exec(src.slice(tagStart))?.[1];
    if (!tag) continue;
    const openEnd = jsxTagEnd(src, tagStart);
    if (openEnd < 0) continue;
    let content = "";
    if (src[openEnd - 1] !== "/") {
      const close = closingTag(src, openEnd + 1, tag);
      if (close < 0) continue;
      content = src.slice(openEnd + 1, close);
    }
    out.push({ at: tagStart, content, roots: jsxRoots(content) });
  }
  return out;
}

/** The index of the ">" that ends the opening tag at `start`, skipping {expressions} and quoted attributes. */
function jsxTagEnd(src: string, start: number): number {
  for (let i = start + 1; i < src.length; i++) {
    const c = src[i];
    if (c === "{") {
      i = matchingClose(src, i);
      if (i < 0) return -1;
    } else if (c === '"' || c === "'") {
      i = src.indexOf(c, i + 1);
      if (i < 0) return -1;
    } else if (c === ">") return i;
  }
  return -1;
}

function closingTag(src: string, from: number, tag: string): number {
  const re = new RegExp(`<(/?)${escape(tag)}(?![\\w.])`, "g");
  re.lastIndex = from;
  let depth = 1;
  let m: RegExpExecArray | null;
  while ((m = re.exec(src)) !== null) {
    if (m[1] === "/") {
      depth--;
      if (depth === 0) return m.index;
    } else {
      const end = jsxTagEnd(src, m.index);
      if (end > 0 && src[end - 1] !== "/") depth++;
      if (end > 0) re.lastIndex = end;
    }
  }
  return -1;
}

const NOT_ROOTS = new Set(["null", "undefined", "true", "false", "typeof", "new", "this", "void", "in", "of", "instanceof", "String", "Number"]);

/** The root identifiers of the top-level {expressions} in some JSX content, skipping nested elements' attributes
 *  and anything that is a component, a string, or a comment. */
function jsxRoots(content: string): Map<string, Set<string> | null> {
  const roots = new Map<string, Set<string> | null>();
  for (let i = 0; i < content.length; i++) {
    if (content[i] === "<" && /[A-Za-z]/.test(content[i + 1] ?? "")) {
      const end = jsxTagEnd(content, i);
      if (end < 0) break;
      i = end;
      continue;
    }
    if (content[i] !== "{") continue;
    const close = matchingClose(content, i);
    if (close < 0) break;
    const expr = stripStringsAndComments(content.slice(i + 1, close));
    for (const m of expr.matchAll(/(?<![\w$.])([A-Za-z_$][\w$]*)(?:\s*\??\.\s*([A-Za-z_$][\w$]*))?/g)) {
      const name = m[1];
      if (NOT_ROOTS.has(name) || /^[A-Z]/.test(name)) continue;
      const known = roots.get(name);
      if (m[2] === undefined || known === null) roots.set(name, null);
      else roots.set(name, new Set([...(known ?? []), m[2]]));
    }
    i = close;
  }
  return roots;
}

interface CatchBody {
  variable: string;
  start: number;
  end: number;
}

/** `catch (err) { ... }` and `.catch((err) => ...)`: the caught variable and the handler's span. */
function catchBodies(src: string): CatchBody[] {
  const out: CatchBody[] = [];
  for (const m of src.matchAll(/\bcatch\s*\(\s*([A-Za-z_$][\w$]*)[^)]*\)\s*\{/g)) {
    const open = m.index! + m[0].length - 1;
    const close = matchingClose(src, open);
    if (close > 0) out.push({ variable: m[1], start: open, end: close });
  }
  for (const m of src.matchAll(/\.catch\(\s*(?:async\s*)?\(?\s*([A-Za-z_$][\w$]*)\s*(?::[^)=]*)?\)?\s*=>/g)) {
    const open = m.index! + "catch".length + 1;
    const close = matchingClose(src, open);
    if (close > 0) out.push({ variable: m[1], start: m.index! + m[0].length, end: close });
  }
  return out;
}

interface Marker {
  kind: "exempt" | "reported-by";
  text: string;
}

/** A marker on the site's line or the line directly above it - or, for an alert, inside the element itself
 *  (`<div role="alert">{/* error-reported-by: useX *\/}{x.error}</div>`). An exemption without a reason is no
 *  exemption. */
function markerFor(lines: string[], line: number, inside = ""): Marker | null {
  for (const text of [lines[line - 1] ?? "", lines[line - 2] ?? "", ...inside.split("\n")]) {
    // `[^\n]` and not `.`: a Windows line ends in a carriage return, which `.` cannot cross, and the reason would
    // then swallow the comment's closing `*/}`.
    const exempt = /error-report-exempt:\s*(.*?)\s*(?:\*\/[^\n]*)?$/.exec(text);
    if (exempt) return exempt[1].length >= 10 ? { kind: "exempt", text: exempt[1] } : { kind: "reported-by", text: "" };
    const by = /error-reported-by:\s*([A-Za-z_$][\w$]*)/.exec(text);
    if (by) return { kind: "reported-by", text: by[1] };
  }
  return null;
}

function problemFor(marker: Marker | null): string {
  if (marker?.kind === "reported-by" && marker.text === "") return "error-report-exempt needs a written reason of at least ten characters";
  if (marker?.kind === "reported-by") return "error-reported-by is for alert elements only; report the value with describeAndReport";
  return `the value is not reported - pass it through ${REPORTING_ROOT}, or exempt a user input check with error-report-exempt: <reason>`;
}

export interface FunctionBody {
  name: string;
  body: string;
  /** What the function returns: each `return` expression, or an arrow's expression body. */
  returned: string[];
}

/** Every named function in some source - `function x(`, `const x = (...) =>`, `const x = useCallback((...) =>` -
 *  with its body and what it returns. */
export function functionBodies(src: string): FunctionBody[] {
  const out: FunctionBody[] = [];
  const decl = /(?:^|[^\w$.])(?:async\s+)?function\s*\*?\s*([A-Za-z_$][\w$]*)\s*[(<]/g;
  for (const m of src.matchAll(decl)) {
    const paramsOpen = src.indexOf("(", m.index! + m[0].length - 1);
    const paramsClose = matchingClose(src, paramsOpen);
    if (paramsClose < 0) continue;
    const open = src.indexOf("{", paramsClose);
    const close = open < 0 ? -1 : matchingClose(src, open);
    if (close < 0) continue;
    const body = src.slice(open, close + 1);
    out.push({ name: m[1], body, returned: returnExpressions(body) });
  }
  // A module object of methods (`export const recordingSession = { async start() { ... } }`) - named by an
  // error-reported-by marker as the store that reports what it holds. It returns nothing itself.
  // A class is named the same way (`error-reported-by: DevReportController`).
  for (const m of src.matchAll(/(?:^|\n)\s*(?:export\s+)?(?:const\s+([A-Za-z_$][\w$]*)\s*(?::[^=\n]+)?=\s*|class\s+([A-Za-z_$][\w$]*)[^{\n]*)\{/g)) {
    const open = m.index! + m[0].length - 1;
    const close = matchingClose(src, open);
    if (close > 0) out.push({ name: m[1] ?? m[2], body: src.slice(open, close + 1), returned: [] });
  }
  const arrow = /(?:const|let)\s+([A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:useCallback\(\s*|useMemo\(\s*\(\)\s*=>\s*)?(?:async\s*)?(\([^)]*\)|[A-Za-z_$][\w$]*)\s*(?::[^=]+?)?=>\s*/g;
  for (const m of src.matchAll(arrow)) {
    const start = m.index! + m[0].length;
    if (src[start] === "{") {
      const close = matchingClose(src, start);
      if (close < 0) continue;
      const body = src.slice(start, close + 1);
      out.push({ name: m[1], body, returned: returnExpressions(body) });
    } else {
      const end = expressionEnd(src, start);
      const body = src.slice(start, end);
      out.push({ name: m[1], body, returned: [body] });
    }
  }
  return out;
}

/** The expression of every `return` in a body that belongs to the function itself - a nested function's or
 *  callback's return is not what this function returns, so those bodies are blanked out first. */
function returnExpressions(body: string): string[] {
  let own = body;
  for (const m of body.matchAll(/=>\s*\{|\bfunction\b[^{]*\{/g)) {
    if (m.index === 0) continue;
    const open = m.index! + m[0].length - 1;
    const close = matchingClose(body, open);
    if (close > open) own = own.slice(0, open + 1) + " ".repeat(close - open - 1) + own.slice(close);
  }
  // An expression-bodied callback (`onError={() => setFailed(report(...))}`) is not what this function returns.
  for (const m of body.matchAll(/=>\s*(?=[^\s{])/g)) {
    const start = m.index! + m[0].length;
    const end = expressionEnd(body, start);
    own = own.slice(0, start) + " ".repeat(end - start) + own.slice(end);
  }
  const out: string[] = [];
  for (const m of own.matchAll(/\breturn\b\s*/g)) {
    const start = m.index! + m[0].length;
    out.push(own.slice(start, expressionEnd(own, start)));
  }
  return out;
}

// ---- a small lexer: enough of JavaScript to match brackets past strings, templates and comments -----------

/** The index of the bracket closing the one at `open`, or -1. */
export function matchingClose(src: string, open: number): number {
  const pairs: Record<string, string> = { "(": ")", "[": "]", "{": "}" };
  const stack: string[] = [];
  for (let i = open; i < src.length; i++) {
    const c = src[i];
    if (c === '"' || c === "'") {
      i = skipQuoted(src, i);
      continue;
    }
    if (c === "`") {
      i = skipTemplate(src, i);
      continue;
    }
    if (c === "/" && src[i + 1] === "/") {
      const nl = src.indexOf("\n", i);
      i = nl < 0 ? src.length : nl;
      continue;
    }
    if (c === "/" && src[i + 1] === "*") {
      const end = src.indexOf("*/", i + 2);
      i = end < 0 ? src.length : end + 1;
      continue;
    }
    if (pairs[c]) stack.push(pairs[c]);
    else if (c === ")" || c === "]" || c === "}") {
      if (stack.pop() !== c) return -1;
      if (stack.length === 0) return i;
    }
  }
  return -1;
}

/** Where an expression starting at `start` ends: a `;`, or a `,` `)` `]` `}` at depth zero, or a blank line. */
function expressionEnd(src: string, start: number): number {
  for (let i = start; i < src.length; i++) {
    const c = src[i];
    if (c === '"' || c === "'") i = skipQuoted(src, i);
    else if (c === "`") i = skipTemplate(src, i);
    else if (c === "(" || c === "[" || c === "{") {
      const close = matchingClose(src, i);
      if (close < 0) return src.length;
      i = close;
    } else if (c === ";" || c === "," || c === ")" || c === "]" || c === "}") return i;
    else if (c === "\n" && src[i + 1] === "\n") return i;
  }
  return src.length;
}

function skipQuoted(src: string, at: number): number {
  const q = src[at];
  for (let i = at + 1; i < src.length; i++) {
    if (src[i] === "\\") i++;
    else if (src[i] === q || src[i] === "\n") return i;
  }
  return src.length;
}

function skipTemplate(src: string, at: number): number {
  for (let i = at + 1; i < src.length; i++) {
    if (src[i] === "\\") i++;
    else if (src[i] === "`") return i;
    else if (src[i] === "$" && src[i + 1] === "{") {
      const close = matchingClose(src, i + 1);
      if (close < 0) return src.length;
      i = close;
    }
  }
  return src.length;
}

function stripStringsAndComments(expr: string): string {
  let out = "";
  for (let i = 0; i < expr.length; i++) {
    const c = expr[i];
    if (c === '"' || c === "'") i = skipQuoted(expr, i);
    else if (c === "`") {
      // A template's text goes; its ${...} expressions stay - `Could not load: ${error}` shows `error`.
      const end = skipTemplate(expr, i);
      for (let j = i + 1; j < end; j++) {
        if (expr[j] === "\\") j++;
        else if (expr[j] === "$" && expr[j + 1] === "{") {
          const close = matchingClose(expr, j + 1);
          if (close < 0 || close > end) break;
          out += ` ${stripStringsAndComments(expr.slice(j + 2, close))} `;
          j = close;
        }
      }
      i = end;
    } else if (c === "/" && expr[i + 1] === "*") {
      const end = expr.indexOf("*/", i + 2);
      i = end < 0 ? expr.length : end + 1;
    } else out += c;
  }
  return out;
}

/** The text with its comments removed - `//` to the end of the line and `/* ... *\/` - and its strings kept as written,
 *  so a comment naming a reporter (`err.message /* reportShownError() is the caller's job *\/`) cannot make an
 *  unreported value read as reported (the 3c re-review, P14 and P15). */
function withoutComments(text: string): string {
  let out = "";
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"' || c === "'" || c === "`") {
      const end = c === "`" ? skipTemplate(text, i) : skipQuoted(text, i);
      out += text.slice(i, end + 1);
      i = end;
    } else if (c === "/" && text[i + 1] === "/") {
      const end = text.indexOf("\n", i);
      i = (end < 0 ? text.length : end) - 1;
    } else if (c === "/" && text[i + 1] === "*") {
      const end = text.indexOf("*/", i + 2);
      i = end < 0 ? text.length : end + 1;
    } else out += c;
  }
  return out;
}

function collapse(text: string): string {
  return text.replace(/\s+/g, " ").trim();
}

function escape(name: string): string {
  return name.replace(/[$]/g, "\\$");
}

function sourceFiles(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) {
      if (name !== "node_modules") sourceFiles(full, out);
    } else if (/\.(ts|tsx)$/.test(name) && !/\.(test|testkit)\.(ts|tsx)$/.test(name) && !name.endsWith(".d.ts")) {
      out.push(full);
    }
  }
  return out;
}
