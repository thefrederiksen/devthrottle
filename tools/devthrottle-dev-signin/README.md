# DevThrottle local development sign-in

The real sign-in page at `https://devthrottle.com/signin` is owned by the backend and is not built
yet, so the Director's first-run login cannot complete against it. This tiny tool stands in for that
page on your own machine: it serves a real sign-in page on loopback and completes the **exact** same
browser hand-back the Director expects, so the whole first-run login flow works end to end with no
backend.

It is deliberately not part of `cc-director.sln`; it is a development utility you run on demand.

## How the flow fits together

1. The Director opens your browser at the configured sign-in address with a `redirect_uri` query
   parameter pointing at its own loopback listener (`http://127.0.0.1:<random>/devthrottle-login-callback/`).
2. This tool serves the sign-in page. You pick a provider (Google, GitHub, or email).
3. It mints a signed test token and redirects the browser to that `redirect_uri`, carrying
   `access_token` and `refresh_token`.
4. The Director's loopback listener captures the pair, stores it, the account gate clears, and the
   first-run consent step appears.

When the real backend page ships, it does the same redirect, so **nothing in the desktop app changes**.

## The one thing that has to match

The token this tool mints is signed with HMAC-SHA256 using a shared secret. The Director verifies the
signature with the secret in `DEVTHROTTLE_JWT_SIGNING_SECRET`. Both sides must use the **same** secret
or the token will be rejected and the gate will stay closed. The helper script below sets both for you.

## Run it (the easy way)

From the repository root:

```powershell
# Builds slot 5, starts this sign-in tool, and launches the Director pointed at it.
scripts\run-dev-signin.ps1 -Build
```

Then in the Director click **Sign in**, pick a provider in the browser, and you land in the app.

## Run it (manually)

```powershell
$env:DEVTHROTTLE_JWT_SIGNING_SECRET = 'devthrottle-local-dev-secret'
dotnet run --project tools\devthrottle-dev-signin

# In another shell, launch a Director with the matching env:
$env:DEVTHROTTLE_JWT_SIGNING_SECRET = 'devthrottle-local-dev-secret'
$env:DEVTHROTTLE_SIGNIN_URL = 'http://127.0.0.1:8765/signin'
local_builds\cc-director5.exe
```

## Environment variables

| Variable | Used by | Meaning |
|----------|---------|---------|
| `DEVTHROTTLE_JWT_SIGNING_SECRET` | this tool + the Director | Shared HMAC-SHA256 signing secret. Must match on both sides. Defaults to `devthrottle-local-dev-secret`. |
| `DEVTHROTTLE_SIGNIN_URL` | the Director | Where the Director sends the browser to sign in. Point it at `http://127.0.0.1:8765/signin`. |
| `DEVTHROTTLE_DEV_SIGNIN_PORT` | this tool | Port to listen on. Defaults to `8765`. |

These are set process-scoped by the helper script, so your other (daily-driver) Directors are not
affected.
