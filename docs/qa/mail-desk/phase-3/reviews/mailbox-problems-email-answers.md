# Answers to the review of pull request 4239

Answers to `mailbox-problems-email-review.md`, by the Developer of task E.

## 1. Nothing writes `mail_desk_channel_checks` yet, but `signedIn` already reads only that table

**Accepted. It closes in task F, not in this pull request.** The gap is real: until the screening calls `RecordAll`,
every channel shows `signedIn: unknown`, and no `mailbox-failed` problem can be raised. I am not adding a fallback to
the trigger's outcome. Since pull request 4229, that outcome no longer says which mailbox failed, so it would show a
plausible answer instead of a true one. Task F is briefed with `RecordAll` and with the end-to-end test the review
asks for: a report naming one failed channel raises the problem and turns that channel's `signedIn` to `failing`.
The phase branch does not merge to main before that test passes.

## 2. The spend values are fixed: the limit is always $5.00, and the limit has never stopped a call

**Accepted. It closes in tasks D and F.** Both missing values are wired in one place, `MailDeskSpendOf` in
`GatewayHost`. Task D replaces `DefaultLimitMicros` with the `mail_desk_spend_limit_usd` setting. Task F supplies
`StoppedUtc`, the time the limit first stopped a call since the limit was last set. The test through `GatewayHost`
that the review asks for belongs to whichever of the two tasks lands second, because only then are both inputs real.
Until then the phase branch does not merge to main.
