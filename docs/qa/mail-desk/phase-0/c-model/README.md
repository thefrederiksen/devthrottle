# Mail Desk phase 0, proof (c): the model on a sample

Status: **NOT PROVEN YET - the model calls have not run.** The mailbox read and the hand labels are done.
The model calls are waiting for a key: this machine holds neither a DevThrottle key nor a DeepInfra key
(see "Blocked" below). This file will be completed with the results when they run.

## What this proof asks

For each recent Inbox email of email@devthrottle.com, the two model questions of mission section 5.7, asked of
`devthrottle/wingman-fast` (Qwen/Qwen2.5-72B-Instruct on DeepInfra):

1. "A real person writing to us, or spam?" - labels `person`, `spam`.
2. "Which route?" - the `handles` lines of three routes plus `unsure` (`prompts/routes.json`):
   `devthrottle/mail-desk` "customers and users of DevThrottle: support, billing, bugs",
   `citizenship` "the citizenship application", `notification` "automated notices from services".

Each call: temperature 0, at most 2 output tokens, the labels shown as letters (A, B, ...) so the answer is one
token; any answer that is not exactly one listed letter counts as `unsure`. The confidence is the probability of
the answered token, from the token log probabilities the endpoint returns (`logprobs`, `top_logprobs: 5`).

## What was found so far

- **The Inbox holds 19 emails, not 50.** Microsoft Graph reports `totalItemCount` 19 for the Inbox and no next
  page. The sample is therefore all 19.
- **All 19 are automated notices from services** - by sender kind: 7 from `ours` (our own cost alerts and weekly
  report), 6 from `updates.heycatch.ai`, 3 from `heycatch.ai`, 2 from `x.com`, 1 from `godaddy.com`. There is no email from a customer, none about the citizenship
  application, and none I would call spam. My hand labels (`results/my_labels.json`) are `person` and
  `notification` for all 19.
- **Design finding for the screening check (mission 5.5, step 3).** In 8 of the 19 emails, the 200-character
  preview is mostly invisible padding (zero-width and joiner characters that marketing mail uses to hide the preview
  text): those 8 previews hold only 46 to 99 visible characters (`results/preview_padding.json`). The check should
  remove invisible characters before cutting the preview to 200 characters, or the model is asked about blank text.
- **The sign-in file name in the brief was wrong.** cc-outlook saves it as
  `tokens/email_devthrottle_com_msal.json` (the at sign and dots become underscores, `get_msal_token_path` in
  `tools/cc-outlook/src/auth.py`), not `tokens/email@devthrottle.com_msal.json`. The script uses cc-outlook's rule.
- **The sign-in was opened read-only.** The cache file's modification time, size and hash were the same before and
  after the read.

## Blocked

This machine holds no DevThrottle key (no `dt_live_` or `dt_test_` key in the Director's configuration or in
cc-secrets) and no DeepInfra key. The Tech Lead ruled that no other key may be used (not the mindzie Azure key, not
the admin service token) and asked for one of these two cc-secrets entries to be stored:

- `devthrottle-api-key` - used first: the website's inference proxy, model `devthrottle/wingman-fast`, tagged with
  the feature header `mail-desk`;
- `deepinfra-api-key` - otherwise: DeepInfra directly, model `Qwen/Qwen2.5-72B-Instruct`.

## How to run it again

From this folder, with a raw folder OUTSIDE the repository (the script refuses one inside it):

    export PATH=$HOME/.local/bin:$PATH
    RAW=/some/folder/outside/the/repository
    uv run --script model_sample.py fetch --raw-dir "$RAW"     # GET only; writes raw text to $RAW only
    ./run_classify.sh "$RAW"                                    # the model calls; key from cc-secrets
    uv run --script model_sample.py show --raw-dir "$RAW"      # on screen, for labelling by hand
    uv run --script model_sample.py report                      # writes results/per_email.json and summary.json
    rm -rf "$RAW"

Tests: `uv run --script test_model_sample.py` (the read-only guard, the spend stop, the answer parsing, the sender
kinds, the raw-folder refusal).

## The limits that hold

- **Read only.** `graph_guard` refuses any method other than GET against graph.microsoft.com, and every Graph call
  goes through `graph_get`, which calls it. The sign-in cache is read into memory and never written back.
- **Spend.** Before each call the script adds a generous estimate of that call to what has been spent and stops if
  it would pass $0.50. Cost is computed from the token counts the response returns at $0.36 per million input and
  $0.40 per million output tokens.
- **Nothing private committed.** Committed files hold short keys (the first 12 hexadecimal characters of a SHA-256
  of the Graph message id), sender kinds, labels, confidences, tokens and cost. Raw text stayed in a scratch folder
  outside the repository and was deleted at the end.

## What was NOT proven

- The model's answers, its agreement with my labels, the confidence threshold, and the cost: the model calls have
  not run.
- With this mailbox, even once they run: person against spam, and the `devthrottle/mail-desk` and `citizenship`
  routes, cannot be measured - the Inbox holds no such email. 19 emails is a small sample, and the labels are my
  judgment.
