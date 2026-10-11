"""Migration script: sync newest data from old locations to cc-director unified storage.

MUST be run AFTER backup-before-migration.py has created a backup.
Uses "newest wins" strategy: copies files that are newer or where the destination
is empty/stale.

Does NOT delete old directories -- user does that manually after verification.

Usage:
    python scripts/migrate-storage.py              # Dry-run
    python scripts/migrate-storage.py --run        # Actually sync data
"""

import argparse
import glob
import os
import shutil
import sys
from pathlib import Path


# ---------------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------------

def _local() -> str:
    return os.environ.get("LOCALAPPDATA", "")


def _home() -> Path:
    return Path.home()


def _docs() -> str:
    return os.path.join(os.environ.get("USERPROFILE", str(_home())), "Documents")


def _new_base() -> str:
    local = _local()
    if local:
        return os.path.join(local, "cc-director")
    return str(_home() / ".cc-director")


# ---------------------------------------------------------------------------
# Backup verification
# ---------------------------------------------------------------------------

def find_backup() -> str:
    """Find the most recent backup zip file. Returns path or empty string."""
    backup_base = os.environ.get("CC_BACKUP_DIR", str(Path.home() / "Backups"))
    pattern = os.path.join(backup_base, "cc-director-migration-*.zip")
    matches = sorted(glob.glob(pattern))
    return matches[-1] if matches else ""


# ---------------------------------------------------------------------------
# "Newest wins" copy logic
# ---------------------------------------------------------------------------

def should_copy(src: str, dst: str) -> str:
    """Return reason string if src should overwrite dst, empty string if not."""
    if not os.path.exists(dst):
        return "missing"

    src_size = os.path.getsize(src)
    dst_size = os.path.getsize(dst)

    # Destination suspiciously small and source has data
    if dst_size < 10 and src_size >= 10:
        return f"dest empty ({dst_size}b), src has {src_size}b"

    # Source is newer
    if os.path.getmtime(src) > os.path.getmtime(dst):
        return "newer"

    return ""


def sync_file(src: str, dst: str, label: str, results: dict) -> None:
    """Copy a single file if newer, logging results."""
    if not os.path.isfile(src):
        return

    reason = should_copy(src, dst)
    if not reason:
        results["skipped"].append(f"{label}: {src} (up to date)")
        return

    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)

    # Validate
    if os.path.exists(dst) and os.path.getsize(dst) == os.path.getsize(src):
        results["synced"].append(f"{label}: {src} -> {dst} ({reason})")
    else:
        results["failed"].append(f"{label}: {src} -> {dst} (copy verification failed)")


def sync_directory(src: str, dst: str, label: str, results: dict) -> None:
    """Copy a directory recursively using newest-wins per file."""
    if not os.path.isdir(src):
        return

    file_count = 0
    for root, dirs, files in os.walk(src):
        rel = os.path.relpath(root, src)
        dest_root = os.path.join(dst, rel) if rel != "." else dst
        os.makedirs(dest_root, exist_ok=True)

        for f in files:
            src_file = os.path.join(root, f)
            dst_file = os.path.join(dest_root, f)
            reason = should_copy(src_file, dst_file)
            if reason:
                shutil.copy2(src_file, dst_file)
                file_count += 1

    if file_count > 0:
        results["synced"].append(f"{label}: {src} -> {dst} ({file_count} files)")
    else:
        results["skipped"].append(f"{label}: {src} (all files up to date)")


# ---------------------------------------------------------------------------
# Migration rules
# ---------------------------------------------------------------------------

def run_migration(dry_run: bool = True) -> None:
    """Run the full migration."""
    # Step 1: Verify backup exists
    backup_dir = find_backup()
    if not backup_dir:
        backup_base = os.environ.get("CC_BACKUP_DIR", str(Path.home() / "Backups"))
        print(f"[ERROR] No backup found at {backup_base}/cc-director-migration-*")
        print("        Run backup-before-migration.py first!")
        sys.exit(1)

    print(f"[OK] Backup found: {backup_dir}")
    print()

    local = _local()
    home = _home()
    docs = _docs()
    base = _new_base()

    # Build migration rules: (label, source, destination, is_dir)
    rules = []

    # --- Vault data ---
    old_vault = os.path.join(local, "cc-myvault") if local else ""
    new_vault = os.path.join(base, "vault")
    if old_vault:
        rules.append(("vault.db", os.path.join(old_vault, "vault.db"), os.path.join(new_vault, "vault.db"), False))
        rules.append(("engine.db", os.path.join(old_vault, "engine.db"), os.path.join(new_vault, "engine.db"), False))
        rules.append(("vault/vectors", os.path.join(old_vault, "vectors"), os.path.join(new_vault, "vectors"), True))
        rules.append(("vault/documents", os.path.join(old_vault, "documents"), os.path.join(new_vault, "documents"), True))
        rules.append(("vault/health", os.path.join(old_vault, "health"), os.path.join(new_vault, "health"), True))
        rules.append(("vault/media", os.path.join(old_vault, "media"), os.path.join(new_vault, "media"), True))
        rules.append(("vault/backups", os.path.join(old_vault, "backups"), os.path.join(new_vault, "backups"), True))

    # --- Director config (from %LOCALAPPDATA%\CcDirector) ---
    old_director = os.path.join(local, "CcDirector") if local else ""
    new_director_cfg = os.path.join(base, "config", "director")
    if old_director:
        # The retired account store's file (accounts.json) is not carried forward: it holds sign-in tokens, and a copy
        # would be a second holder of them (License Switching, contract 1.1). It is left where it is.
        rules.append(("root-directories.json", os.path.join(old_director, "root-directories.json"), os.path.join(new_director_cfg, "root-directories.json"), False))
        rules.append(("whisper-models", os.path.join(old_director, "whisper-models"), os.path.join(base, "models", "whisper"), True))

    # --- Director config (from Documents\CcDirector) ---
    old_docs_dir = os.path.join(docs, "CcDirector")
    rules.append(("sessions.json", os.path.join(old_docs_dir, "sessions.json"), os.path.join(new_director_cfg, "sessions.json"), False))
    rules.append(("recent-sessions.json", os.path.join(old_docs_dir, "recent-sessions.json"), os.path.join(new_director_cfg, "recent-sessions.json"), False))
    rules.append(("repositories.json", os.path.join(old_docs_dir, "repositories.json"), os.path.join(new_director_cfg, "repositories.json"), False))
    rules.append(("sessions/", os.path.join(old_docs_dir, "sessions"), os.path.join(new_director_cfg, "sessions"), True))

    # --- Shared config ---
    old_cc_tools = str(home / ".cc_tools")
    rules.append(("config.json", os.path.join(old_cc_tools, "config.json"), os.path.join(base, "config", "config.json"), False))

    # --- Tool configs ---
    if local:
        # Outlook
        old_outlook = os.path.join(local, "cc-tools", "data", "outlook")
        rules.append(("outlook", old_outlook, os.path.join(base, "config", "outlook"), True))

        # Gmail
        old_gmail = os.path.join(local, "cc-tools", "data", "gmail")
        rules.append(("gmail", old_gmail, os.path.join(base, "config", "gmail"), True))

        # Comm queue
        old_comm = os.path.join(local, "cc-tools", "data", "comm_manager", "content")
        rules.append(("comm-queue", old_comm, os.path.join(base, "config", "comm-queue"), True))

        # Browser
        old_browser = os.path.join(local, "cc-browser")
        rules.append(("browser", old_browser, os.path.join(base, "config", "browser"), True))

    # Reddit
    old_reddit = str(home / ".cc-reddit")
    rules.append(("reddit/config.json", os.path.join(old_reddit, "config.json"), os.path.join(base, "config", "reddit", "config.json"), False))

    # LinkedIn
    old_linkedin = str(home / ".cc-linkedin")
    rules.append(("linkedin/config.json", os.path.join(old_linkedin, "config.json"), os.path.join(base, "config", "linkedin", "config.json"), False))

    # Vault config
    old_vault_cfg = str(home / ".cc-vault")
    rules.append(("vault/config.json", os.path.join(old_vault_cfg, "config.json"), os.path.join(base, "config", "vault", "config.json"), False))

    # --- Logs ---
    if old_director:
        old_logs = os.path.join(old_director, "logs")
        rules.append(("director/logs", old_logs, os.path.join(base, "logs", "director"), True))

    if old_vault and local:
        old_vault_logs = os.path.join(old_vault, "logs")
        rules.append(("engine/logs", old_vault_logs, os.path.join(base, "logs", "engine"), True))

    # Filter to only existing sources
    active_rules = []
    for label, src, dst, is_dir in rules:
        exists = os.path.isdir(src) if is_dir else os.path.isfile(src)
        if exists:
            active_rules.append((label, src, dst, is_dir))

    if not active_rules:
        print("[INFO] No legacy data found at old locations. Nothing to migrate.")
        return

    print(f"[INFO] Found {len(active_rules)} item(s) to sync:")
    print()

    for label, src, dst, is_dir in active_rules:
        kind = "DIR " if is_dir else "FILE"
        print(f"  [{kind}] {label}")
        print(f"         {src}")
        print(f"      -> {dst}")
        print()

    if dry_run:
        print("[INFO] Dry run complete. Use --run to actually sync data.")
        return

    # Execute sync
    print("[INFO] Starting sync...")
    print()

    results = {"synced": [], "skipped": [], "failed": []}

    for label, src, dst, is_dir in active_rules:
        if is_dir:
            sync_directory(src, dst, label, results)
        else:
            sync_file(src, dst, label, results)

    # Print report
    print("=" * 60)
    print("[REPORT]")
    print()

    if results["synced"]:
        print(f"  SYNCED ({len(results['synced'])}):")
        for item in results["synced"]:
            print(f"    [+] {item}")
        print()

    if results["skipped"]:
        print(f"  SKIPPED ({len(results['skipped'])}):")
        for item in results["skipped"]:
            print(f"    [-] {item}")
        print()

    if results["failed"]:
        print(f"  FAILED ({len(results['failed'])}):")
        for item in results["failed"]:
            print(f"    [X] {item}")
        print()

    print("=" * 60)
    total = len(results["synced"]) + len(results["skipped"]) + len(results["failed"])
    print(f"[DONE] Total: {total} | Synced: {len(results['synced'])} | Skipped: {len(results['skipped'])} | Failed: {len(results['failed'])}")

    if results["failed"]:
        print()
        print("[WARNING] Some items failed. Check output above.")
        sys.exit(1)

    if results["synced"]:
        print()
        print("[INFO] Migration successful. After verifying, these old dirs can be deleted:")
        _print_cleanup_list()


def _print_cleanup_list():
    """Print list of old directories that can be deleted."""
    local = _local()
    home = _home()
    docs = _docs()

    dirs = [
        str(home / ".cc_tools"),
        str(home / ".cc-vault"),
        str(home / ".cc-reddit"),
        str(home / ".cc-linkedin"),
        str(home / ".cc-tools"),
    ]
    if local:
        dirs.extend([
            os.path.join(local, "CcDirector"),
            os.path.join(local, "cc-myvault"),
            os.path.join(local, "cc-browser"),
            os.path.join(local, "cc-tools", "data"),
        ])
    dirs.append(os.path.join(docs, "CcDirector"))

    print()
    print("  NOTE: Executables now live at %LOCALAPPDATA%\\cc-director\\bin\\")
    print()
    for d in dirs:
        if os.path.exists(d):
            print(f"  - {d}")


def main():
    parser = argparse.ArgumentParser(description="Sync legacy cc-director data to unified storage")
    parser.add_argument("--run", action="store_true", help="Actually sync data (default is dry-run)")
    args = parser.parse_args()
    run_migration(dry_run=not args.run)


if __name__ == "__main__":
    main()
