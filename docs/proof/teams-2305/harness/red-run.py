"""Show every Mentor test can fail (devthrottle_internal#2305): one deliberate break at a time, the tests run red, the
break undone, the same tests run green. Writes docs/proof/teams-2305/red-run.txt.

Run from the repository root:  python docs/proof/teams-2305/harness/red-run.py

Each break is a plain text replacement in one source file; the original is restored in a `finally`, so a failed run
cannot leave a broken file behind, and the script refuses to start on a file that does not hold the text it breaks.
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
OUT = ROOT / "docs" / "proof" / "teams-2305" / "red-run.txt"
VITEST = ROOT / "node_modules" / "vitest" / "vitest.mjs"

COCKPIT = ROOT / "apps" / "cockpit"
CORE = ROOT / "packages" / "client-core"
PAGE = COCKPIT / "src" / "mentor" / "MentorView.tsx"
ENTRY = COCKPIT / "src" / "mentor" / "useMentorEntry.ts"
CLIENT = CORE / "src" / "teams" / "mentorClient.ts"

BREAKS = [
    ("The page trims the quoted prompt", PAGE, "{quote.text}", "{quote.text.trim()}",
     COCKPIT, "src/mentor/MentorView.test.tsx"),
    ("The page sorts the blocks by email", PAGE, "{page.blocks.map((block) => (",
     "{[...page.blocks].sort((a, b) => (a.personEmail ?? \"\").localeCompare(b.personEmail ?? \"\")).map((block) => (",
     COCKPIT, "src/mentor/MentorView.test.tsx"),
    ("The page calls every reader 'your' role", PAGE,
     'return reader.role === "Owner" ? "the team\'s Owner" : `your ${reader.role}`;', "return `your ${reader.role}`;",
     COCKPIT, "src/mentor/MentorView.test.tsx"),
    ("The rail hides the entry when the read fails", ENTRY,
     "setOffered({ teamId, offered: true });", "setOffered({ teamId, offered: false });",
     COCKPIT, "src/AppShell.test.tsx"),
    ("The rail decides the entry from the role label", ENTRY,
     "setOffered({ teamId, offered: answer.kind === \"page\" });",
     "setOffered({ teamId, offered: answer.kind === \"page\" && current?.role !== \"Developer\" });",
     COCKPIT, "src/AppShell.test.tsx"),
    ("The client trims the quoted prompt", CLIENT, "text: q.text }", "text: q.text.trim() }",
     CORE, "src/teams/mentorClient.test.ts"),
    ("The client accepts an empty readers list", CLIENT, "if (p.readers.length === 0) {", "if (p.readers.length < 0) {",
     CORE, "src/teams/mentorClient.test.ts"),
    ("The client rejects a null person email", CLIENT, "!isTextOrNull(b.personEmail) ||", "!isText(b.personEmail) ||",
     CORE, "src/teams/mentorClient.test.ts"),
]

ANSI = re.compile(r"\x1b\[[0-9;]*m")


def run(cwd: Path, test: str) -> list[str]:
    result = subprocess.run(["node", str(VITEST), "run", test], cwd=cwd, capture_output=True, text=True, encoding="utf-8")
    lines = ANSI.sub("", result.stdout + result.stderr).splitlines()
    keep = [l.replace("\u00d7", "[FAIL]").strip() for l in lines if "\u00d7" in l or l.strip().startswith("Tests ")]
    return keep


def main() -> int:
    report = []
    for name, path, original, broken, cwd, test in BREAKS:
        # Bytes, so the restore puts back the file exactly - line endings included.
        source = path.read_bytes().decode("utf-8")
        if source.count(original) != 1:
            raise RuntimeError(f"{path.name} does not hold exactly one '{original}'; the break '{name}' is stale.")
        try:
            path.write_bytes(source.replace(original, broken).encode("utf-8"))
            red = run(cwd, test)
        finally:
            path.write_bytes(source.encode("utf-8"))
        green = run(cwd, test)
        report.append(f"=== BREAK: {name}\n    {path.relative_to(ROOT).as_posix()}: '{original}' -> '{broken}'")
        report.append("--- with the break:")
        report.extend(f"    {l}" for l in red)
        report.append("--- after restoring:")
        report.extend(f"    {l}" for l in green)
        report.append("")
        print(f"[DONE] {name}")
    OUT.write_text("\n".join(report) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
