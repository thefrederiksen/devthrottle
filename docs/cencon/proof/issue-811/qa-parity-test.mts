// Independent QA parity checks (issue #811): exercise the TS ports directly with cases the QA
// agent chose, including truncation caps and the desktop HistoryTextTests fixtures, to confirm the
// ports behave like the C# source. Not the developer's test file.
import { cleanForReading } from "./src/history/historyText";
import { extractLinks } from "./src/history/historyLinks";
import { mapHistory, anyHidden } from "./src/history/bubbleMapper";
import type { SessionHistoryDto } from "./src/history/types";

let fails = 0;
function eq(name: string, got: unknown, want: unknown) {
  const g = JSON.stringify(got), w = JSON.stringify(want);
  if (g === w) console.log("PASS", name);
  else { console.log("FAIL", name, "\n  got =", g, "\n  want=", w); fails++; }
}

// CleanForReading
eq("ANSI stripped (cursor+color)", cleanForReading("a\x1b[2Kb \x1b[31mred\x1b[0m"), "ab red");
eq("system-reminder block dropped", cleanForReading("keep<system-reminder>x\ny</system-reminder>end"), "keepend");
eq("task-notification dropped", cleanForReading("<task-notification>done</task-notification>hi"), "hi");
eq("stray unmatched wrapper tag removed", cleanForReading("text <system-reminder> trailing"), "text  trailing");
eq("generic List<string> untouched", cleanForReading("var x: List<string>;"), "var x: List<string>;");
eq("placeholder </task-id> wrapped", cleanForReading("pass </task-id> through"), "pass `</task-id>` through");
eq("placeholder NOT wrapped in fenced code", cleanForReading("```\n<issue#>\n```"), "```\n<issue#>\n```");
eq("blank runs collapsed", cleanForReading("a\n\n\n\nb"), "a\n\nb");

// Links (URLs + absolute paths only; relative skipped)
eq("windows abs path + url, dedup order",
  extractLinks("open D:\\repo\\src\\LinkDetector.cs and https://github.com/x/y"),
  [{ text: "https://github.com/x/y", isUrl: true }, { text: "D:\\repo\\src\\LinkDetector.cs", isUrl: false }]);
eq("relative path not detected", extractLinks("mobile/src/app.ts"), []);
eq("url trailing comma stripped", extractLinks("see https://a.com/p, ok"), [{ text: "https://a.com/p", isUrl: true }]);

// Truncation caps: assistant tool-result capped at 400, tool-input suffix at 160, tool-result bubble at 2000
const big = "X".repeat(5000);
const histCaps: SessionHistoryDto = {
  sessionId: "s", directorId: "", agent: "ClaudeCode", isSupported: true, isRawText: false,
  historyState: "Idle", status: "ok", error: null,
  messages: [
    { role: "Assistant", parts: [
      { kind: "ToolResult", text: big, toolName: null, toolId: "t" },
    ] },
    { role: "User", parts: [{ kind: "ToolResult", text: big, toolName: null, toolId: "t" }] },
  ],
};
const capsAll = mapHistory(histCaps, { showToolCalls: true, showToolResults: true, showThinking: true });
// assistant: "[result] " + 400 chars + " ..." = 9+400+4 = 413
eq("assistant tool-result capped at 400", capsAll[0].body.length, 413);
// user tool-result: per-part UserToolResultMax=600 truncates FIRST (600+" ..."=604), then the
// 2000 bubble cap is a no-op - identical to the C# (Truncate(text,600) then Truncate(body,2000)).
eq("user tool-result per-part cap 600 wins over bubble 2000", capsAll[1].body.length, 604);

// Default filter hides tool/thinking; a pure tool-result user message is omitted entirely
const hist: SessionHistoryDto = {
  sessionId: "s", directorId: "", agent: "ClaudeCode", isSupported: true, isRawText: false,
  historyState: "Idle", status: "ok", error: null,
  messages: [
    { role: "User", parts: [{ kind: "Text", text: "do it", toolName: null, toolId: null }] },
    { role: "Assistant", parts: [
      { kind: "Text", text: "ok", toolName: null, toolId: null },
      { kind: "Thinking", text: "ponder", toolName: null, toolId: null },
      { kind: "ToolUse", text: "{\"a\":1}", toolName: "Bash", toolId: "t1" },
      { kind: "ToolResult", text: "ok out", toolName: null, toolId: "t1" },
    ] },
    { role: "User", parts: [{ kind: "ToolResult", text: "exit 0", toolName: null, toolId: "t1" }] },
  ],
};
eq("DEFAULT: only You + Assistant text",
  mapHistory(hist, { showToolCalls: false, showToolResults: false, showThinking: false }).map(b => `${b.speaker}|${b.kind}|${b.body}`),
  ["You|user|do it", "Assistant|assistant|ok"]);
eq("Thinking on only",
  mapHistory(hist, { showToolCalls: false, showToolResults: false, showThinking: true })[1].body,
  "ok\n(thinking) ponder");
eq("Tool calls on shows tool name + input suffix",
  mapHistory(hist, { showToolCalls: true, showToolResults: false, showThinking: false })[1].body,
  'ok\n[tool] Bash  {"a":1}');
eq("Results on shows assistant result and tool-result bubble",
  mapHistory(hist, { showToolCalls: false, showToolResults: true, showThinking: false }).map(b => b.speaker),
  ["You", "Assistant", "Tool result"]);
eq("anyHidden true when one off", anyHidden({ showToolCalls: true, showToolResults: true, showThinking: false }), true);
eq("anyHidden false when all on", anyHidden({ showToolCalls: true, showToolResults: true, showThinking: true }), false);

if (fails > 0) { console.log("\n" + fails + " FAILED"); process.exit(1); }
console.log("\nALL " + "QA PARITY CHECKS PASSED");
