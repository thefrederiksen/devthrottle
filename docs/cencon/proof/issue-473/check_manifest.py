#!/usr/bin/env python3
"""
Consistency check for docs/public/index.json (issue #473).

Rules enforced (from .claude/skills/update-docs/skill.md):
  1. Every "file" entry in the manifest points to a real file on disk.
  2. Every documentation page (markdown file) under docs/public/, except
     index.json itself, has a manifest entry.

Binary assets under any assets/ directory are content embedded BY the pages
(screenshots), not standalone pages, and are intentionally not listed in the
manifest - the existing manifest never listed them. They are excluded from
rule 2 so the check matches how the manifest is actually authored.

Output is ASCII only. Exit code 0 means zero mismatches.
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PUBLIC = os.path.normpath(os.path.join(HERE, "..", "..", "..", "public"))
MANIFEST = os.path.join(PUBLIC, "index.json")


def manifest_files(manifest_path):
    with open(manifest_path, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    files = []
    for category in data.get("categories", []):
        for page in category.get("pages", []):
            files.append(page["file"].replace("/", os.sep))
    return files


def on_disk_pages(public_dir):
    pages = []
    for root, _dirs, names in os.walk(public_dir):
        # assets/ holds embedded screenshots, not standalone doc pages
        if "assets" in os.path.relpath(root, public_dir).split(os.sep):
            continue
        for name in names:
            if not name.lower().endswith(".md"):
                continue
            rel = os.path.relpath(os.path.join(root, name), public_dir)
            pages.append(rel)
    return pages


def main():
    print("DevThrottle docs/public manifest consistency check")
    print("Public docs root: " + PUBLIC)
    print("")

    declared = manifest_files(MANIFEST)
    disk = on_disk_pages(PUBLIC)

    declared_set = set(declared)
    disk_set = set(disk)

    missing_on_disk = sorted(
        f for f in declared if not os.path.isfile(os.path.join(PUBLIC, f))
    )
    unlisted = sorted(disk_set - declared_set)

    print("Manifest entries (declared pages): " + str(len(declared)))
    for f in sorted(declared):
        print("  manifest: " + f)
    print("")
    print("Markdown pages found on disk: " + str(len(disk)))
    for f in sorted(disk):
        print("  on-disk:  " + f)
    print("")

    print("RULE 1 - every manifest file exists on disk:")
    if missing_on_disk:
        for f in missing_on_disk:
            print("  MISMATCH (manifest entry has no file): " + f)
    else:
        print("  OK - all " + str(len(declared)) + " manifest entries resolve to a real file.")
    print("")

    print("RULE 2 - every on-disk page is listed in the manifest:")
    if unlisted:
        for f in unlisted:
            print("  MISMATCH (page on disk not in manifest): " + f)
    else:
        print("  OK - all " + str(len(disk)) + " on-disk pages are listed in the manifest.")
    print("")

    total = len(missing_on_disk) + len(unlisted)
    print("TOTAL MISMATCHES: " + str(total))
    if total == 0:
        print("RESULT: PASS - zero mismatches.")
        return 0
    print("RESULT: FAIL - " + str(total) + " mismatch(es).")
    return 1


if __name__ == "__main__":
    sys.exit(main())
