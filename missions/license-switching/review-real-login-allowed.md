# Review findings - License Switching: let a Director with the setting on use the real login

Reviewed: the uncommitted diff in this worktree (9 files), read against the surrounding code on this branch.
No code was edited. Findings are ordered most severe first. Severity words: Medium, Low.

Short answer: the change does what the brief says, and every path that opens the store or starts the tool
carries the allowance. Nothing leaks through the change's own code. The two things worth fixing before merge
are a missing belt-and-braces strip of the variable from session environments, and a C# test whose second
assertion cannot fail. The rest is low.

---

## 1. Medium - the variable is not on the session strip list, so a Director that inherits it hands it to every session

**Where:** `src/CcDirector.Core/Sessions/InheritedSessionEnvironment.cs:33` (`IsStripped`),
`tools/cc-devthrottle/src/license_store.py:599` (the refusal text).

**Failure scenario.** The confinement rests on one assumption: the Director's own process never has
`CC_LICENSE_ALLOW_REAL_DEFAULT` set. The diff keeps that true for the Director's own code, but nothing keeps it
true for how the Director is started. `InheritedSessionEnvironment` says in its own header that "a Director is
often itself started from inside a session" or from a shell, and copies the whole process environment into every
session. So:

1. The owner types `cc-devthrottle license usage` by hand, is refused, and the new refusal text now names the
   variable: "only a Director with license switching turned on may use it (it sets CC_LICENSE_ALLOW_REAL_DEFAULT)".
   The obvious workaround is to set it in the shell. They set it and later start a Director from that shell, or
   put it in the user environment so hand-typed commands keep working.
2. Every session that Director starts inherits it. A command typed in a session - `license switch`, `license
   remove` - now runs against the real login, which is the exact case the brief says must still be refused.

The same shape has already happened here with `CC_FACTORY_MEMORY_DIR` (8 October 2026): the SessionManager
correctly did not set it; nothing removed the inherited one. The class exists so adding a name is one line.

**Fix.** Add the name to `IsStripped` (one `Equals` line, next to `FactoryMemoryFiles.DirectoryEnvVar`) with a
test in the `InheritedSessionEnvironment` suite, so a session can never see it no matter how the Director was
launched. The Director's own runs of the tool are unaffected: `ProcessRunner` and the sign-in `ProcessStartInfo`
set it on the child directly, not through the session environment. Consider also leaving the variable's name out
of the refusal sentence (keep the next step "pass an isolated folder"), so the message does not teach the
workaround; the name is documented in `license_store.py` for anyone who needs it.

---

## 2. Medium - the C# test's confinement assertion cannot fail, and its off case depends on the host environment

**Where:** `src/CcDirector.Core.UnitTests/Licenses/LicenseCommandLineTests.cs:187-200`
(`ReadUsageAsync_AllowRealDefault_IsInTheToolsEnvironmentOnlyWhenOn`).

**Failure scenario.** The second assertion, `Assert.Null(Environment.GetEnvironmentVariable(...))`, is meant to
prove the variable "never leaks into the Director's own environment". It cannot fail for any implementation of
`ToolEnvironment` or `ProcessRunner`: `ProcessStartInfo.Environment` is a copy and never writes the process
environment, so the assertion passes with the feature deleted, with the variable set process-wide by a future
change in another class, or with the dictionary written to the wrong child. It is a check answerable without the
evidence. The brief lists it as proof that the variable does not leak; it is not that proof.

Separately, both `InlineData` rows read the host environment: on a machine or runner where
`CC_LICENSE_ALLOW_REAL_DEFAULT` happens to be set (finding 1 describes how that comes about), the `false` row
sees "1" from the tool and the `Assert.Null` fails on correct code. Under the owner's test rule that is a test to
delete, not repair.

**Fix.** Replace the `Assert.Null` with the claim that matters: after the allowed run, start the same recording
tool once more through the plain `ProcessRunner.RunAsync` overload (no environment dictionary) and assert it saw
"" - that proves one allowed run leaves nothing behind for the next child, which is the session-shaped claim.
For the host-environment dependence, have `RecordingTool.Create()` fail loudly (or the test skip with a reason)
when the variable is already set in the process, so the test never reports a code defect for a machine condition.

---

## 3. Low - the tool's own children inherit the allowance

**Where:** `tools/cc-devthrottle/src/license_signin.py:143` (`ClaudeLogin`: `env = dict(os.environ, ...)`) and
`:406` (`_run_harness`: `full_env = dict(os.environ, **env)`).

**Failure scenario.** When the Director runs `license sign-in` or `license renew` with the allowance, the tool
starts `claude auth login` and `browser-harness` with its whole environment, so both - and the browser profile
the harness drives - run with `CC_LICENSE_ALLOW_REAL_DEFAULT=1`. Neither runs `cc-devthrottle` today, so there is
no path to the real login through them. But `claude auth login` is Claude Code, and a later change that approves
through an agent child (a `claude -p` helper, say) would be born allowed, with nothing in the tool saying so.

**Fix.** In both places, drop the variable from the child environment:
`env.pop(ENV_ALLOW_REAL_DEFAULT, None)` after building the dictionary. The allowance is for the store the tool
itself opened, never for anything it starts.

---

## 4. Low (unverified) - the switch and a running session both rewrite `~/.claude.json` whole

**Where:** `tools/cc-devthrottle/src/license_store.py:288` (`_write_oauth_account`) and `:295`
(`_clear_oauth_account`), reached from `switch` via `_step_copy`.

**Failure scenario.** For the real default, the account file is `~/.claude.json` - Claude Code's whole user-scope
state (project list, onboarding, user-scope MCP servers). The tool reads it, replaces `oauthAccount`, and writes
the whole file back. That read-modify-write is correct in isolation and never ran beside a live Claude Code
process in the isolated-folder phases. On the real login a Director's sessions are usually running, and Claude
Code also rewrites that file whole from its in-memory copy. If a session writes after the switch, the OLD
`oauthAccount` lands next to the NEW key: `list` and `usage` then report the previous account as active, the next
`switch --expect-active` refuses with `active-account-changed`, and the Gateway's picture disagrees with the
machine until something rewrites the block. The key itself (in `.credentials.json`) is not at risk; the switch
has verified before any session could write.

I have not observed Claude Code put `oauthAccount` back; this is a risk to check, not a reported defect.

**Fix.** Verify once on the owner's laptop: with a session open, run a switch, let that session finish a turn,
then read `oauthAccount.emailAddress` in `~/.claude.json`. If it reverted, the tool needs to reconcile a key whose
account file disagrees (re-read the account from the key's own `oauthAccount` source, or treat the disagreement
as a named condition in `list`), and the reporter should say so rather than report the old account as active.

---

## 5. Low - stale sentence in the `license_ops` module docstring

**Where:** `tools/cc-devthrottle/src/license_ops.py:7-9`.

The docstring still says the default config folder is "`~/.claude` (which the store refuses in this release, so
every run names an isolated folder)". That is no longer true after this change; the `license_store.py` header,
the `cli.py` help and the C# comments were all updated, this one was missed.

**Fix.** Say what the store header now says: refused unless a Director with license switching on runs the command.

---

## Looked at and found no defect (so the next reader need not repeat it)

- **Every store construction (question 1).** Python has exactly one `LicenseStore(` construction,
  `license_ops._open`; `license_usage_ops._open` and every `license_signin_ops` command reuse it. The Keychain
  backend gets the same flag through `backend_for_platform(allow_real_default)` at `license_store.py:607`, and
  its `_service` guard reads it. `ClaudeLogin` already unsets `CLAUDE_CONFIG_DIR` for the real default so the
  login names the real Keychain item. C# has one production construction, `LicenseSwitching.CreateForThisMachine`,
  and two launches of the tool, `RunForJsonAsync` and `StartSignInAsync`; both carry `ToolEnvironment()`.
  `license approve` runs with `isolated: false` but still gets the environment and opens no store.
- **Confinement through the change's own code (question 2).** `ProcessRunner` sets the dictionary on
  `ProcessStartInfo.Environment` with `UseShellExecute = false`; the sign-in path does the same. Neither touches
  `Environment.SetEnvironmentVariable`. Sessions are built from `Environment.GetEnvironmentVariables()` of the
  Director, which this change never alters. The only gap is the inherited case in finding 1.
- **Setting off (question 3).** `AllowRealDefault` defaults to false; `ToolEnvironment()` is then empty;
  `ProcessRunner`'s new overload adds nothing for null or empty, and the old overload delegates with null. The
  refusal sentence changed wording only, and both tests that read it match on "real default". The coordinators'
  `_enabled` guards and `LicenseSwitching.Start` are untouched.
- **Destructive operations on the real login (question 4).** Switch: `_step_copy` refuses to overwrite a key it
  cannot prove is held elsewhere, and `_verify_switch` reads back. Renewal of the active account: runs
  `claude auth login` straight into `~/.claude` under the store lock, after `finish_sign_in` checks the folder
  still holds that account in that role; both approvers (browser profile here, the other machine's
  `license approve`) check the shown account matches the email BEFORE clicking Authorize, and `finish_sign_in`
  refuses a wrong-account read-back afterwards. The spare refresh never writes the active key
  (`license_usage.py:477`). Phase 1's live-swap proof covers a running process moving on its next call. No
  missing guard found beyond finding 4.
- **The Windows stand-in script.** In a batch file an undefined `%CC_LICENSE_ALLOW_REAL_DEFAULT%` expands to
  nothing, so the off row reads `[]` and the helper returns ""; the on row reads `[1]`.
- **Python tests.** The `machine` fixture forces the file backend on every platform, so the new allowed-case test
  never touches a Mac's Keychain; the stand-in home keeps it off the real login. The four refused values are
  refused in the constructor before any read.
