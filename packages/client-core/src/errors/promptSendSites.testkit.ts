// TEST SUPPORT ONLY - never imported by product code (it reads source files from disk).
//
// Where the prompt's words leave a shell, DERIVED FROM THE CODE (the Error Logging mission, issue #3675). The
// owner's ruling: "A test makes sure no send path ever passes the prompt's words into a report." A hand-kept
// list of send paths goes stale the day someone adds one, so the list is worked out here instead:
//
//   1. The anchors are the Gateway routes that take a person's words into a session: POST or PATCH to
//      /sessions/{sid}/prompt or /sessions/{sid}/queue, and the dictation completion /dictation/{id}/complete
//      (which carries the typed text either side of a dictation).
//   2. Every function in client-core whose body calls one of those routes is a send function, and so is every
//      function that calls a send function - followed until nothing new is found.
//   3. Every call of a send function in the scanned shell files is a send SITE, named by its file, the function
//      called, and the function or handler it sits in.
//
// A test then drives every site with a failing Gateway and checks every report body. A new site with no driver
// fails that test by name; so does a driver whose site has gone.

import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";

/** The routes that carry a person's words into a session. */
const WORD_ROUTES = /gatewayFetch\(`\/(?:sessions\/\$\{sid\}\/(?:prompt|queue)|dictation\/\$\{id\}\/complete)`/;
/** A write: the words travel in the body of a POST or a PATCH, never a GET or a DELETE. */
const WRITES = /method:\s*"(?:POST|PATCH)"/;

export interface PromptSendSite {
  /** "apps/mobile/src/components/SessionControls.tsx#sendTypedPrompt#onSend" - file, callee, enclosing handler. */
  id: string;
  file: string;
  line: number;
  callee: string;
}

/** The repository root, from this file's own location: <root>/packages/client-core/src/errors/. */
export function repositoryRoot(): string {
  return join(__dirname, "..", "..", "..", "..");
}

/** Every send function in client-core, by name. */
export function promptSendFunctions(root = repositoryRoot()): Set<string> {
  const functions = sourceFiles(join(root, "packages", "client-core", "src")).flatMap((f) => functionBodies(readFileSync(f, "utf8")));
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
    for (const callee of send) {
      const re = new RegExp(callPattern(callee).source, "g");
      let m: RegExpExecArray | null;
      while ((m = re.exec(src)) !== null) {
        const line = src.slice(0, m.index).split("\n").length;
        const text = lines[line - 1];
        // An import, or the function's own declaration, is not a call.
        if (/^\s*(?:import|export)\b/.test(text) || /function\s+$/.test(src.slice(Math.max(0, m.index - 20), m.index))) continue;
        const rel = relative(root, file).split(sep).join("/");
        sites.push({ id: `${rel}#${callee}#${enclosingName(src, m.index)}`, file: rel, line, callee });
      }
    }
  }
  return sites.sort((a, b) => a.id.localeCompare(b.id));
}

function callPattern(name: string): RegExp {
  // Not preceded by a word character or a dot (a method of something else), followed by "(".
  return new RegExp(`(?<![A-Za-z0-9_.$])${name}\\(`);
}

/** The innermost enclosing handler or function: `const onSend = useCallback(...)`, `function GatedLayout(...)`. */
function enclosingName(src: string, at: number): string {
  let best: { name: string; start: number } | null = null;
  const re = /(?:const\s+(\w+)\s*=\s*useCallback\(|function\s+(\w+)\s*[(<])/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(src)) !== null && m.index < at) {
    const body = bodySpan(src, m.index + m[0].length - 1, m[1] !== undefined);
    if (body && body.open < at && at < body.close && (best === null || body.open > best.start)) {
      best = { name: m[1] ?? m[2], start: body.open };
    }
  }
  return best?.name ?? "(top level)";
}

/** The braces of a declaration's body. A function's parameter list is skipped first (it may hold a brace in a
 *  type); a useCallback's body is the first brace after its arrow. */
function bodySpan(src: string, from: number, isCallback: boolean): { open: number; close: number } | null {
  let j = from;
  if (isCallback) {
    j = src.indexOf("=>", from);
    if (j < 0) return null;
  } else {
    let depth = 0;
    for (; j < src.length; j++) {
      if (src[j] === "(") depth++;
      else if (src[j] === ")") {
        depth--;
        if (depth === 0) break;
      }
    }
  }
  const open = src.indexOf("{", j);
  if (open < 0) return null;
  let braces = 0;
  for (let k = open; k < src.length; k++) {
    if (src[k] === "{") braces++;
    else if (src[k] === "}") {
      braces--;
      if (braces === 0) return { open, close: k };
    }
  }
  return null;
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

/** Every named function declaration and its body, by brace matching. Good enough for our own source. */
function functionBodies(src: string): { name: string; body: string }[] {
  const out: { name: string; body: string }[] = [];
  const re = /(?:export\s+)?(?:async\s+)?function\s+(\w+)\s*[(<]/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(src)) !== null) {
    // Skip the parameter list (and any generic list) to the body's opening brace.
    let depth = 0;
    let j = m.index + m[0].length - 1;
    for (; j < src.length; j++) {
      const c = src[j];
      if (c === "(" || c === "<") depth++;
      else if (c === ")" || c === ">") {
        depth--;
        if (depth === 0) break;
      }
    }
    const open = src.indexOf("{", j);
    if (open < 0) continue;
    let braces = 0;
    let k = open;
    for (; k < src.length; k++) {
      if (src[k] === "{") braces++;
      else if (src[k] === "}") {
        braces--;
        if (braces === 0) break;
      }
    }
    out.push({ name: m[1], body: src.slice(open, k + 1) });
  }
  return out;
}
