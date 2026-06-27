# Shipped Tools Review

## Executive summary

The shipped set from `tools/registry.json` is nine command-line tools: `cc-pdf`, `cc-html`, `cc-word`, `cc-gmail`, `cc-outlook`, `cc-playwright`, `cc-vault`, `cc-comm-queue`, and `cc-devthrottle`.
The biggest shipping risk is packaging: `cc-comm-queue` uses a distribution name that does not match the registry name, so the Python bundle dependency collector can omit its unique dependency.
The second broad risk is output consistency. Most tools still have human-only output for core commands, with partial or missing JavaScript Object Notation modes.
Several tools use Rich tables and panels that can emit Unicode box drawing, while the house rule requires ASCII-only output.
The document conversion tools are simple and useful, but they lack quiet and machine-readable modes, overwrite protection, and consistent conversion options.
The communication tools have the deepest user value, but they also have the most inconsistent command shapes and parsing behavior.
`cc-devthrottle` is a good unifying surface, but its setup commands can mix human progress text into JavaScript Object Notation mode.
The items below are prioritized for shippability, automation reliability, and support burden.

## Prioritized improvement items

### High priority

#### 1. Fix `cc-comm-queue` package name mismatch in the Python tools bundle

- Tools affected: `cc-comm-queue`, build scripts.
- What is wrong: `tools/cc-comm-queue/pyproject.toml` declares `name = "cc_comm_queue"`, while `tools/registry.json` uses `cc-comm-queue`. The bundle dependency collector filters shipped tools by directory and registry name, then excludes in-house packages by normalized registry names. This can leave `pydantic` out of the shipped wheelhouse because it is unique to `cc-comm-queue` among shipped tools.
- Why it matters: A clean install can fail to install or run `cc-comm-queue`, even though the registry says it ships.
- Suggested fix: Rename the project distribution to `cc-comm-queue`, or update the bundle collector to normalize registry names, directory names, and project names through one shared function. Add a bundle test that installs only the release wheelhouse and runs `cc-comm-queue --version` and `cc-comm-queue add-json --help`.

#### 2. Enforce ASCII-only Rich output across all shipped tools

- Tools affected: `cc-vault`, `cc-gmail`, `cc-outlook`, `cc-devthrottle`, `cc-comm-queue`, and any command that prints Rich tables or panels.
- What is wrong: Rich defaults can emit Unicode box drawing in terminal output. Running `tools/cc-vault/main.py --help` produced box characters. The repository also contains a Unicode ellipsis in `tools/cc-playwright/LINKEDIN_POSTING.md`.
- Why it matters: The product rule says output must be ASCII-only. Unicode output can break logs, command transcripts, older terminals, and automated parsers.
- Suggested fix: Create a shared console factory that forces ASCII-safe Rich boxes and disables Unicode decorations. Use it in every shipped tool. Add automated checks that run `--help`, `--version`, and one representative table command per tool and fail on non-ASCII bytes.

#### 3. Add consistent JavaScript Object Notation output to core commands

- Tools affected: `cc-gmail`, `cc-outlook`, `cc-vault`, `cc-comm-queue`, `cc-devthrottle`, document tools.
- What is wrong: Machine-readable output is partial. Examples: `cc-gmail accounts list --json` exists, but `list`, `read`, `search`, `send`, and calendar or contacts commands mostly print human text. `cc-outlook` has `accounts list --json` and `recipients --format json`, but not consistent `--json` on mail and calendar commands. `cc-comm-queue` has JavaScript Object Notation for some commands but not `list`, `status`, or state transitions. The document tools have no machine-readable mode.
- Why it matters: These tools are used by agents and scripts. Without consistent output, callers scrape text and break when wording changes.
- Suggested fix: Define a shipped tool output contract: every read command supports `--json`; every mutation with `--json` returns `{ "success": true, ... }` or `{ "success": false, "error": ... }`; human progress goes to standard error; data goes to standard output. Add exit code tests.

#### 4. Make `cc-comm-queue` validation strict instead of silently defaulting

- Tools affected: `cc-comm-queue`.
- What is wrong: `list --status` maps unknown status values to no filter, so a typo can show all items. `add --linkedin-visibility` silently falls back to public on invalid input. Some platform-specific required fields are optional until later review, so users can create incomplete queue items.
- Why it matters: Approval queues need high trust. Silent broadening or silent defaulting can cause reviewers or posting automation to act on the wrong item.
- Suggested fix: Reject unknown status, visibility, audience, privacy, and platform-specific values with exit code 2 and a clear valid-values list. Add validation for required platform fields such as Reddit subreddit and title for Reddit posts, email recipient and subject for email items, and recipient profile data for LinkedIn messages.

#### 5. Prevent mixed human progress in `cc-devthrottle setup --json` flows

- Tools affected: `cc-devthrottle setup install`, `setup update`, `setup repair`.
- What is wrong: Setup commands accept `--json`, but `run_setup_cli` prints human text such as "Setup CLI not found locally" and "Delegating to setup engine" before delegating. Download progress also writes human progress.
- Why it matters: A caller that requests JavaScript Object Notation can receive invalid mixed output, which makes automation unreliable during install and repair.
- Suggested fix: In JavaScript Object Notation mode, send progress to standard error or suppress it, and ensure standard output is only valid JavaScript Object Notation from the setup engine. Add a dry run test that parses standard output as JavaScript Object Notation.

### Medium priority

#### 6. Standardize count and limit flags across shipped tools

- Tools affected: `cc-vault`, `cc-comm-queue`, `cc-gmail`, `cc-outlook`.
- What is wrong: Similar commands use different names and aliases: `--count`, `--limit`, `-n`, and positional defaults vary. Project guidance says result limits should use `--count` or `-n`, not `--limit`, but several shipped surfaces expose `limit` semantics only through `-n` or option help text.
- Why it matters: Users and agents move between tools constantly. Inconsistent names increase mistakes and support cost.
- Suggested fix: Keep existing flags for compatibility, but add `--count` everywhere a result count is requested. Mark `--limit` as a hidden compatibility alias where it already exists. Update help text to use one wording.

#### 7. Add quiet and overwrite controls to document converters

- Tools affected: `cc-pdf`, `cc-html`, `cc-word`.
- What is wrong: Conversion commands always print step-by-step human progress and overwrite output paths without an explicit `--force` or `--no-clobber` choice. They also lack a quiet mode for script use.
- Why it matters: Document conversion is often used in build and publishing scripts where clean logs and accidental overwrite protection matter.
- Suggested fix: Add `--quiet`, `--force`, and `--no-clobber` with a clear default. In non-quiet mode, keep progress on standard error and reserve standard output for requested data.

#### 8. Make `cc-playwright snapshot --full` do something or remove it

- Tools affected: `cc-playwright`.
- What is wrong: The command declares `snapshot --full`, but `cmd_snapshot` ignores `args.full` and always returns the same interactive-element list.
- Why it matters: Users will assume `--full` returns a fuller page snapshot, then debug the wrong problem when it does not.
- Suggested fix: Either implement full mode with broader visible text, accessibility tree, or document object model summary, or remove the flag until the feature exists. Add a command test that proves the output differs when `--full` is used.

#### 9. Add a safer, cross-platform browser close path to `cc-playwright stop`

- Tools affected: `cc-playwright`.
- What is wrong: On Windows, `stop` uses `taskkill /F /T /PID`. This is forceful and Windows-specific. It may skip browser cleanup and can kill child processes under that process tree.
- Why it matters: The tool manages persistent profiles and cookies. Forced termination increases profile-lock and corruption risk.
- Suggested fix: First try graceful browser close through the debugging protocol or a normal termination signal, wait briefly, then force only the recorded process if needed. Report which path was used in the JavaScript Object Notation result.

### Low priority

#### 10. Unify version strings and health checks

- Tools affected: all shipped tools.
- What is wrong: Version output varies between `cc-tool version 0.1.0`, `cc-tool v1.3.0`, and argparse version output. Several tool package versions are still `0.1.0` even though they ship as part of a versioned product.
- Why it matters: Support and install diagnostics need a single format to compare tool health across machines.
- Suggested fix: Standardize `cc-tool <product-version> (tool <tool-version>)` or a similar ASCII-only format. Add installer health checks that run every shipped tool with `--version` after install.

#### 11. Improve discoverability for installed setup and authentication documents

- Tools affected: `cc-gmail`, `cc-outlook`, `cc-playwright`.
- What is wrong: Help text points to repository files such as readme documents or docs paths, but installed users may not know where those files live.
- Why it matters: Authentication and browser setup are the hardest first-run experiences.
- Suggested fix: Add `doctor` or `setup check` commands for each tool that prints installed doc paths, account state, and next actions. Ensure every referenced path exists in the shipped package or is a web address.

#### 12. Add command surface snapshot tests

- Tools affected: all shipped tools.
- What is wrong: There is no single proof that the registry, bundle manifest, script entry points, `--help`, `--version`, ASCII-only output, and JavaScript Object Notation contracts stay aligned.
- Why it matters: The shipped tool set is small enough to gate thoroughly, and regressions are easy to catch before release.
- Suggested fix: Add a release test that reads `tools/registry.json`, installs the built bundle into a clean environment, and runs standard smoke commands for each shipped tool. Store normalized snapshots for help and version output.
