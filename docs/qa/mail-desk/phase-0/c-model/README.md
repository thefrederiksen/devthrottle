# Mail Desk phase 0, proof (c): the model on a sample

## Verdict: NOT PROVEN against the real model

**The real model was never called.** `devthrottle/wingman-fast` (Qwen/Qwen2.5-72B-Instruct on DeepInfra) was not
asked a single question in this proof, so there are no real model answers, no measured agreement and no measured
confidence values here. Why:

- This machine holds no DevThrottle key (no `dt_live_` or `dt_test_` key in the Director's configuration or in
  cc-secrets) and no DeepInfra key.
- The hosted Gateway, which does hold a key, is off limits in this phase (mission decision D17).
- The keys that do exist on this machine belong to a client (the mindzie Azure key) or to another purpose (the
  admin service token); the Tech Lead ruled them forbidden.
- The Delivery Lead ruled that no key is coming in this phase, and that this proof is finished as not proven.

**Spend: zero dollars.** No paid call was made. The dollar figures in the fake runs are notional (marked
`spent_is_notional: true`): what the fake token counts would cost, there only to exercise the spend stop.

What WAS done: the real mailbox was read (GET only), the 19 real emails were labelled by hand, and the whole
pipeline - prompts, label constraint, confidence, spend stop, scoring - was run end to end against a deterministic
fake model, on the 19 real emails and on an invented set of 18 with expected labels. The real run is one command
once a key is stored (see "How to run it again").

## What was asked

For each email, the two model questions of mission section 5.7:

1. "A real person writing to us, or spam?" - labels `person`, `spam` (`prompts/person_or_spam.txt`).
2. "Which route?" - the `handles` lines of three routes plus `unsure` (`prompts/routes.json`, `prompts/route.txt`):
   `devthrottle/mail-desk` "customers and users of DevThrottle: support, billing, bugs",
   `citizenship` "the citizenship application", `notification` "automated notices from services".

Each call: temperature 0, at most 2 output tokens, the labels shown as letters (A, B, ...) so the answer is a
single token. Any answer that is not exactly one listed letter counts as `unsure`. The confidence is the probability
of the answered token, from the log probabilities the endpoint returns (`logprobs: true`, `top_logprobs: 5`; the proxy
forwards the request body unchanged, but whether DeepInfra returns log probabilities for this model is not proven);
the probability of every label is recorded too. If none come back, the confidence is recorded as empty.

## The real mailbox

- **The Inbox holds 19 emails, not 50.** Microsoft Graph reports `totalItemCount` 19 and no next page; the sample is
  all 19.
- **All 19 are automated notices from services.** By sender kind: 7 `ours` (our own AI cost alerts and weekly
  report), 6 `updates.heycatch.ai`, 3 `heycatch.ai`, 2 `x.com`, 1 `godaddy.com`. None is from a customer, none is
  about the citizenship application, none is spam. My labels (`results/my_labels.json`): `person` and
  `notification` for all 19, because each is a genuine notice from a service the account uses.
- So even a real model run on this Inbox could only measure the `notification` route and false spam calls; it
  could not measure person against spam, or the `devthrottle/mail-desk` and `citizenship` routes. The invented set
  (`synthetic/emails.json`, 18 emails, every label covered) is there for that.
- **The sign-in was opened read-only.** The cache file's modification time, size and hash were identical before and
  after the read. The file is `tokens/email_devthrottle_com_msal.json` (cc-outlook's `get_msal_token_path` turns the
  at sign and dots into underscores), not the name in the brief.

## Finding for the screening check: invisible padding in the preview

In **8 of the 19** real emails the 200-character preview is mostly invisible padding - zero-width spaces, joiners
and similar characters that marketing mail uses to hide the preview line. Those 8 previews hold only **46 to 99
visible characters** out of 200 (`results/preview_padding.json`); in the worst, three quarters of what the model
would be shown is blank. Measured effect: the model is asked about a sender and subject with almost no body text.

Recommendation for mission 5.5 step 3: remove invisible characters (Unicode format characters, and collapse runs of
white space) BEFORE cutting the preview to 200 characters. The invented email `s18` carries this padding so the
fix can be tested.

## The pipeline against the fake model

The fake model picks a letter from a hash of the prompt with made-up log probabilities, and about one answer in six
is a sentence ("I think the answer is A") instead of a letter. Its answers are noise, so its agreement numbers mean
nothing about any real model; the runs prove only that the machinery works.

| Run | Emails | What it shows |
|---|---|---|
| `results/fake_run_synthetic/` | 18 invented | Both questions per email; 5 off-list answers became `unsure`; confusion tables and the threshold table written (`summary.json`, `per_email.json`) |
| `results/fake_run_real19/` | 19 real | The same on the real emails, scored against my labels; 7 off-list answers became `unsure` |
| `results/fake_run_spend_stop/` | 1 of 18 | With a demonstration limit of $0.0003 the run stopped before the call that would have crossed it (`stopped` field) |

Tests (`test_model_sample.py`, 30 passing) cover: the guard refusing POST, PATCH, PUT, DELETE and OPTIONS against
graph.microsoft.com and allowing GET; every Graph call going through the guard; the spend stop; a requested limit
above $0.50 being capped at $0.50; an answer outside the labels becoming `unsure` with no confidence; the sender
kinds; the 12-character short key; the 200-character cut; the raw folder being refused inside the repository; the
synthetic set holding no address and only valid labels.

## Proposed confidence threshold (provisional)

**Propose 0.90 for the route answer, and 0.95 before a `spam` answer may junk an email.** Below it, the email goes
to the Mail Desk agent (mission 5.7 layer 4).

Reasoning, without real measurements to lean on:

- The two mistakes are not equal. Sending an email to the Mail Desk agent when the model was right costs one small
  agent run. Filing or junking an email wrongly can lose a customer's first message unseen - the very thing D11 was
  written to prevent. A threshold should lean towards the agent.
- A large instruction-tuned model at temperature 0 is usually over-confident: most single-token answers come back
  above 0.9 whether right or wrong. A threshold much below 0.9 would therefore filter almost nothing. Spam is held
  to a higher bar because junking is the costlier mistake.
- The value costs nothing to be wrong about: production runs in observe mode, which acts on nothing, and the real
  threshold is set in phase 6 from the observe-mode screening of the existing Inbox, using the threshold table this
  script already produces (for each candidate value: answers kept, kept answers that were wrong, emails sent to the
  agent, and how many of those the model had wrong).

## Would a cheaper model be worth a later mission? (mission question 4)

**Not for cost, at today's volume.** Each call is about 120 to 135 tokens of email and labels plus the system
prompt and chat wrapping - roughly 200 input tokens and 1 output token. Two calls per email: about 400 input tokens,
so about $0.00015 per email at $0.36 per million. The first screening of up to 200 emails costs about 3 cents; even
30 new emails a day is about 13 cents a month. `zai-org/GLM-4.7-Flash` at $0.06 per million would save about 11
cents a month, and would need a deploy of the website proxy. The proxy's own notes also record that GLM-4.7-Flash
"never returned parseable output" and that Llama-3.1-8B "accepted every single candidate" in an earlier measurement
for another feature. A cheaper model becomes worth a mission only if volume grows by a hundred times, or if the real
measurement in phase 6 shows the larger model is no better.

## How to run it again

From this folder, with raw folders OUTSIDE the repository (the script refuses one inside it):

    export PATH=$HOME/.local/bin:$PATH
    RAW=/some/folder/outside/the/repository

    # The real mailbox, GET only; raw text goes to $RAW only
    uv run --script model_sample.py fetch --raw-dir "$RAW"
    # The real model; the key comes from cc-secrets entry devthrottle-api-key (the proxy, preferred)
    # or deepinfra-api-key (DeepInfra directly). Neither exists on devlinux today.
    ./run_classify.sh "$RAW"
    uv run --script model_sample.py show --raw-dir "$RAW"    # on screen only, to label by hand
    uv run --script model_sample.py report                    # results/per_email.json and summary.json
    rm -rf "$RAW"

    # The fake model, no key, no cost
    uv run --script model_sample.py synthetic --raw-dir "$RAW"
    uv run --script model_sample.py classify --raw-dir "$RAW" --endpoint fake --out /tmp/answers.json
    uv run --script model_sample.py report --answers /tmp/answers.json --my-labels "$RAW/expected_labels.json" --out /tmp

    # Tests
    uv run --script test_model_sample.py

## The limits that held

- **Read only.** `graph_guard` refuses any method other than GET against graph.microsoft.com and every Graph call
  goes through `graph_get`, which calls it. Two GET requests were made: the Inbox's 50 newest messages, and the
  Inbox's item count. The sign-in cache was read into memory and never written back.
- **Spend.** A hard stop before each call at $0.50 (a lower limit may be passed, never a higher one), with cost from
  the response's token counts at $0.36 per million input and $0.40 per million output tokens. Actual spend: $0.
- **Nothing private committed.** Committed files hold short keys (first 12 hexadecimal characters of a SHA-256 of
  the Graph message id), sender kinds, labels, confidences, token counts and cost. The invented set stores addresses
  as a local part and a reserved example domain, joined only outside the repository. The raw text of the real
  emails stayed in a scratch folder outside the repository and was deleted at the end.

## What was NOT proven

- **Anything about the real model**: its answers, its agreement with my labels, its confidence values, whether it
  keeps to one letter, whether DeepInfra returns log probabilities for this model, and the real cost.
- Person against spam, and the `devthrottle/mail-desk` and `citizenship` routes, on real email: the Inbox holds none.
- The threshold: 0.90 and 0.95 are reasoned proposals, not measurements.
- 19 real emails is a small sample, all of one kind, and every label is my own judgment. The invented set's
  expected labels are also my judgment.
