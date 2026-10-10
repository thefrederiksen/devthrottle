// TEST SUPPORT ONLY - never imported by product code (it reads source files from disk).
//
// Where the prompt's words leave a shell, DERIVED FROM THE CODE (the Error Logging mission, issue #3675). The
// owner's ruling: "A test makes sure no send path ever passes the prompt's words into a report." A hand-kept
// list of send paths goes stale the day someone adds one, so the list is worked out here instead:
//
//   1. The anchors are the Gateway routes that take a person's words into a session: POST or PATCH to
//      /sessions/{sid}/prompt, /sessions/{sid}/queue or one queued item /sessions/{sid}/queue/{id}, and the
//      dictation completion /dictation/{id}/complete (which carries the typed text either side of a dictation).
//      Whatever expression fills each {...} - `${sid}`, `${encodeURIComponent(sessionId)}` - is accepted.
//   2. Every function in client-core whose body calls one of those routes is a send function, and so is every
//      function that calls a send function - followed until nothing new is found. A function is any of: a
//      `function` declaration, a constant holding an arrow or a function expression, a `useCallback` handler, or
//      a class or object method.
//   3. Every call of a send function in the scanned shell files is a send SITE, named by its file, the function
//      called, and the handler or function declaration it sits in.
//
// THE SELF-CHECK (review finding 3). Steps 1 and 2 read the code through patterns, and a send path written in a
// shape they do not recognise would simply not be derived - the test would then pass having never driven it. So a
// plain search for the route paths, independent of variable names and of how the call is spelled, runs beside the
// derivation, and every write to a word route it finds must sit inside a derived send function. One that does not
// - a call through plain `fetch`, a function shape the finder misses - throws, naming the file and line.
//
// A test then drives every site with a failing Gateway and checks every report body. A new site with no driver
// fails that test by name; so does a driver whose site has gone.

import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";

/** A word route's path inside a template literal - any `${...}` in its segments - ending the literal or its path. */
const WORD_PATH = /\/(?:sessions\/\$\{[^}`]+\}\/(?:prompt|queue)(?:\/\$\{[^}`]+\})?|dictation\/\$\{[^}`]+\}\/complete)(?=[`?])/;
/** The routes that carry a person's words into a session, called through the Gateway client. */
const WORD_ROUTES = new RegExp("gatewayFetch\\(\\s*`" + WORD_PATH.source);
/** A write: the words travel in the body of a POST or a PATCH, never a GET or a DELETE. */
const WRITES = /method:\s*"(?:POST|PATCH)"/;
/** The self-check's plain search: the route path however it is called, as a template or as joined strings. */
const PLAIN_PATHS = [
  WORD_PATH,
  /["'`]\/sessions\/["'`]\s*\+[^;\n]*\+\s*["'`]\/(?:prompt|queue)["'`]/,
  /["'`]\/dictation\/["'`]\s*\+[^;\n]*\+\s*["'`]\/complete["'`]/,
];

export interface PromptSendSite {
  /** "apps/mobile/src/components/SessionControls.tsx#sendTypedPrompt#onSend" - file, callee, enclosing handler. */
  id: string;
  file: string;
  line: number;
  callee: string;
}

export interface FoundFunction {
  name: string;
  /** The body's span: a block's braces, or an expression arrow's expression. */
  open: number;
  close: number;
  body: string;
  /** A `function` declaration or a `useCallback` handler: what a site is named after. */
  handler: boolean;
}

/** The repository root, from this file's own location: <root>/packages/client-core/src/errors/. */
export function repositoryRoot(): string {
  return join(__dirname, "..", "..", "..", "..");
}

/**
 * Every send function in client-core, by name. Throws when the self-check finds a write to a word route that is
 * not inside a derived send function, naming each by file and line.
 */
export function promptSendFunctions(root = repositoryRoot()): Set<string> {
  const files = sourceFiles(join(root, "packages", "client-core", "src")).map((file) => {
    const src = readFileSync(file, "utf8");
    return { file, src, functions: findFunctions(src) };
  });
  const functions = files.flatMap((f) => f.functions);
  const send = new Set(functions.filter((f) => WORD_ROUTES.test(f.body) && WRITES.test(f.body)).map((f) => f.name));
  let grew = true;
  while (grew) {
    grew = false;
    for (const f of functions) {
      if (send.has(f.name)) continue;
      if ([...send].some((s) => callPattern(s).test(f.body))) {
        send.add(f.name);
        grew = true;
      }
    }
  }

  const gaps: string[] = [];
  for (const { file, src, functions: inFile } of files) {
    for (const plain of PLAIN_PATHS) {
      const re = new RegExp(plain.source, "g");
      let m: RegExpExecArray | null;
      while ((m = re.exec(src)) !== null) {
        const where = `${relative(root, file).split(sep).join("/")}:${src.slice(0, m.index).split("\n").length}`;
        const home = innermost(inFile, m.index);
        if (home === null) gaps.push(`${where}: a word route outside any function the kit can name`);
        else if (!send.has(home.name) && WRITES.test(home.body))
          gaps.push(`${where}: ${home.name} writes to a word route but was not derived as a send function`);
      }
    }
  }
  if (gaps.length > 0) {
    throw new Error(
      "promptSendSites: the plain path search found word routes the derivation did not take - widen the kit, or " +
        "the test passes without driving them:\n  " +
        gaps.join("\n  "),
    );
  }
  return send;
}

/** Every call of a send function in the given shell files or folders (paths relative to the repository root). */
export function promptSendSites(scanned: string[], root = repositoryRoot()): PromptSendSite[] {
  const send = promptSendFunctions(root);
  const files = scanned.flatMap((p) => {
    const full = join(root, p);
    return statSync(full).isDirectory() ? sourceFiles(full) : [full];
  });
  const sites: PromptSendSite[] = [];
  for (const file of files) {
    const src = readFileSync(file, "utf8");
    const lines = src.split("\n");
    const handlers = findFunctions(src).filter((f) => f.handler);
    for (const callee of send) {
      const re = new RegExp(callPattern(callee).source, "g");
      let m: RegExpExecArray | null;
      while ((m = re.exec(src)) !== null) {
        const line = src.slice(0, m.index).split("\n").length;
        const text = lines[line - 1];
        // An import, or the function's own declaration, is not a call.
        if (/^\s*(?:import|export)\b/.test(text) || /function\s+$/.test(src.slice(Math.max(0, m.index - 20), m.index))) continue;
        const rel = relative(root, file).split(sep).join("/");
        // Named after the handler or declaration a person reads in the shell ("onSend", "GatedLayout"), never an
        // inline arrow inside it.
        const enclosing = innermost(handlers, m.index)?.name ?? "(top level)";
        sites.push({ id: `${rel}#${callee}#${enclosing}`, file: rel, line, callee });
      }
    }
  }
  return sites.sort((a, b) => a.id.localeCompare(b.id));
}

function callPattern(name: string): RegExp {
  // Not preceded by a word character or a dot (a method of something else), followed by "(".
  return new RegExp(`(?<![A-Za-z0-9_.$])${name}\\(`);
}

/** The innermost function whose body holds a position. */
function innermost(functions: FoundFunction[], at: number): FoundFunction | null {
  let best: FoundFunction | null = null;
  for (const f of functions) {
    if (f.open <= at && at <= f.close && (best === null || f.open > best.open)) best = f;
  }
  return best;
}

/** Words that open a block with parentheses and are never a method's name. */
const NOT_A_METHOD = new Set(["if", "for", "while", "switch", "catch", "return", "function", "new", "await", "typeof", "super", "with"]);

/**
 * Every named function in a source file, with its body - by pattern and bracket matching. Good enough for our own
 * source; the self-check in promptSendFunctions is what notices when it is not.
 */
export function findFunctions(src: string): FoundFunction[] {
  const out: FoundFunction[] = [];
  const add = (name: string, afterParams: number, handler: boolean) => {
    const span = bodyAfter(src, afterParams);
    if (span !== null) out.push({ name, open: span.open, close: span.close, body: src.slice(span.open, span.close + 1), handler });
  };
  let m: RegExpExecArray | null;

  // function name(...) {   |   export async function name<T>(...) {
  const declarations = /(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(\w+)\s*(?:<[^>]*>)?\s*\(/g;
  while ((m = declarations.exec(src)) !== null) {
    const close = matchParen(src, m.index + m[0].length - 1);
    if (close !== null) add(m[1], close + 1, true);
  }
  // const name = useCallback(async (...) => { ... }
  const callbacks = /(?:const|let)\s+(\w+)\s*=\s*useCallback\(\s*(?:async\s+)?/g;
  while ((m = callbacks.exec(src)) !== null) {
    const at = m.index + m[0].length;
    const close = src[at] === "(" ? matchParen(src, at) : identifierEnd(src, at);
    if (close !== null) add(m[1], close + 1, true);
  }
  // const name = async (...) => ...   |   const name = (x: T): R => ...   |   const name = function (...) { ... }
  const constants = /(?:const|let|var)\s+(\w+)\s*(?::[^=;\n]+)?=\s*(?:async\s+)?(?:function\b\s*\*?\s*\w*\s*)?(?:<[^>]*>\s*)?(?=\(|\w+\s*=>)/g;
  while ((m = constants.exec(src)) !== null) {
    const at = m.index + m[0].length;
    const close = src[at] === "(" ? matchParen(src, at) : identifierEnd(src, at);
    if (close !== null) add(m[1], close + 1, false);
  }
  // A class or object method on its own line:   private async pumpInput(): Promise<void> {
  const methods = /^[ \t]*(?:(?:public|private|protected|static|async|override|get|set)\s+)*(\w+)\s*(?:<[^>]*>)?\s*\(/gm;
  while ((m = methods.exec(src)) !== null) {
    if (NOT_A_METHOD.has(m[1])) continue;
    const close = matchParen(src, m.index + m[0].length - 1);
    // A method has a block straight after its parameters (and return type); a call at a line's start does not.
    if (close === null || !/^\s*(?::[^;{=]*?)?\s*\{/.test(src.slice(close + 1, close + 200))) continue;
    add(m[1], close + 1, false);
  }
  return out;
}

/** The index of the parenthesis that closes the one at `open`. */
function matchParen(src: string, open: number): number | null {
  let depth = 0;
  for (let j = open; j < src.length; j++) {
    if (src[j] === "(") depth++;
    else if (src[j] === ")") {
      depth--;
      if (depth === 0) return j;
    }
  }
  return null;
}

/** For a single-parameter arrow (`x => ...`): the last character of the parameter's name. */
function identifierEnd(src: string, at: number): number | null {
  const m = /^\w+/.exec(src.slice(at));
  return m ? at + m[0].length - 1 : null;
}

/**
 * The body after a parameter list: past an optional return type, either a block (straight away, or after `=>`),
 * or an arrow's expression, which runs to the first `;` or `,` or closing bracket at its own depth. Null when what
 * follows is not a function body at all (`const x = (a + b) * 2`).
 */
function bodyAfter(src: string, from: number): { open: number; close: number } | null {
  const head = /^\s*(?::\s*[^{=;]*?)?\s*(?:=>\s*(\{)?|(\{))/.exec(src.slice(from, from + 400));
  if (head === null) return null;
  const brace = head[1] !== undefined || head[2] !== undefined;
  const start = from + head[0].length - (brace ? 1 : 0);
  let depth = 0;
  for (let k = start; k < src.length; k++) {
    const c = src[k];
    if (c === "(" || c === "{" || c === "[") depth++;
    else if (c === ")" || c === "}" || c === "]") {
      if (depth === 0) return { open: start, close: k - 1 };
      depth--;
      if (brace && depth === 0) return { open: start, close: k };
    } else if (!brace && depth === 0 && (c === ";" || c === ",")) return { open: start, close: k - 1 };
  }
  return { open: start, close: src.length - 1 };
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
