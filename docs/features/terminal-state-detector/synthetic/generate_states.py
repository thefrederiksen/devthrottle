#!/usr/bin/env python3
"""
Generate 100 synthetic-but-realistic Claude Code terminal states for testing the
Wingman's LLM turn-state judge (WingmanService.ClassifyTerminalStateAsync).

Each state is a resolved, ANSI-stripped screen (a list of rows) plus the expected
verdict the judge must return:
    working | waiting_for_input | waiting_for_permission | cancelled | unknown

Grounded in real Claude Code v2.1.150 captures (docs/features/terminal-state-detector):
- persistent mode footer:  "  >> bypass permissions on (shift+tab to cycle)"
  (also "accept edits on", "plan mode on")
- working footer:          mode footer + " · esc to interrupt"
- spinner lines:           "<glyph> <Verb>... (Ns . v Ntokens)"  glyph in * . + x and ✻✽✶✢
- idle input box:          "> " on its own line between rule lines, no working footer
- choice/permission box:   numbered options with a "> N." selector arrow
- cc-director pickers:      "Enter to select . up/down to navigate . Esc to cancel"
- torn frames:             box-dashes bleed into the footer ("esc-to-interrupt")

ASCII-only output per project rule; we use ">>" for the chevron and "-" rules so the
file stays clean, but include the real unicode glyphs where they matter for realism.
Writes states.json next to this script.
"""
import json
import os

RULE = "-" * 120
MODE_FOOTERS = [
    "  ⏵⏵ bypass permissions on (shift+tab to cycle)",
    "  ⏵⏵ accept edits on (shift+tab to cycle)",
    "  ⏵⏵ plan mode on (shift+tab to cycle)",
]
SPIN_GLYPHS = ["*", "·", "✴", "✽", "✶", "✢", "+", "×"]
WORK_VERBS = ["Concocting", "Mulling", "Determining", "Tinkering", "Brewing",
              "Sauteing", "Churning", "Pondering", "Computing", "Working"]
HEADER = [
    " ##  Claude Code v2.1.150",
    " ##  Opus 4.7 (1M context) . Claude Max",
    "     D:\\ReposFred\\cc-director",
]

states = []

def add(cat, expected, rows, note=""):
    states.append({"id": f"{cat}{len([s for s in states if s['category']==cat])+1:02d}",
                   "category": cat, "expected": expected, "note": note, "screen": rows})

# ---------------------------------------------------------------- WORKING (30)
work_tools = [
    ["> refactor the config loader", "* Searching the codebase... (Brewed for 8s, esc to interrupt)",
     "  Read DictionaryLoader.cs", "  Read CleanupOrchestrator.cs"],
    ["> add a regression test", "✴ Reading 1 file... (ctrl+o to expand)",
     "  |_ src\\CcDirector.Core\\Wingman\\WingmanService.cs"],
    ["> run the tests", "* Running tests... (12s . v 240 tokens)", "  dotnet test"],
    ["> fix the lint errors", "✽ Determining... (2s . v 61 tokens . thinking)"],
    ["> summarize the readme", "✶ Mulling... (6s . v 237 tokens)"],
    ["> build the project", "* Building and verifying... (1m 31s . v 1.2k tokens)"],
    ["> grep for the handler", "· Searching... (4s . ^ 88 tokens)", "  Grep \"HandlePipeEvent\""],
    ["> read three files", "  Read 1 file (ctrl+o to expand)", "✴ Concocting... (16s . v 833 tokens)"],
    ["> launch a subagent to investigate", "* Exploring... (22s . v 2.1k tokens . running 1 subagent)"],
    ["> write the new class", "✽ Tinkering... (9s . v 410 tokens)", "  Update FinishDetector.cs"],
    ["> check the git status", "· Computing... (1s)", "  Bash git status"],
    ["> analyze the diff", "✶ Pondering... (33s . v 1.8k tokens . thinking)"],
]
for i, body in enumerate(work_tools):
    foot = MODE_FOOTERS[i % 3] + " · esc to interrupt"
    rows = HEADER + [RULE] + body + [RULE, ">", RULE, foot]
    add("W", "working", rows, "active spinner + esc to interrupt footer")

# working with footer-less spinner (still working, no esc-to-interrupt visible but spinner+elapsed present)
for i in range(6):
    g = SPIN_GLYPHS[i % len(SPIN_GLYPHS)]
    rows = HEADER + [RULE, f"> task {i}", f"{g} {WORK_VERBS[i]}... ({3+i}s . v {50+i*40} tokens)", RULE, ">", RULE, MODE_FOOTERS[i % 3] + " · esc to interrupt"]
    add("W", "working", rows, "spinner with elapsed counter")

# working: tool output streaming, esc to interrupt
for i in range(6):
    rows = HEADER + [RULE, f"> do thing {i}", "  Bash npm run build", "  > tsc -p .", f"  compiled {i*3} files", f"* Building... ({i+2}s · esc to interrupt)", RULE, ">", RULE, MODE_FOOTERS[i % 3] + " · esc to interrupt"]
    add("W", "working", rows, "tool output streaming")

# torn working footer (box-dashes bleed into footer) - real artifact
for i in range(6):
    torn = f"✳-⏵⏵-byp{WORK_VERBS[i][:4].lower()}...({i+2}s tokens)-on-(shift+tab-to-cycle)-·-esc-to-interrupt".ljust(120, "-")
    rows = HEADER + [RULE, f"> torn frame {i}", f"✴ {WORK_VERBS[i]}... ({i+4}s . v {120+i*30} tokens)", torn, ">", torn]
    add("W", "working", rows, "TORN footer: box-dashes replace spaces in 'esc to interrupt'")

# ---------------------------------------------------------------- WAITING_FOR_INPUT (28)
idle_answers = [
    ["  Done. I updated the loader and added a regression test. Both build clean."],
    ["  The login bug is fixed. Three files were changed and the tests pass."],
    ["  Here is a one-line summary: it parses the manifest and validates each entry."],
    ["  17 times 23 is 391."],
    ["  I read README.md. It documents the build, the tools, and the Control API."],
    ["  Pushed to main. The commit is on origin now."],
    ["  Nothing further needed; the refactor is complete and verified."],
]
for i, ans in enumerate(idle_answers):
    rows = HEADER + [RULE] + ans + [RULE, ">", RULE, MODE_FOOTERS[i % 3]]
    add("I", "waiting_for_input", rows, "finished answer, empty prompt, mode footer, NO esc-to-interrupt")

# idle with placeholder text in the input box
for i in range(6):
    ph = ['> Try "fix the lint errors"', "> Try \"how do I log an error?\"", "> edit <filepath>"][i % 3]
    rows = HEADER + [RULE, "  Done.", RULE, ph, RULE, MODE_FOOTERS[i % 3]]
    add("I", "waiting_for_input", rows, "idle input box with faint placeholder text (not user activity)")

# idle: the famous bypass-footer regression (mode footer must NOT be read as permission)
for i in range(5):
    rows = HEADER + [RULE, "  Updated the config and verified it builds.", RULE, ">", RULE, "  ? for shortcuts" + " " * 35 + MODE_FOOTERS[i % 3].strip()]
    add("I", "waiting_for_input", rows, "REGRESSION: mode footer + '? for shortcuts' is idle, not permission")

# finished WITH a trailing question to the user (still waiting_for_input - turn over, agent asked something)
qs = ["  I can refactor the config loader. Want me to go ahead?",
      "  Should I commit these changes now?",
      "  Would you like me to add tests for the edge cases too?",
      "  That will delete the old file. OK to proceed?",
      "  I found two approaches. Which do you prefer - the cache or the rewrite?"]
for i, q in enumerate(qs):
    rows = HEADER + [RULE, "  I analyzed the loader.", q, RULE, ">", RULE, MODE_FOOTERS[i % 3]]
    add("I", "waiting_for_input", rows, "agent finished with a prose question; empty box; turn is over")

# idle after a longer reply that scrolled (stale 'esc to interrupt' high in scrollback, now idle)
for i in range(5):
    rows = HEADER + ["* Running tests... (esc to interrupt)", "  [tests passed]", RULE, "  All 42 tests pass.", RULE, ">", RULE, MODE_FOOTERS[i % 3]]
    add("I", "waiting_for_input", rows, "stale working footer in scrollback ABOVE a now-idle prompt")

# ---------------------------------------------------------------- WAITING_FOR_PERMISSION (24)
perm_boxes = [
    ("Do you want to make this edit to app-settings.json?",
     ["  > 1. Yes", "    2. Yes, and don't ask again this session", "    3. No, and tell Claude what to do differently (esc)"]),
    ("Do you want to run this command?  rm -rf build/",
     ["  > 1. Yes", "    2. Yes, and don't ask again", "    3. No (esc)"]),
    ("Do you want to create README-new.md?",
     ["  > 1. Yes", "    2. No, and tell Claude what to do differently (esc)"]),
    ("Allow Claude to use the WebFetch tool?",
     ["  > 1. Yes", "    2. Yes, for this session", "    3. No (esc)"]),
    ("Do you want to make this edit to Program.cs?",
     ["    1. Yes", "  > 2. Yes, and don't ask again this session", "    3. No (esc)"]),
]
for i, (q, opts) in enumerate(perm_boxes):
    rows = HEADER + [RULE, "  Edit file", "  config/app-settings.json", "    - old line", "    + new line", "", "  " + q] + opts + ["", MODE_FOOTERS[i % 3].strip()]
    add("P", "waiting_for_permission", rows, "real bordered numbered-choice confirmation box")

# [y/n] gates
for i in range(5):
    rows = HEADER + [RULE, f"  Proceed with the migration step {i}? [y/n]", RULE, ">", MODE_FOOTERS[i % 3].strip()]
    add("P", "waiting_for_permission", rows, "[y/n] inline gate")

# cc-director special picker menus (the 'Pick a joke' case) - waiting on a user decision
picker_titles = [("Pick a joke", "Which joke do you want to hear?",
                  ["  > 1. Option 1", "    2. A dad joke / pun that will make you groan.",
                   "    3. A short joke about coffee and Mondays.", "    4. Type something.", "    5. Chat about this"]),
                 ("Choose an approach", "How should I implement this?",
                  ["  > 1. Use a cache", "    2. Rewrite the loader", "    3. Leave it as is", "    4. Type something."]),
                 ("Pick a file", "Which file should I edit?",
                  ["  > 1. Program.cs", "    2. Startup.cs", "    3. Config.cs"])]
for i, (title, q, opts) in enumerate(picker_titles):
    rows = HEADER + [RULE, "  I created a question for you.", RULE, ">", RULE + " [ ] " + title, q] + opts + [RULE, "Enter to select . up/down to navigate . Esc to cancel"]
    add("P", "waiting_for_permission", rows, "cc-director picker menu: 'Enter to select ... Esc to cancel'")

# torn picker (the real captured 'Pick a joke' with tearing)
torn_menu = HEADER + [RULE, "> create a question with numbered choices", "✶ Tinkering...", RULE, ">",
                      RULE + " [ ] Pick a joke", "Which joke do you want to hear?", "  > 1. Option 1",
                      "    3. Optionj3ke / pun that will make you groan.", "       A short joke about coffee and Mondays.",
                      "    4. Type something.", RULE, "    5. Chat about this", "Enter to select . up/down to navigate . Esc to cancel"]
add("P", "waiting_for_permission", torn_menu, "REAL torn 'Pick a joke' picker (stale spinner verb present)")

# plan-mode ExitPlanMode style approval
for i in range(5):
    rows = HEADER + [RULE, "  Here is the plan:", "  1. Move the dictionary to the shared path.",
                     "  2. Update the loader.", "  3. Add a regression test.", "",
                     "  Would you like to proceed?", "  > 1. Yes, and auto-accept edits", "    2. Yes, and manually approve edits", "    3. No, keep planning", "", "  plan mode on (shift+tab to cycle)"]
    add("P", "waiting_for_permission", rows, "ExitPlanMode approval box")

# ---------------------------------------------------------------- CANCELLED (8)
for i in range(8):
    verb = WORK_VERBS[i % len(WORK_VERBS)]
    rows = HEADER + [f"* {verb}... (esc to interrupt)", "", "  [Request interrupted by user]", "", "> _", RULE, MODE_FOOTERS[i % 3]]
    add("C", "cancelled", rows, "interrupted by user, back at the prompt")

# ---------------------------------------------------------------- UNKNOWN (10)
# Genuinely unclassifiable: NO Claude banner, NO input box, NO spinner, NO footer - just
# garbled/sparse residue. A booting banner-only screen is NOT here (that reads as idle).
unknowns = [
    ["", "", ""],
    ["   "],
    ["█▀█▀ garbled ▀█", "x9x9x9", "...."],
    ["loading", ""],
    ["  ", "  ", "  "],
    ["[2J[H some raw escape residue [0m"],
    ["???", "???"],
    ["  a single ambiguous line of text with no footer at all"],
    ["████", "████", "████"],
    ["partial frame", "no footer", "no prompt"],
]
for u in unknowns:
    add("U", "unknown", u, "too sparse/garbled to classify (no banner, no box, no footer)")

# ---------------------------------------------------------------- a few more to reach 100
# MCP tool permission
add("P", "waiting_for_permission", HEADER + [RULE, "  Claude wants to use mindzie_list_projects (MCP).", "  > 1. Allow once", "    2. Allow for this session", "    3. Deny (esc)", "", MODE_FOOTERS[0].strip()],
    "MCP tool permission box")
# edit confirm with a longer diff
add("P", "waiting_for_permission", HEADER + [RULE, "  Edit file  src/Program.cs", "    -   var x = 1;", "    +   var x = 2;", "    +   var y = 3;", "", "  Do you want to make this edit to Program.cs?", "  > 1. Yes", "    2. Yes, and don't ask again this session", "    3. No (esc)", "", MODE_FOOTERS[1].strip()],
    "edit confirmation with multi-line diff")
# compaction in progress = working
add("W", "working", HEADER + [RULE, "  Compacting conversation... (esc to interrupt)", RULE, ">", RULE, MODE_FOOTERS[0] + " · esc to interrupt"],
    "compaction in progress")
# fresh booted session, idle and ready
add("I", "waiting_for_input", HEADER + [RULE, '> Try "edit <filepath>" or "how do I ...?"', RULE, MODE_FOOTERS[0]],
    "fresh session idle, ready for first instruction")
# idle right after compaction finished
add("I", "waiting_for_input", HEADER + [RULE, "  Compacted. Conversation summary saved.", RULE, ">", RULE, MODE_FOOTERS[2]],
    "idle immediately after compaction completed")

out = os.path.join(os.path.dirname(__file__), "states.json")
with open(out, "w", encoding="utf-8") as f:
    json.dump({"count": len(states), "states": states}, f, ensure_ascii=False, indent=1)
print(f"wrote {len(states)} states -> {out}")
by_cat = {}
for s in states:
    by_cat[s["expected"]] = by_cat.get(s["expected"], 0) + 1
print("by expected:", by_cat)
