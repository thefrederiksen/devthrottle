# /// script
# requires-python = ">=3.10"
# dependencies = ["msal>=1.28"]
# ///
"""Mail Desk phase 0, proof (a): the sign-in premise, as a throwaway definition check.

Run by a Director from a PUBLISHED version of a factory definition, with every CC_ variable stripped from its
environment. It proves, and writes as evidence into its memory file (CC_CHECK_STATE_FILE):

1. which variable NAMES it saw (never a value), and where it looked for the saved cc-outlook sign-in of
   email@devthrottle.com: once from the HOME variable (the control) and once from the operating system's account
   record (pwd), which is the rule the real screen.py would follow;
2. that MSAL renews a token for https://graph.microsoft.com/Mail.ReadWrite SILENTLY from that saved sign-in (there is
   no interactive or device-code sign-in anywhere in this file), and that one read-only GET works with it;
3. the names in the access token's "scp" claim, and whether Mail.Send is among them;
4. that the renewed cache is written back to the same file, owner-only, the way cc-outlook does it.

It prints {"count": 0}, so it never starts a session. Nothing secret is written anywhere: no token, no cache content,
no message, no address other than the mailbox's own.

The only network calls are MSAL's own call to the sign-in service and GET requests to Microsoft Graph. Every Graph
call goes through graph_get(), which refuses any method but GET and any host but graph.microsoft.com.
"""

import base64
import json
import os
import sys
import time
import urllib.request
from pathlib import Path

import msal

MAILBOX = "email@devthrottle.com"
# cc-outlook's own Azure app and authority (tools/cc-outlook/src/auth.py and its saved profile for this mailbox).
CLIENT_ID = "f575e3f4-8f62-4bfb-8f30-f776e55b51bd"
AUTHORITY = "https://login.microsoftonline.com/common"
SCOPE = "https://graph.microsoft.com/Mail.ReadWrite"
GRAPH_HOST = "graph.microsoft.com"
# cc-outlook names the file after the address with "@" and "." made "_" (auth.get_msal_token_path).
CACHE_NAME = MAILBOX.replace("@", "_").replace(".", "_") + "_msal.json"


class RefusedCall(Exception):
    pass


def graph_get(url, token):
    """The one way this check talks to Microsoft Graph: GET, to graph.microsoft.com, nothing else."""
    return graph_call("GET", url, token)


def graph_call(method, url, token):
    host = urllib.request.urlparse(url).hostname
    if host != GRAPH_HOST:
        raise RefusedCall(f"refused: {url} is not on {GRAPH_HOST}")
    if method != "GET":
        raise RefusedCall(f"refused: {method} is not allowed against {GRAPH_HOST}; this check only reads")
    req = urllib.request.Request(url, method="GET", headers={"Authorization": f"Bearer {token}"})
    with urllib.request.urlopen(req, timeout=30) as res:
        return json.loads(res.read().decode("utf-8"))


def director_base(home):
    """The Director's per-user base folder under a given home, by the same platform rules as cc_storage._user_base,
    minus every variable: Windows %LOCALAPPDATA%, macOS Application Support, Linux ~/.local/share."""
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        return Path(local) / "cc-director" if local else Path(home) / "AppData" / "Local" / "cc-director"
    if sys.platform == "darwin":
        return Path(home) / "Library" / "Application Support" / "cc-director"
    return Path(home) / ".local" / "share" / "cc-director"


def account_record_home():
    """The user's real home from the operating system's account record, never from HOME. On Windows (no pwd) the
    base comes from LOCALAPPDATA, which is not a CC_ variable and so reaches a check."""
    if os.name != "posix":
        return os.path.expanduser("~")
    import pwd
    return pwd.getpwuid(os.getuid()).pw_dir


def caches_under(home):
    base = director_base(home)
    return base, sorted(base.glob(f"instances/*/config/outlook/tokens/{CACHE_NAME}"))


def scp_names(access_token):
    """Only the names in the token's scp claim. A token that is not a JSON Web Token is said to be so."""
    parts = access_token.split(".")
    if len(parts) != 3:
        return None
    payload = parts[1] + "=" * (-len(parts[1]) % 4)
    claims = json.loads(base64.urlsafe_b64decode(payload.encode("ascii")).decode("utf-8"))
    return sorted(str(claims.get("scp", "")).split())


def stat_of(path):
    st = path.stat()
    return {"mtimeNs": st.st_mtime_ns, "mtimeUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(st.st_mtime)),
            "mode": oct(st.st_mode & 0o777), "bytes": st.st_size}


def main():
    state_file = Path(os.environ["CC_CHECK_STATE_FILE"])
    previous = state_file.read_text(encoding="utf-8").strip()
    memory = json.loads(previous) if previous else {"runs": []}
    run = {"run": len(memory["runs"]) + 1, "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    memory["runs"].append(run)

    names = sorted(os.environ)
    run["variableNames"] = names
    run["ccVariableNames"] = [n for n in names if n.startswith("CC_")]

    env_home = os.environ.get("HOME", "")
    account_home = account_record_home()
    control_base, control_found = caches_under(env_home) if env_home else (None, [])
    real_base, real_found = caches_under(account_home)
    run["homeVariable"] = env_home
    run["accountRecordHome"] = account_home
    run["controlFromHomeVariable"] = {"searched": str(control_base), "found": [str(p) for p in control_found]}
    run["fromAccountRecord"] = {"searched": str(real_base), "found": [str(p) for p in real_found]}

    def fail(reason):
        run["result"] = "failed: " + reason
        state_file.write_text(json.dumps(memory, indent=1), encoding="utf-8")
        print(reason, file=sys.stderr)
        sys.exit(1)

    if len(real_found) != 1:
        fail(f"{MAILBOX} is {'not signed in' if not real_found else 'signed in more than once'} on this machine "
             f"(looked under {real_base}/instances/*/config/outlook/tokens, found {len(real_found)}): "
             f"run cc-outlook -a devthrottle auth")
    cache_path = real_found[0]
    run["cacheBefore"] = stat_of(cache_path)

    cache = msal.SerializableTokenCache()
    cache.deserialize(cache_path.read_text(encoding="utf-8"))
    app = msal.PublicClientApplication(client_id=CLIENT_ID, authority=AUTHORITY, token_cache=cache)
    accounts = app.get_accounts(username=MAILBOX)
    run["accountsInCache"] = len(accounts)
    if not accounts:
        fail(f"the saved sign-in at {cache_path} holds no account for {MAILBOX}: run cc-outlook -a devthrottle auth")

    # Silent only. force_refresh makes MSAL redeem the refresh token for a token minted for this one scope now.
    result = app.acquire_token_silent([SCOPE], account=accounts[0], force_refresh=True)
    if not result or "access_token" not in result:
        error = (result or {}).get("error", "no token came back")
        fail(f"the saved sign-in for {MAILBOX} could not be renewed silently ({error}): run cc-outlook -a devthrottle auth")
    run["silentToken"] = "yes"
    run["tokenSource"] = result.get("token_source", "")

    scp = scp_names(result["access_token"])
    run["scp"] = scp if scp is not None else "the access token is not a JSON Web Token"
    run["scpHasMailSend"] = bool(scp) and "Mail.Send" in scp

    inbox = graph_get("https://graph.microsoft.com/v1.0/me/mailFolders/inbox?$select=totalItemCount", result["access_token"])
    run["inboxTotalItemCount"] = inbox.get("totalItemCount")

    # The guard refuses what this check must never do; recorded so the transcript shows it is live, not just written.
    refused = []
    for method in ("POST", "PATCH", "DELETE", "PUT"):
        try:
            graph_call(method, "https://graph.microsoft.com/v1.0/me/messages", "unused")
        except RefusedCall as exc:
            refused.append(f"{method}: {exc}")
    run["guardRefused"] = refused

    # Written back exactly as cc-outlook does (MSALTokenBackend._save_cache): only when the state changed, to the same
    # path, then owner-only on POSIX.
    run["cacheStateChanged"] = cache.has_state_changed
    if cache.has_state_changed:
        cache_path.write_text(cache.serialize(), encoding="utf-8")
        if os.name == "posix":
            os.chmod(cache_path, 0o600)
    run["cacheAfter"] = stat_of(cache_path)
    run["result"] = "passed"

    state_file.write_text(json.dumps(memory, indent=1), encoding="utf-8")
    print(json.dumps({"count": 0}))


if __name__ == "__main__":
    main()
