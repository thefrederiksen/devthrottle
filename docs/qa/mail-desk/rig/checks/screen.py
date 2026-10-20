# /// script
# requires-python = ">=3.10"
# dependencies = ["msal>=1.28"]
# ///
"""THE RIG'S WRAPPER around the Mail Desk's real screening check. Used ONLY by docs/qa/mail-desk/rig/run_qa.py.

The rig publishes a `mail-desk` definition on its throwaway Gateway whose `checks/screen.py` is THIS file and whose
`checks/real_screen.py` is the template's real `src/CcDirector.Gateway/MailDesk/Template/checks/screen.py`, byte for
byte. This wrapper loads that real check and replaces exactly two things, then runs the real `main()`:

  (a) the network: `urllib.request.urlopen`, the one function the phase 2 tests replace too. The real check's own
      `graph_request()`, its request guard and its `transport()` (with its HTTP-error, unreachable and not-JSON
      handling) all still run; only the last step - opening the connection - goes to the rig's fake Graph on
      127.0.0.1 instead of https://graph.microsoft.com. Any other address is refused before a connection is made.
  (b) the sign-in: `OutlookReader.sign_in`. Instead of reading cc-outlook's saved sign-in under the user's real home
      and asking Microsoft for a token, it looks for the same file name in the RIG's scratch sign-in folder; found, the
      mailbox is signed in with a fake token; not found, the real check's own "not signed in" failure is raised, its
      fix read from the rig folder's cc-outlook account list. MSAL is never constructed.

Everything else - reading and writing the memory, the cursor, the keys, the Sent cache, the batches, the filing queue,
the output, every failure sentence - is the real code.

It refuses to run (exit 1, one sentence, nothing read) unless:
  - files/rig.json names a rig folder holding the rig's marker file, and this wrapper's own copy is inside it (the
    Director the rig starts keeps its downloaded checks under the rig folder);
  - the sign-in folder is inside the rig folder and is not, and does not hold, the user's real Director folder;
  - the fake Graph is an http address on 127.0.0.1.
So it can never find the owner's real sign-in or reach Microsoft.
"""

import importlib.util
import json
import os
import pwd
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
MARKER = "mail-desk-rig.marker"
GRAPH_HOST = "graph.microsoft.com"


def refuse(sentence):
    print(f"the Mail Desk rig's wrapper refuses to run: {sentence}", file=sys.stderr)
    sys.exit(1)


def inside(child, parent):
    child, parent = Path(child).resolve(), Path(parent).resolve()
    return child == parent or parent in child.parents


def settings():
    """files/rig.json beside checks/, written by the rig: {"rigFolder", "signInFolder", "fakeGraph"}."""
    path = HERE.parent / "files" / "rig.json"
    try:
        found = json.loads(path.read_text(encoding="utf-8"))
        rig, sign_in, fake = Path(found["rigFolder"]), Path(found["signInFolder"]), found["fakeGraph"]
    except (OSError, ValueError, KeyError, TypeError) as e:
        refuse(f"{path} cannot be read as the rig's settings ({type(e).__name__})")
    if not (rig / MARKER).is_file():
        refuse(f"the rig folder {rig} holds no {MARKER}")
    if not inside(__file__, rig):
        refuse(f"this copy of the wrapper ({Path(__file__).resolve()}) is not inside the rig folder {rig}")
    if not inside(sign_in, rig):
        refuse(f"the sign-in folder {sign_in} is not inside the rig folder {rig}")
    real_base = Path(pwd.getpwuid(os.getuid()).pw_dir) / ".local" / "share" / "cc-director"
    if inside(sign_in, real_base) or inside(real_base, sign_in):
        refuse(f"the sign-in folder {sign_in} is or holds the user's real Director folder {real_base}")
    parts = urllib.parse.urlsplit(fake)
    if parts.scheme != "http" or parts.hostname != "127.0.0.1" or parts.path not in ("", "/"):
        refuse(f"the fake Graph {fake} is not an http address on 127.0.0.1")
    return rig, sign_in, f"http://127.0.0.1:{parts.port}"


def load_real_check():
    spec = importlib.util.spec_from_file_location("screen", HERE / "real_screen.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    _, sign_in_folder, fake = settings()
    screen = load_real_check()
    real_urlopen = urllib.request.urlopen

    def urlopen(request, timeout=None):
        """(a) The same request to the fake Graph: https://graph.microsoft.com/<path>?<query> becomes
        http://127.0.0.1:<port>/<path>?<query>, every header and the body unchanged."""
        parts = urllib.parse.urlsplit(request.full_url)
        if parts.scheme != "https" or parts.netloc != GRAPH_HOST:
            raise urllib.error.URLError(f"the rig's wrapper sends nothing to {parts.scheme}://{parts.netloc}")
        rewritten = urllib.request.Request(fake + urllib.parse.urlunsplit(("", "", parts.path, parts.query, "")),
                                           data=request.data, method=request.get_method(),
                                           headers=dict(request.header_items()))
        return real_urlopen(rewritten, timeout=timeout)

    def sign_in(self):
        """(b) cc-outlook's file name for the address, looked for in the rig's sign-in folder only."""
        name = screen.outlook_cache_name(self.address)
        found = sorted(p for p in sign_in_folder.glob("instances/*/config/outlook/tokens/*_msal.json")
                       if p.name.lower() == name)
        if not found:
            raise self.not_signed_in(sign_in_folder, f"looked for {sign_in_folder}/instances/*/config/outlook/tokens/"
                                                     f"{name} and found none")
        self.token = f"rig-token-for-{self.address}"

    def no_real_home(*_):
        raise screen.CheckFailed("the rig's wrapper never reads the user's real Director folder")

    class NoMsal:
        def __getattr__(self, name):
            raise screen.CheckFailed("the rig's wrapper never uses MSAL")

    screen.urllib.request.urlopen = urlopen
    screen.OutlookReader.sign_in = sign_in
    screen.account_record_home = no_real_home
    screen.director_base = no_real_home
    screen.msal = NoMsal()
    return screen.main()


if __name__ == "__main__":
    sys.exit(main())
