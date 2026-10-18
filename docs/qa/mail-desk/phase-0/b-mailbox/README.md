# Mail Desk phase 0, proof (b): read-only measurements of the real mailbox

Measured on 18 October 2026 at 14:38 UTC against email@devthrottle.com, with cc-outlook's saved sign-in.
Script: `measure_mailbox.py`. Full output: `results/measurements.json` (numbers only - no message ids, no
addresses other than ours, no subjects, no previews).

## The short answer

- **Reading changed nothing.** Inbox total 19 and unread 4 before the run, 19 and 4 after. The saved sign-in
  file was byte-for-byte the same before and after (same SHA-256), so it was opened read-only as required.
- **The Inbox is small: 19 messages, the oldest 6.6 days old.** Question 5's rule ("most recent 200 or the last
  30 days, whichever is fewer") yields **19 messages**, all in one 50-message page, in 0.04 seconds.
- **The Sent folder: 44 messages, back 123 days, 38 distinct recipient addresses**, one 100-message page, 0.5 seconds.
- **A Graph message id is 152 characters** (all 19 the same length).
- **One message, as the check would report it, is 446 bytes typical and 680 bytes at worst** (200-character preview,
  UTF-8 JSON).
- **Recommended batch size for phase 2: fill by bytes up to 7 KB of context, and never more than 10 messages.**
  Reasons below.

## 1. Inbox counts before and after

| | totalItemCount | unreadItemCount |
|---|---|---|
| Before | 19 | 4 |
| After | 19 | 4 |

No new mail arrived during the run (it took under one second, 17 Graph GET requests, no throttling).

## 2. Inbox paging (mission question 5)

- `$select=id,receivedDateTime,from,subject,hasAttachments,bodyPreview`, `$orderby=receivedDateTime desc`, page size 50.
- 1 page, 19 messages, 0.037 seconds. The walk stopped at the end of the folder: neither the 200 limit nor the
  30-day limit was reached.
- **`@odata.nextLink` paging works with `$orderby`.** Because the Inbox fits one page, a second walk used pages of 5:
  4 pages, 19 messages, no message repeated across pages, newest-first order kept on every page. The same on the Sent
  folder in pages of 10: 5 pages, 44 messages, no repeats, order kept.
- **`$filter=receivedDateTime ge <30 days ago>` together with `$orderby=receivedDateTime desc` (or `asc`) is
  accepted** - the check can ask Graph for "since the cursor, oldest first" directly.
- `$top=1000` and `$top=1001` were both accepted and returned all 19. With only 19 messages this does NOT show
  where Graph caps a page; it shows only that a large `$top` is not refused.

## 3. Sent folder (the "addresses we have written to" cache, mission 5.5)

| Messages | Oldest | Distinct recipient addresses (to, cc, bcc) | Pages (size 100) | Time |
|---|---|---|---|---|
| 44 | 122.8 days back | 38 | 1 | 0.48 seconds |

A cache of 38 addresses is tiny; refreshing it once a day costs one request today.
**None of the 19 Inbox senders is in it** (`knownSender` would be false for all of them).

## 4. Message size

Message id length: min 152, median 152, 90th percentile 152, max 152.

One record, as the check would report it (`key` = first 12 hexadecimal characters of SHA-256 of the id, received
time, sender address and name, subject, hasAttachments, preview, knownSender), compact JSON, bytes:

| Preview | Encoding | min | median | 90th percentile | max |
|---|---|---|---|---|---|
| 200 characters | UTF-8 (`ensure_ascii=False`) | 425 | 446 | 665 | 680 |
| 200 characters | non-ASCII escaped as `\uXXXX` | 425 | 446 | 1028 | 1049 |
| 100 characters | UTF-8 | 303 | 346 | 395 | 408 |
| 100 characters | non-ASCII escaped | 303 | 346 | 503 | 519 |

The same record once more, escaped as JSON text inside the output's `context` string (quotes become `\"`):
200-character preview, UTF-8: median 488, max 710. Escaped as `\uXXXX`: median 488, max 1202.

## 5. The budget

Context at most 8,192 bytes; whole output `{"count": n, "context": "<JSON text>"}` at most 16,384 bytes.
In every case measured the **8 KB context cap is the one that binds** - the escaped output stays well under 16 KB.

How many messages fit one context:

| Preview | Encoding | All median-size | All worst-size | Real batches over the 19, oldest first |
|---|---|---|---|---|
| 200 | UTF-8 | 18 | 12 | 15, then 4 |
| 200 | `\uXXXX` | 18 | 7 | 12, then 7 |
| 100 | UTF-8 | 23 | 19 | all 19 in one |
| 100 | `\uXXXX` | 23 | 15 | all 19 in one |

The mission's estimate of "roughly 20 messages per run" holds for typical mail, not for the worst case.

### Recommendation for phase 2

1. **Write the context as UTF-8 (`ensure_ascii=False`) and measure it in bytes.** Escaping non-ASCII as `\uXXXX`
   inflated the largest real record by more than half (680 to 1049 bytes) and cut the worst case from 12 to 7.
2. **Keep the 200-character preview** (mission 5.5 step 3). Cutting to 100 buys only 5 more messages typical
   (18 to 23); the classifier gets half the text for it.
3. **Fill each batch by bytes, stopping before the context would pass 7,168 bytes (7 KB), and never more than 10
   messages.** Ten worst-size records from this sample come to 6,824 bytes of context, so the cap of 10 leaves
   about 1,370 bytes (17 percent) under the 8 KB limit even when every message is as large as the largest we saw. The byte
   check is the real guard, because a sample of 19 cannot show the true worst case (a long subject or a long display
   name); the count cap keeps the batch predictable for the Gateway. Room is also left in the context for the filing
   results that live mode adds (5.5 step 5), which this proof did not measure.

## How to run it again

```
export PATH=$HOME/.local/bin:$PATH
cd docs/qa/mail-desk/phase-0/b-mailbox
uv run --script measure_mailbox.py --self-test                     # the guard only, no network
uv run --script measure_mailbox.py --out results/measurements.json  # the measurements
```

It needs cc-outlook's saved sign-in for email@devthrottle.com (`cc-outlook -a devthrottle auth`). It reads the
client id from cc-outlook's `profiles.json` and the cache from
`~/.local/share/cc-director/instances/default/config/outlook/tokens/email_devthrottle_com_msal.json`. Note the file
name: cc-outlook replaces both `@` and `.` with `_`, so the name in the phase brief
(`email@devthrottle.com_msal.json`) is not the real one.

## How it stays read-only

- `GetOnlyGraphSession` refuses any method other than GET, and any host other than graph.microsoft.com, before
  sending. `--self-test` proves POST, PATCH, PUT, DELETE, HEAD, OPTIONS and a GET to another host are all refused; it
  runs before every measurement run.
- The token cache is loaded into memory and never serialized back. Only a silent sign-in is attempted; no
  interactive or device-code sign-in exists in the script. Only the `Mail.ReadWrite` scope is asked for, as in
  mission 5.4. The read-only promise comes from the guard, not from the scope.
- Raw messages were held only in memory for the length of the run. Nothing raw was written to disk, so there was
  nothing to delete.

## What was NOT measured or proven

- **Paging at scale.** The Inbox holds 19 messages, so the 200-message limit, the 30-day limit and Graph's page cap
  for `$top` were never reached. The cost of the first-run look-back on a large mailbox is not known from this.
- **A true worst case for record size.** Nineteen messages are a small sample; the 90th percentile and max are
  sizes from 19 messages, not from a distribution of real traffic.
- **`knownSender` matching.** None of the 19 Inbox senders appears among the 38 Sent recipients, so the match was
  never exercised on a real hit.
- **Token renewal.** The sign-in was minutes old, so the cached access token was almost certainly still valid and no
  renewal was needed; renewal, and writing a renewed cache back (mission 5.4), were not exercised. (Proof (a) owns writing the cache.)
- **Throttling.** 17 requests were never throttled; behaviour under Graph throttling is not known.
- **The filing-results part of the context** (live mode) and the short-key table in the check's memory were not
  measured.
