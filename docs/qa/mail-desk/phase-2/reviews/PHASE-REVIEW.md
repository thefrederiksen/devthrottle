# Mail Desk phase 2: review of the finished phase

Reviewed: pull request 4224, branch `origin/mail-desk-phase-2` at commit `c970e172e`, as the whole change against
`origin/main` at `839e0fb5d` (26 files, 4,889 lines added, 5 removed). Reviewer seat opened by the Delivery Lead
(session 44afaef9); this review ran on a different agent family from the one that wrote the code, as the method
requires. The Developers' pieces
were reviewed one by one before this (`screen-review.md`, `template-review.md`, and their answers); this review judges
the phase as a whole and did not take those reviews' word for anything it could check itself. Date: 18 October 2026.

## Verdict

**Accepted. No finding.** I tried to break the request guard and could not: nothing but a read of
`graph.microsoft.com` and the move of one message to Archive or Junk Email leaves `screen.py`, and no move is made
unless the channel's mode is exactly `live`. I could not construct a run in which the cursor passes a message that was
not reported, or in which a message is skipped or reported twice across batches, including when a batch is cut inside
one second and when the first-run cap cuts a tie. The sign-in lookup fails closed on every path I could find. The
template is a draft that nothing can publish from the Gateway's side and that no production code calls yet. Nothing
committed names a real address other than `email@devthrottle.com`, a real domain, a token, any body text, or an
assistant. Both suites pass on this machine.

A review may return nothing, and this one does. Four notes for the Delivery Lead follow; none shows a harm in this
phase's code, and none must change before merge.

## Scope

### What I read

- The whole of `screen.py` (776 lines), `factory.yaml`, `seats/mail-desk.yaml`, `MailDeskTemplate.cs`,
  `MailDeskTemplateOffer.cs`, and the diffs to `.gitattributes`, `CcDirector.Gateway.csproj`,
  `FactoryDefinitionEndpoints.cs`, `FactoryDefinitionStore.cs` and `FactoryBriefPanelFold.cs`.
- Every test file: the eight Python files under `tests/` and the two C# files under
  `src/CcDirector.Gateway.UnitTests/MailDesk/`.
- `CONTRACT.md`, `README.md`, and the four review and answer files in `reviews/`.
- The mission document on `origin/main`: sections 4 (decisions D1 to D22), 5.3, 5.4 with its phase 0 findings, 5.5,
  5.6, the phase 2 row of section 6, and section 7. The phase 0 README.
- On `origin/main`, the code the check and the template are judged against: `TriggerCheckContract.cs` (the 200
  character reason window, `OneLine`, `MaxContextBytes` = 8,192), `TriggerDtos.cs` (`OutputCap` = 16,384 characters,
  `StateCapBytes` = 65,536, the 300 second timeout of a due check), `DefinitionCheckRunner.cs` (the state file is
  written from the trigger's stored state before every run and named by `CC_CHECK_STATE_FILE`), `TriggerService.cs`
  and `FactorySeatStart.cs` (a seat start is built only from the PUBLISHED version; a count of zero is "nothing to do"
  and any other count starts the seat), `FactoryDefinitionEndpoints.KindOf` (an HTTP caller is a person, a session or
  neither, so no caller can claim the new `gateway` author kind), and every reader of a definition version's author
  kind in the Gateway, the web clients and the command line tool (all pass the string through; only the brief panel's
  `Who` puts words to it, and it was taught the new kind).

### What I ran

All in my own worktree cut from `origin/mail-desk-phase-2`, on devlinux, with no real mailbox, no real token and no
network call of mine.

| Run | Command | Result |
|---|---|---|
| The check's tests | `uv run --no-project --with pytest --with 'msal>=1.28' python -m pytest docs/qa/mail-desk/phase-2/tests -v` | **210 passed** in 2.65 seconds |
| The Mail Desk C# tests | `dotnet test -p:EnableWindowsTargeting=true --filter Category=MailDesk` in `src/CcDirector.Gateway.UnitTests`, .NET 10, `DOTNET_USE_POLLING_FILE_WATCHER=1`, a scratch `LOCALAPPDATA` | **26 passed, 0 failed** in 11 seconds |
| My own probes through the test rig | 25 cases, run from a file placed beside the tests and removed afterwards; nothing of mine is committed | see below |
| Sweeps of the diff and of the nine commit messages | address pattern; the owner's domains and names; token shapes; `Claude`, `Anthropic`, `Co-Authored`, `Generated with`, the robot emoji | see below |
| Line endings | `git ls-files --eol` and a carriage return count on the three template files | all three are line feed only, in the index and the worktree, with the attribute applied |

### What I probed, beyond the committed tests

- **The guard.** Twenty-two shapes the committed tests do not send: the host in capitals, with a trailing dot, with
  an explicit port 443; the move path with a trailing slash, a `;x` suffix, a capital `Move` or `ME`; an id holding a
  `;` or an Arabic-Indic digit; a body whose destination is a list or null; the method `Post`, `POST ` with a trailing
  space, `OPTIONS`, `TRACE`; a GET to `/v1.0` itself, to `/v1.0x/`, to `//v1.0/`, with `..` before the version; a
  move address ending in a line separator (U+2028). Twenty were refused before the network. The two that reached the
  fake Graph were the move address with an empty query (`.../move?`) and with an empty fragment (`.../move#`):
  `urlsplit` reads both as the move path with nothing after it, and what is sent is the same move of the same message
  to the same folder, so neither is a way past the guard. The check never builds such an address.
- **The first-run cap on a tie.** 250 messages all received in one second: the first run's cap of 200 cuts the tie,
  and later runs re-read the second and report the other 50 too. More than the cap, but nothing skipped and nothing
  twice. Not a harm.
- **Two channels, one in observe and one in live mode, each with a queued move:** only the live channel's move was
  sent, with that channel's token; the observe channel's queue and Inbox were untouched.
- **A mode of `LIVE`** (capitals): a failed check before any sign-in or network call.

### The sweeps

- **Addresses.** Every address in the diff is `email@devthrottle.com` (ours, allowed by the mission), an `example.com`,
  `example.org` or `.example` address, `someone@graph.microsoft.com` as user information in a guard test, or the pair
  `a.b@c.com` and `a@b.c.com` used to describe cc-outlook's file-name collision. The owner's second mailbox is named
  nowhere, as D19 requires.
- **Domains, tokens, body text.** No real domain of the owner's, no token-shaped string, no Graph id of real length,
  and no text from a real email. The one client id is cc-outlook's own Azure app, already on `origin/main` in the
  phase 0 proof scripts.
- **Attribution.** None in any commit message or file. The two hits of the sweep are `agent: ClaudeCode` in
  `factory.yaml`, which is the product's own agent identifier used by every shipped definition example, and the name
  of the repository's `CLAUDE.md` in a review document, which is a file reference. Neither is an attribution.

### What I could not reach

- **A real mailbox, a real token, a real Director run.** Everything here ran against the fake Graph and a fake MSAL
  application; phase 0 proved the sign-in premise with a real Director and phase 6 runs the real thing. That Graph
  answers 404 for a moved message's old id is the fake's behaviour, as the contract says.
- **Windows and macOS.** Built and run on Linux only.
- **The HTTP pipeline** of the import and create routes: the tests call the handlers directly, as the existing
  definition tests do.
- **Phase 1's code.** Only its contract exists; the switch the offer waits for is not on `origin/main`.
- **The Gateway's `OneLine` as C#.** The tests reproduce it in Python; I compared the reproduction with the C# line by
  line and it matches.
- **The alphabet of a real Graph message id.** The guard admits letters, digits, `-`, `_` and `=`. Phase 0 committed no
  id (by design), so I could not confirm a real id holds nothing else. See note 2.

## The questions the brief asked, answered

1. **Can anything in `screen.py` change or send mail outside live mode, or send anything but GET and the two moves?**
   No. Every Graph call goes through `graph_request()`, which calls `check_request()` before `transport()`; `urlopen`
   appears once in the file, inside `transport()`, and a test reads the source to keep it so. `check_request` refuses,
   before parsing, any address holding a character at or below 0x20 or 0x7F; then anything not `https` to exactly
   `graph.microsoft.com`; a fragment; `..`, `%` or `\` in the path; a GET with a body or outside `/v1.0/`; and every
   POST that is not `/v1.0/me/messages/{id}/move` with an id of only `[A-Za-z0-9_=-]`, no query, and a body that is
   exactly one key `destinationId` of `archive` or `junkemail`. Every other method is refused by name. The only caller
   of the move is `file_queue`, reached only in the `elif` branch after `mode == "observe"`, and `read_memory` admits
   only the exact strings `observe` and `live`. MSAL's own traffic asks for `Mail.ReadWrite` only. Python's redirect
   handler cannot turn a request into a write.
2. **Does the cursor ever advance past what was reported, or skip or repeat a message across batches?** No.
   Candidates are sorted by receive time, then channel, then page order, and the reported set is a prefix of that
   order, so each channel's reported messages are a prefix of its own oldest-first list. The cursor becomes the newest
   reported time plus every id reported at exactly that time, merged with the ids it already held when the time did
   not move; the next run reads `ge` that time and drops the held ids. A cut inside one second, a failure after a move,
   new mail during a backlog, two channels sharing the ten, the 7 KB budget cutting at 7 and at 4 messages, and my
   250-in-one-second first run all come out with nothing skipped and nothing twice.
3. **Does the sign-in lookup fail closed?** Yes. The home folder comes from the account record, never `HOME`; the base
   is the Director's per-user folder with no fallback; none found, more than one found, a file that is not a cache, a
   cache holding no account, a renewal that fails, and the sign-in service out of reach or answering an error are each
   a non-zero exit with the memory byte for byte unchanged and no Graph call. The fix names cc-outlook's own nickname
   or, when cc-outlook's lookup would lead elsewhere, the address as cc-outlook holds it; never a guess.
4. **Is the template inert until published?** Yes, twice over. The offer only ever calls `SaveDraft`; `Publish` and
   `Restore` keep `RequireAWriter`, which throws on the `gateway` kind (tested); a seat start is built only from a
   factory's published version (`FactorySeatStart.ReadPublished` on `origin/main`); and the offer has no production
   caller at all until phase 1's switch exists. No HTTP caller can claim the `gateway` kind.
5. **Does anything commit a real address, domain, token, body text or attribution?** No; the sweeps above.

## Notes for the Delivery Lead (not findings; nothing in this phase must change)

1. **Publishing the factory before phase 3 is deployed would start sessions.** On `origin/main` a check report with a
   count above zero starts the seat from the published version, and in observe mode the check's count is the number
   of new messages, not zero. So "nothing runs until it is published" is true, but a publish alone, with phase 3's
   `screened` outcome not yet in the Gateway, would start a Mail Desk agent on the next run with new mail, which D17
   forbids in production. The mission already orders phase 3 before phase 6; this note is only so the order is held
   as a hard dependency, and because the phase 2 README does not say it.
2. **One real Graph id would settle the guard's id alphabet.** The guard admits `[A-Za-z0-9_=-]`, which is the
   URL-safe form Graph documents, but no committed proof shows a real id. If a real id held another character, the
   failure is safe (that move is `refused` for that item and nothing is sent) but live-mode filing would then never
   move anything in that mailbox. Phase 4's rig uses the fake, so the first real test would be live mode in
   production. One real id looked at in phase 6, against the pattern, closes it; nothing need be committed.
3. **I agree with the README's open points 1 and 4** (one mailbox's failure stops every mailbox; a message entering
   the Inbox with an older receive time is never reported). Both follow from the mission's design, both are written
   into the contract, and both are the Delivery Lead's to decide before phase 6 runs two mailboxes.
4. **The gate's verdict on this machine is NEVER RAN,** as the README says plainly, because the Windows-only suites
   cannot pass on Linux. The README's suite-by-suite comparison with unchanged `origin/main` is the right substitute
   and I did not repeat it; I re-ran only the two suites this phase added to. A Windows gate before the release that
   carries this phase would cover what Linux cannot.

## For the record

- Branch of this review: `mail-desk-phase-2-review`, cut from `origin/mail-desk-phase-2` at `c970e172e`; this file is
  its one change. The probes are reproducible from the descriptions above with the test rig's `world` fixture.
- Python 3.11, MSAL 1.39, uv at `~/.local/bin`; .NET 10 at `~/.dotnet`.
