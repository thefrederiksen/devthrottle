# Keeping a spare sign-in usable - what was measured

Measured on 11 October 2026 on the Windows machine, Claude Code 2.1.296, in an ISOLATED config folder
signed in to a test account ("account C"), never the machine's real Claude login. Tokens were compared by
the first 8 hex characters of their SHA-256, never shown. The code this decided is
`tools/cc-devthrottle/src/license_usage.py`.

## The question

A spare's access token expires in hours. A switch at 3 AM needs a spare that still works, with nobody
there to sign in. How does a spare stay usable, and does anything that keeps it usable break trap 2
("a key is never copied")?

## What Claude Code does

Read from the installed binary, then checked against the test folder:

- **Claude Code renews a key only on its way to an API call.** Its refresh runs when the stored access
  token is about to expire, under a lock, posting to `https://platform.claude.com/v1/oauth/token` a JSON
  body `{grant_type: "refresh_token", refresh_token, client_id, scope}` (`client_id` is Claude Code's
  public one, `9d1c250a-e61b-44d9-88ed-5944d1962f5e`; `scope` is the stored scopes, space separated).
  It writes the answer back to the same store it read from.
- **`claude auth status` never renews.** With the access token marked expired, `claude auth status`
  (JSON and `--text`) ran and the stored key was unchanged before and after. It also still said
  `"loggedIn": true` - it reads the stored record, not the server.
- So there is no cheap Claude Code command that renews an idle folder; the only commands that do are
  ones that call the model, which spends the account's usage and needs a prompt.

## What the token endpoint does

All measured on account C:

| Step | Result |
|---|---|
| Access token lifetime | `expires_in` 28800 seconds (8 hours), after sign-in and after every refresh |
| Refresh token lifetime | `refresh_token_expires_in` 2471424 seconds, ending on the SAME date the sign-in's refresh token already had (about 30 days after the sign-in). **A refresh does not extend it.** |
| Fields in the answer | `access_token`, `refresh_token`, `expires_in`, `refresh_token_expires_in`, `scope`, `token_type`, `token_uuid`, `account`, `organization` |
| Does a refresh rotate the refresh token? | **Yes, every time** (four refreshes, four new refresh tokens) |
| The old ACCESS token after a refresh | **Refused at once**: the usage endpoint answered 401 for it |
| The old (spent) REFRESH token, posted again | HTTP 400 `invalid_grant` |
| The current key after that spent token was tried | Still worked: a usage read and a further refresh both succeeded |
| The renewed folder | Read by the usage endpoint with the new token; `claude auth status` still reports it signed in |

## What that decides

1. **A spare is renewed by DevThrottle itself, in the spare's own folder**, with exactly the request
   Claude Code sends, and the answer is written back to the same place (`license refresh`, or
   `license usage --refresh`, which renews a due spare and then reads it). No second copy exists at any
   point: the key is read, renewed and rewritten where it lives.
2. **Trap 2 is real and immediate.** A refresh in one copy of a key rotates the refresh token AND
   kills the other copy's access token on the spot. A copied key does not merely risk a lost refresh
   later; it breaks the first time either copy renews.
3. **Never renew the ACTIVE key.** Running sessions renew it themselves. A renewal from outside would
   kill their access token mid-turn and spend the refresh token they hold. `license refresh` refuses the
   active account.
4. **A renewal holds the key store's lock from start to finish** - reading the refresh token, asking the
   provider, writing the renewed key. The provider spends the old refresh token the moment it answers, so
   a switch, or a second renewal, that touched the key in between would lose the renewed one. While the
   lock is held, any other store operation is refused as `switch-in-progress`; a renewal that finds the
   lock taken sends nothing and tries again on the next round.
5. **A spare renewed every so often still dies about 30 days after its sign-in.** Renewing keeps the
   8-hour access token alive, not the sign-in. `signInExpiresAtUtc` is reported on every usage row so the
   Gateway and the page can warn before a spare needs a new sign-in. This is a new fact for the mission:
   phase 4 (adding a spare) has to be repeated per spare roughly monthly, or the switch runs out of
   spares.
6. **When a renewal is due:** under one hour left on the access token. The Director reads spares every
   10 minutes, so a spare gets several chances before it actually expires.
7. **A crash between the server's answer and the write loses the spare** (the old refresh token is
   already spent). The write is a same-folder atomic replace, so the window is tiny, and a failed write
   says plainly that the spare needs a new sign-in.

## How to repeat it

In an isolated, signed-in config folder only:

```
CLAUDE_CONFIG_DIR=<folder> claude auth status        # compare token fingerprints before and after
cc-devthrottle license refresh --force --default-config-dir <isolated default> --store-dir <store>
cc-devthrottle license usage --refresh --json --default-config-dir <isolated default> --store-dir <store>
```
