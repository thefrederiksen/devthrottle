// 1:1 parity checks of the TS ports against the desktop C# behavior (the Cockpit.Tests cases).
import { cleanForReading } from "../../../../../../../ReposFred/devthrottle-wt-811h/mobile/src/history/historyText";
import { extractLinks } from "../../../../../../../ReposFred/devthrottle-wt-811h/mobile/src/history/historyLinks";
import { mapHistory } from "../../../../../../../ReposFred/devthrottle-wt-811h/mobile/src/history/bubbleMapper";
import type { SessionHistoryDto } from "../../../../../../../ReposFred/devthrottle-wt-811h/mobile/src/history/types";

let fails = 0;
function eq(name: string, got: unknown, want: unknown) {
  const g = JSON.stringify(got), w = JSON.stringify(want);
  if (g === w) { console.log("PASS", name); }
  else { console.log("FAIL", name, "\n  got =", g, "\n  want=", w); fails++; }
}

// --- HistoryText.CleanForReading (mirrors HistoryTextTests.cs) ---
eq("plain prose unchanged", cleanForReading("Just normal text."), "Just normal text.");
eq("ordinary angle brackets/generics kept", cleanForReading("Use List<string> here."), "Use List<string> here.");
eq("slash-command wrapper keeps only command",
  cleanForReading("<command-name>/compact</command-name>"), "/compact");
eq("local-command-caveat dropped",
  cleanForReading("<local-command-caveat>do not respond</local-command-caveat>"), "");
eq("system-reminder dropped",
  cleanForReading("before <system-reminder>noise</system-reminder> after"), "before  after");
eq("placeholder tag wrapped as code", cleanForReading("send <your reply> now"), "send `<your reply>` now");
eq("ANSI stripped", cleanForReading("a \x1b[31mred\x1b[0m b"), "a red b");
eq("placeholder inside code span untouched", cleanForReading("`/loop <issue#>`"), "`/loop <issue#>`");

// --- HistoryLinks (URLs + absolute paths only, no repo root) ---
eq("url + absolute path detected",
  extractLinks("see https://example.com/docs and D:\\repo\\src\\file.cs"),
  [{ text: "https://example.com/docs", isUrl: true }, { text: "D:\\repo\\src\\file.cs", isUrl: false }]);
eq("relative path NOT guessed", extractLinks("edit src/app.ts please"), []);
eq("trailing period stripped from url", extractLinks("go to https://example.com."),
  [{ text: "https://example.com", isUrl: true }]);

// --- HistoryBubbleMapper filtering + speakers ---
const hist: SessionHistoryDto = {
  sessionId: "s", directorId: "", agent: "ClaudeCode", isSupported: true, isRawText: false,
  historyState: "Idle", status: "ok", error: null,
  messages: [
    { role: "User", parts: [{ kind: "Text", text: "hello", toolName: null, toolId: null }] },
    { role: "Assistant", parts: [
      { kind: "Text", text: "hi", toolName: null, toolId: null },
      { kind: "Thinking", text: "hmm", toolName: null, toolId: null },
      { kind: "ToolUse", text: "{}", toolName: "Read", toolId: "t1" },
      { kind: "ToolResult", text: "res", toolName: null, toolId: "t1" },
    ] },
    { role: "User", parts: [{ kind: "ToolResult", text: "exit 0", toolName: null, toolId: "t1" }] },
  ],
};
const hidden = mapHistory(hist, { showToolCalls: false, showToolResults: false, showThinking: false });
eq("default: You + Assistant(text only), tool-result bubble hidden",
  hidden.map(b => `${b.speaker}:${b.body}`), ["You:hello", "Assistant:hi"]);
const all = mapHistory(hist, { showToolCalls: true, showToolResults: true, showThinking: true });
eq("all on: thinking/tool/result folded in + Tool result bubble shown",
  all.map(b => b.speaker), ["You", "Assistant", "Tool result"]);
eq("all on: assistant body order",
  all[1].body, "hi\n(thinking) hmm\n[tool] Read\n[result] res");

if (fails > 0) { console.log("\n" + fails + " FAILED"); process.exit(1); }
console.log("\nALL PARITY CHECKS PASSED");
