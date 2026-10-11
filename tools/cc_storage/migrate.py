"""Migration script for consolidating cc-director storage.

Detects data at old locations, copies to new cc-director locations,
validates the copy succeeded, and reports results.

Does NOT auto-delete old directories -- user does that manually after confirming.

Usage:
    python -m cc_storage.migrate          # Dry-run (show what would be copied)
    python -m cc_storage.migrate --run    # Actually copy data
"""

import argparse
import os
import shutil
import sys
from pathlib import Path

from .storage import CcStorage


def _local() -> str:
    return os.environ.get("LOCALAPPDATA", "")


def _home() -> Path:
    return Path.home()


def _docs() -> str:
    return os.path.join(os.environ.get("USERPROFILE", str(_home())), "Documents")


# Migration rules: (source_path, destination_path, is_directory)
def get_migration_rules():
    local = _local()
    home = _home()
    docs = _docs()

    rules = []

    # --- Vault data ---
    old_vault = os.path.join(local, "cc-myvault") if local else ""
    new_vault = str(CcStorage.vault())
    if old_vault:
        rules.append((os.path.join(old_vault, "vault.db"), os.path.join(new_vault, "vault.db"), False))
        rules.append((os.path.join(old_vault, "engine.db"), os.path.join(new_vault, "engine.db"), False))
        rules.append((os.path.join(old_vault, "vectors"), os.path.join(new_vault, "vectors"), True))
        rules.append((os.path.join(old_vault, "documents"), os.path.join(new_vault, "documents"), True))
        rules.append((os.path.join(old_vault, "health"), os.path.join(new_vault, "health"), True))
        rules.append((os.path.join(old_vault, "media"), os.path.join(new_vault, "media"), True))
        rules.append((os.path.join(old_vault, "backups"), os.path.join(new_vault, "backups"), True))

    # --- Director config (from %LOCALAPPDATA%\CcDirector) ---
    old_director = os.path.join(local, "CcDirector") if local else ""
    new_director_cfg = str(CcStorage.tool_config("director"))
    if old_director:
        # The retired account store's file (accounts.json) is not carried forward: it holds sign-in tokens, and a copy
        # would be a second holder of them (License Switching, contract 1.1). It is left where it is.
        rules.append((os.path.join(old_director, "root-directories.json"), os.path.join(new_director_cfg, "root-directories.json"), False))

    # --- Director config (from Documents\CcDirector) ---
    old_docs_dir = os.path.join(docs, "CcDirector")
    rules.append((os.path.join(old_docs_dir, "sessions.json"), os.path.join(new_director_cfg, "sessions.json"), False))
    rules.append((os.path.join(old_docs_dir, "recent-sessions.json"), os.path.join(new_director_cfg, "recent-sessions.json"), False))
    rules.append((os.path.join(old_docs_dir, "repositories.json"), os.path.join(new_director_cfg, "repositories.json"), False))
    rules.append((os.path.join(old_docs_dir, "sessions"), os.path.join(new_director_cfg, "sessions"), True))

    # --- Shared config ---
    old_cc_tools = str(home / ".cc_tools")
    rules.append((os.path.join(old_cc_tools, "config.json"), str(CcStorage.config_json()), False))

    # --- Tool configs ---
    if local:
        # Outlook
        old_outlook = os.path.join(local, "cc-tools", "data", "outlook")
        rules.append((old_outlook, str(CcStorage.tool_config("outlook")), True))

        # Gmail
        old_gmail = os.path.join(local, "cc-tools", "data", "gmail")
        rules.append((old_gmail, str(CcStorage.tool_config("gmail")), True))

        # Comm queue
        old_comm = os.path.join(local, "cc-tools", "data", "comm_manager", "content")
        rules.append((old_comm, str(CcStorage.tool_config("comm-queue")), True))

        # Browser
        old_browser = os.path.join(local, "cc-browser")
        rules.append((old_browser, str(CcStorage.tool_config("browser")), True))

    # Reddit
    old_reddit = str(home / ".cc-reddit")
    rules.append((os.path.join(old_reddit, "config.json"), os.path.join(str(CcStorage.tool_config("reddit")), "config.json"), False))

    # LinkedIn
    old_linkedin = str(home / ".cc-linkedin")
    rules.append((os.path.join(old_linkedin, "config.json"), os.path.join(str(CcStorage.tool_config("linkedin")), "config.json"), False))

    # Vault config
    old_vault_cfg = str(home / ".cc-vault")
    rules.append((os.path.join(old_vault_cfg, "config.json"), os.path.join(str(CcStorage.tool_config("vault")), "config.json"), False))

    # --- Logs ---
    if old_director:
        old_logs = os.path.join(old_director, "logs")
        rules.append((old_logs, str(CcStorage.tool_logs("director")), True))

    if old_vault and local:
        old_vault_logs = os.path.join(old_vault, "logs")
        rules.append((old_vault_logs, str(CcStorage.tool_logs("engine")), True))

    return rules


def _should_copy(src: str, dst: str) -> str:
    """Determine if src should overwrite dst using 'newer wins' strategy.

    Returns a reason string if copy should happen, empty string if not.
    """
    if not os.path.exists(dst):
        return "missing"

    src_size = os.path.getsize(src)
    dst_size = os.path.getsize(dst)

    # Destination is suspiciously small (empty/stale) and source has real data
    if dst_size < 10 and src_size >= 10:
        return f"dest empty ({dst_size}b), src has {src_size}b"

    # Source is newer by modification time
    src_mtime = os.path.getmtime(src)
    dst_mtime = os.path.getmtime(dst)
    if src_mtime > dst_mtime:
        return "newer"

    return ""


def copy_file(src: str, dst: str) -> bool:
    """Copy a single file using 'newer wins' strategy, creating parent dirs as needed."""
    if not os.path.isfile(src):
        return False

    reason = _should_copy(src, dst)
    if not reason:
        return False

    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)
    return True


def copy_directory(src: str, dst: str) -> int:
    """Copy a directory recursively using 'newer wins' per file. Returns count of files copied."""
    if not os.path.isdir(src):
        return 0

    copied = 0
    for root, dirs, files in os.walk(src):
        rel = os.path.relpath(root, src)
        dest_root = os.path.join(dst, rel) if rel != "." else dst
        os.makedirs(dest_root, exist_ok=True)

        for f in files:
            src_file = os.path.join(root, f)
            dst_file = os.path.join(dest_root, f)
            reason = _should_copy(src_file, dst_file)
            if reason:
                shutil.copy2(src_file, dst_file)
                copied += 1

    return copied


def validate_copy(src: str, dst: str, is_dir: bool) -> bool:
    """Validate that destination has the expected data."""
    if is_dir:
        if not os.path.isdir(dst):
            return False
        src_files = set()
        for root, _, files in os.walk(src):
            for f in files:
                rel = os.path.relpath(os.path.join(root, f), src)
                src_files.add(rel)
        for rel in src_files:
            if not os.path.exists(os.path.join(dst, rel)):
                return False
        return True
    else:
        if not os.path.isfile(dst):
            return False
        return os.path.getsize(dst) == os.path.getsize(src)


def run_migration(dry_run: bool = True) -> None:
    """Run the migration."""
    rules = get_migration_rules()

    found = []
    for src, dst, is_dir in rules:
        exists = os.path.isdir(src) if is_dir else os.path.isfile(src)
        if exists:
            found.append((src, dst, is_dir))

    if not found:
        print("[INFO] No legacy data found. Nothing to migrate.")
        return

    print(f"[INFO] Found {len(found)} item(s) to migrate:")
    print()

    for src, dst, is_dir in found:
        kind = "DIR " if is_dir else "FILE"
        already = os.path.exists(dst)
        status = " (already exists at destination)" if already else ""
        print(f"  [{kind}] {src}")
        print(f"     -> {dst}{status}")
        print()

    if dry_run:
        print("[INFO] Dry run complete. Use --run to actually copy data.")
        return

    print("[INFO] Starting migration...")
    print()

    copied_count = 0
    skipped_count = 0
    failed_count = 0

    for src, dst, is_dir in found:
        try:
            if is_dir:
                count = copy_directory(src, dst)
                if count > 0:
                    if validate_copy(src, dst, is_dir):
                        print(f"  [OK] {src} -> {dst} ({count} files)")
                        copied_count += 1
                    else:
                        print(f"  [WARN] {src} -> {dst} (copied but validation failed)")
                        failed_count += 1
                else:
                    print(f"  [SKIP] {src} (already at destination)")
                    skipped_count += 1
            else:
                if copy_file(src, dst):
                    if validate_copy(src, dst, is_dir):
                        print(f"  [OK] {src} -> {dst}")
                        copied_count += 1
                    else:
                        print(f"  [WARN] {src} -> {dst} (copied but validation failed)")
                        failed_count += 1
                else:
                    print(f"  [SKIP] {src} (already at destination or missing)")
                    skipped_count += 1
        except Exception as e:
            print(f"  [ERROR] {src}: {e}")
            failed_count += 1

    print()
    print(f"[DONE] Copied: {copied_count}, Skipped: {skipped_count}, Failed: {failed_count}")

    if failed_count == 0 and copied_count > 0:
        print()
        print("[INFO] Migration successful. Old directories can be cleaned up manually:")
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
    ]
    if local:
        dirs.extend([
            os.path.join(local, "CcDirector"),
            os.path.join(local, "cc-myvault"),
            os.path.join(local, "cc-browser"),
            os.path.join(local, "cc-tools", "data"),
        ])
    dirs.append(os.path.join(docs, "CcDirector"))

    for d in dirs:
        if os.path.exists(d):
            print(f"  - {d}")


def main():
    parser = argparse.ArgumentParser(description="Migrate cc-director storage to unified locations")
    parser.add_argument("--run", action="store_true", help="Actually copy data (default is dry-run)")
    args = parser.parse_args()
    run_migration(dry_run=not args.run)


if __name__ == "__main__":
    main()
