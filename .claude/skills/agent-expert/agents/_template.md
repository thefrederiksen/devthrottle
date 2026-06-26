<!--
Per-agent reference template. Copy this to agents/<agent>.md and fill EVERY section.
Rules:
- ASCII only. No Unicode, no emoji, no em-dashes (use " - "). This repo forbids non-ASCII.
- Mark every non-trivial fact [VERIFIED from docs] or [INFERRED/UNCERTAIN].
- Link the source inline AND collect all links in the Sources section.
- Prefer official docs and source repos over blog posts.
-->

# <Agent Display Name> (<AgentKind>)

> One-line summary. Our integration status: <driver class> / <plugin class>.

## 1. Identity and install
- Binary name, npm package or install command, source repository, official documentation home.
- Windows install location of the launchable shim, and the version-probe command.

## 2. Command-line interface
- Interactive vs headless/print/non-interactive mode.
- Flag table: model flag, session-id / resume, output format (json / stream-json), auto-approve,
  system-prompt flags, anything we pass or could pass at launch.

## 3. Configuration
- Config files, their locations, and precedence (global / user / project / session).

## 4. Context injection (how to inject a preamble)
- Instruction / context files (AGENTS.md, CLAUDE.md, tool-specific), system-prompt flags,
  and hook-based additionalContext.
- For each: does it survive /clear? does it survive /compact?

## 5. Lifecycle events and hooks
- Full event list. Config file format and location. The stdin/stdout JSON contract.
- Which events fire on: startup, resume, clear / new, compact.
- Can a hook's output inject model context (additionalContext or equivalent)? Field name.

## 6. SDK / programmatic API / server mode
- Libraries, RPC / server / event-stream, endpoints or methods, auth.

## 7. MCP / extensions / plugins / skills

## 8. Transcript / history
- Location, file format, whether parseable, token-usage availability.

## 9. Session semantics
- clear vs compact vs new vs resume. What each does. Session-id behavior across them.

## 10. How CC Director integrates it
- Our classes: driver, agent, plugin. Declared driver capabilities. History provider kind.
- Fleet-preamble strategy family (A shell-hook / B event-bus / C extension / D instruction-file).
- Concrete plan to inject the fleet preamble at launch and re-inject on clear/compact.
- Current gaps.

## 11. Caveats and verification needed
- Recency / churn, unverified field names, behaviors to confirm live against the installed binary.

## Sources
- Every documentation URL used, bulleted.
