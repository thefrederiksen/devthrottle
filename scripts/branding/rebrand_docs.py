"""
Rebrand the public-facing install docs from CC Director -> DevThrottle, and fix
stale repo URLs after the rename to thefrederiksen/devthrottle.

Ordered + protected so stable identifiers are NOT changed:
  - KEEP installed mac bundle "CC Director.app" (its own identity, like cc-director.exe)
  - KEEP main-app assets cc-director-win-x64.exe / cc-director-mac-arm64.zip
  - KEEP install paths %LOCALAPPDATA%\\cc-director, ~/Applications, cc-director.exe
  - CHANGE brand "CC Director" -> "DevThrottle"
  - CHANGE setup assets cc-director-setup-* -> devthrottle-setup-*
  - CHANGE repo URLs (incl broken cc-director/cc-director org) -> thefrederiksen/devthrottle
ASCII-only output.
"""
import os

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FILES = [
    "README.md",
    "docs/public/getting-started/01-introduction.md",
    "docs/public/getting-started/02-installation.md",
    "docs/public/getting-started/03-quick-start.md",
    "docs/install/install-prompt.md",
]

SENTINEL = "KEEP_APP"

# Ordered (search, replace) pairs.
RULES = [
    # 1. Protect the installed mac Director bundle name.
    ("CC Director.app", SENTINEL),
    # 2. Brand: "CC Director Setup" -> "DevThrottle Setup", "CC Director" -> "DevThrottle".
    ("CC Director", "DevThrottle"),
    # 3. Restore the protected bundle name.
    (SENTINEL, "CC Director.app"),
    # 4. Fix the broken wrong-org clone URL first (it contains the substring below).
    ("github.com/cc-director/cc-director", "github.com/thefrederiksen/devthrottle"),
    # 5. Repo refs (covers github.com/.../releases, api.github.com/repos/..., gh --repo, bare refs).
    ("thefrederiksen/cc-director", "thefrederiksen/devthrottle"),
    # 6. Setup asset names (the user-facing downloads). Main-app assets are untouched.
    ("cc-director-setup-win-x64.exe", "devthrottle-setup-win-x64.exe"),
    ("cc-director-setup-cli-win-x64.exe", "devthrottle-setup-cli-win-x64.exe"),
    ("cc-director-setup-mac-arm64.zip", "devthrottle-setup-mac-arm64.zip"),
]

for rel in FILES:
    path = os.path.join(REPO, rel)
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    orig = text
    for old, new in RULES:
        text = text.replace(old, new)
    if text != orig:
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    # Report what remains that might be stale.
    remaining_brand = text.count("CC Director")
    remaining_url = text.count("thefrederiksen/cc-director") + text.count("cc-director/cc-director")
    print(f"{rel}: changed={text != orig}  remaining 'CC Director'={remaining_brand}  stale-url={remaining_url}")
print("DONE")
