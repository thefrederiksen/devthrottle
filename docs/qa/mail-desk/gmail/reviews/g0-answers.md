# Answers to the review of the G0 proof

Answering `g0-review.md` (reviewed commit `82bc12b7d`). Date: 21 October 2026. All four findings are accepted.
The probe changes are in commit `bca238290`; the transcript was then made again from that commit, and the README
updated from it.

## 1. The knownSender search takes the sender's address unchecked - ACCEPTED

The Tech Lead ruled: keep way (b), with a strict plain-address pattern before any search (otherwise not known), and a
hit confirmed against the found Sent message's To, Cc and Bcc headers.

Done in the README's recommendation (section f), which now states both conditions as part of the recommendation, and
in the probe, so the real mailbox numbers include them:
- `PLAIN_ADDRESS` in `gmail_probe.py`: letters, digits and `. _ % + -` before the `@`, not starting with a hyphen, and
  dot-separated labels of letters, digits and hyphens after it. No spaces, quotes, parentheses, braces, brackets,
  asterisks or colons. Anything else returns `not-plain` with no search made.
- `known_by_search`: the search, then for a hit one metadata GET (To, Cc, Bcc) of the found message; known only when
  `getaddresses` of those headers holds exactly the address.
- Tests: nine addresses that are not plain, including every example of the review (`a b@...`,
  `gmail.com or x@...`, a quoted local part with `label:sent`, `*@...`), never reach the transport; a look-alike
  recipient is `hit-not-confirmed`; a no-hit makes one call only. With the pattern loosened to match anything, 9 tests
  fail.
- On the real mailbox: 10 senders, 0 not plain, 8 no hit, 2 hits both confirmed; 2.8 seconds in all, including the 2
  confirming GETs (before: 2.4 seconds without them).
- "What is NOT proven" now names that the search's exactness on look-alike addresses was not measured against Gmail,
  and that the confirmation is proven by the tests only.

## 2. A refused refresh names the placeholder `<nickname>` - ACCEPTED

`post_token_refresh` now takes the nickname (the token file's directory name, which is what cc-gmail's `-a` takes)
and fails with `Google refused the refresh of account personal (invalid_grant): run cc-gmail -a personal auth --force`.
The test now matches the whole tail, `: run cc-gmail -a personal auth --force$`. With the placeholder put back, that
test fails.

## 3. An unreadable token.json crashes the probe - ACCEPTED

`load_token` now turns a read or parse error, or JSON that is not an object, into a failed channel naming
`cc-gmail -a <nickname> auth --force`, with the comment that cc-gmail's own write is in place. Tests: a token.json cut
short, empty, and a JSON list each fail that way; through `main`, a cut-short token.json gives exit 1 and a `failed`
line naming the fix, with no network call.

The two-writers item in "What is NOT proven" is corrected: the probe's write is atomic, cc-gmail's is not, so a reader
in between can see a partial file; the probe fails that channel (tested) and the next run reads the whole file.

## 4. Nothing ties the transcript to the committed probe - ACCEPTED

`run_proof.sh` now writes the probe's SHA-256 into the transcript's first lines, and compares `git hash-object` of the
probe with `HEAD:docs/qa/mail-desk/gmail/g0/gmail_probe.py`, printing either that it is byte-for-byte the committed
version or that it differs. The final transcript reads:

```
G0 transcript, 2026-10-21T11:42:02Z, machine devlinux, commit bca238290
gmail_probe.py SHA-256: 224e18267ae10afe0abfe4af98dfd60222668eb0661c2f11d9804305703cea39
gmail_probe.py is byte-for-byte the version committed at bca238290
```

The README says the same, and that the three earlier runs came from uncommitted working copies (as the review saw for
the first transcript, which said `commit bff2295cf`, a commit without the probe).

## The added "What is NOT proven" item - ACCEPTED

"Not run as a factory check under a Director" is in the README, naming the Director's PATH and uv as what this proof
does not cover.
