import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { promptSendFunctions, promptSendSites } from "./promptSendSites.testkit";

// The send-site derivation is the ground the "never the prompt's words" tests stand on: a send path it does not see
// is a path those tests never drive, and they stay green. Review finding 3 (issue #3675): it was keyed to the
// variable names `sid` and `id` and to `function` declarations. These pin that every shape our code uses is derived,
// and that a shape it still cannot read fails loudly - by file and line - instead of passing in silence.

const roots: string[] = [];

function repository(files: Record<string, string>): string {
  const root = mkdtempSync(join(tmpdir(), "prompt-send-sites-"));
  roots.push(root);
  for (const [path, src] of Object.entries(files)) {
    const full = join(root, ...path.split("/"));
    mkdirSync(join(full, ".."), { recursive: true });
    writeFileSync(full, src);
  }
  return root;
}

afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

const CORE = "packages/client-core/src/api/client.ts";

describe("prompt-send derivation", () => {
  it("derives a send whatever fills the route's segments, and as an arrow constant or a class method", () => {
    const root = repository({
      [CORE]: `
export const sendArrow = async (sessionId: string, text: string): Promise<void> => {
  await gatewayFetch(\`/sessions/\${encodeURIComponent(sessionId)}/prompt\`, { method: "POST", body: JSON.stringify({ text }) });
};
export const editItem = async function (s: string, i: string, text: string) {
  return gatewayFetch(\`/sessions/\${s}/queue/\${i}\`, { method: "PATCH", body: JSON.stringify({ text }) });
};
export class Pump {
  private async pumpInput(): Promise<void> {
    await sendArrow(this.sessionId, this.pending);
  }
}
export async function listQueue(sessionId: string) {
  return gatewayFetch(\`/sessions/\${sessionId}/queue\`, { method: "GET" });
}
`,
      "apps/shell/src/Page.tsx": `
const onSend = useCallback(async () => {
  await sendArrow(sessionId, text);
}, []);
`,
    });

    const send = promptSendFunctions(root);

    expect([...send].sort()).toEqual(["editItem", "pumpInput", "sendArrow"]);
    expect(promptSendSites(["apps/shell/src"], root).map((s) => s.id)).toEqual(["apps/shell/src/Page.tsx#sendArrow#onSend"]);
  });

  it("a write to a word route the derivation cannot take fails by file and line", () => {
    // The deliberately broken path: plain fetch instead of the Gateway client, which no anchor matches.
    const root = repository({
      [CORE]: `
export async function sendAround(sessionId: string, text: string) {
  return fetch(\`/sessions/\${sessionId}/prompt\`, { method: "POST", body: JSON.stringify({ text }) });
}
`,
    });

    expect(() => promptSendFunctions(root)).toThrow(
      `${CORE}:3: sendAround writes to a word route but was not derived as a send function`,
    );
  });

  it("a route joined from strings is found by the plain search too", () => {
    const root = repository({
      [CORE]: `
export async function sendJoined(sid: string, text: string) {
  return fetch("/sessions/" + sid + "/queue", { method: "POST", body: JSON.stringify({ text }) });
}
`,
    });

    expect(() => promptSendFunctions(root)).toThrow(`${CORE}:3: sendJoined writes to a word route`);
  });

  it("on this repository the self-check passes, and the terminal keystroke pump is a send function", () => {
    const send = promptSendFunctions();

    console.log(`[promptSendSites.testkit] client-core send functions (${send.size}): ${[...send].sort().join(", ")}`);
    expect(send.size).toBeGreaterThan(0);
    for (const name of ["sendPrompt", "sendVoicePrompt", "enqueuePrompt", "editQueueItem", "sendTypedPrompt", "pumpInput"]) {
      expect(send.has(name), name).toBe(true);
    }
  });
});
