# Command-line interface conformance proof for issue 4385

The on-demand run completed on devlinux on 24 October 2026 against Claude Code 2.1.310 and Pi 1.2.0.
`current-run/expected-cost.txt` was written before the run. `current-run/conformance.log` records all thirteen
applicable fixed states passing, and `current-run/exit.txt` records exit code zero. The harness then copied the passing
fixtures to `fixtures/passing/` and added the exact versions to
`src/CcDirector.Core/Agents/cli-conformance-passed.json`.

The older-build control is `fixtures/claude-2.1.293-claude-code-model-picker.json`, reduced from the saved earlier
model-picker shape. Replaying it exits one: its `Enter to confirm` footer does not provide the current
`enter-to-set-default` answer.

Pi 1.2.0 hides its terminal cursor in this run. The Pi composer classifier therefore deliberately answers `NotFound`
for idle and multi-line composer screens: its positive rule requires a visible cursor so a partial repaint cannot be
mistaken for an editable composer. The fixtures pin that command-line interface limitation rather than weakening the
reader.

Codex expectations and driving cases are implemented, but no Codex version is in the passing manifest. Its real run is
pending until the owner's Codex allowance resets on 18 November 2026. Until then Director start-up logs the warning and
continues; it never blocks a Codex session.
