"""
Rebrand the Cockpit web UI brand text "CC Director" -> "DevThrottle".

Walks the Cockpit project's .razor/.html/.css/.js files. Replacing the exact brand
string "CC Director" deliberately preserves:
  - standalone "Director"/"Directors" (the Director app/component, page names, stats)
  - "cc-director" (lowercase: the repo name + slot exe names in Exes.razor)
  - live session names like "cc-director" (those are runtime data, not in code)
ASCII-only output.
"""
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..",
                                    "src", "CcDirector.Cockpit"))
EXTS = (".razor", ".html", ".css", ".js")
total = 0
for dirpath, dirs, files in os.walk(ROOT):
    dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
    for name in files:
        if not name.endswith(EXTS):
            continue
        path = os.path.join(dirpath, name)
        s = open(path, encoding="utf-8").read()
        n = s.count("CC Director")
        if n:
            open(path, "w", encoding="utf-8", newline="").write(s.replace("CC Director", "DevThrottle"))
            total += n
            print(f"{n:3d}  {os.path.relpath(path, ROOT)}")
print(f"TOTAL 'CC Director' -> 'DevThrottle': {total}")
