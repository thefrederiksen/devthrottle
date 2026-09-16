"""The store's only protection at rest is its permissions, so each check here is first shown to FAIL on a
loosened folder or file before it is trusted to pass on a private one."""

import os
import stat
import subprocess
import sys

import pytest

from conftest import add_entry, run_entry_point
from src import paths, permissions
from src.store import SecretStore
from src.storefile import UserOnlyFile

WINDOWS = sys.platform == "win32"
MACOS = sys.platform == "darwin"
# An account every Mac has, standing in for "another account" in the access control list tests.
OTHER_MAC_ACCOUNT = "_www"
BUILTIN_USERS_SID = "S-1-5-32-545"


def _icacls(*args):
    subprocess.run([os.path.join(os.environ["SystemRoot"], "System32", "icacls.exe"), *args],
                   check=True, capture_output=True)


def _permissions_snapshot(path):
    if WINDOWS:
        return permissions.windows_access_list(path)
    info = os.stat(path)
    if MACOS:
        return info.st_uid, stat.S_IMODE(info.st_mode), permissions.mac_access_entries(path)
    return info.st_uid, stat.S_IMODE(info.st_mode)


def test_Put_LeavesFolderAndFilePrivate(store):
    add_entry(store)

    assert permissions.folder_problem(paths.secrets_home()) is None
    assert permissions.file_problem(paths.store_path()) is None


def test_Put_LeavesNoTempFileBehind(store):
    add_entry(store)
    add_entry(store)

    assert sorted(p.name for p in paths.secrets_home().iterdir() if p.is_file()) == [".cc-secrets-folder", "secrets.json"]


def test_ExistingFolderThatIsNotPrivate_IsRefused_AndLeftExactlyAsItWas(tmp_path, monkeypatch):
    folder = tmp_path / "someone-elses-folder"
    folder.mkdir()
    (folder / "their-file.txt").write_text("theirs", encoding="utf-8")
    before = _permissions_snapshot(folder)
    assert permissions.folder_problem(folder) is not None
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)

    with pytest.raises(permissions.StorePermissionError, match="created itself"):
        paths.ensure_home()

    assert _permissions_snapshot(folder) == before
    assert sorted(p.name for p in folder.iterdir()) == ["their-file.txt"]


def test_Command_PointedAtAnExistingFolder_RefusesWithAClearMessage(tmp_path):
    folder = tmp_path / "someone-elses-folder"
    folder.mkdir()
    before = _permissions_snapshot(folder)

    result = run_entry_point(["run", "anything", "--", sys.executable, "-c", "print(1)"],
                             {"CC_SECRETS_HOME": str(folder), "CC_SESSION_ID": "permissions-test"})

    assert result.returncode != 0
    assert b"only changes the permissions of a folder it created itself" in result.stderr
    assert _permissions_snapshot(folder) == before
    assert list(folder.iterdir()) == []


def test_ExistingFolderThatIsAlreadyPrivate_IsAdopted(tmp_path, monkeypatch):
    folder = tmp_path / "already-private"
    folder.mkdir()
    if WINDOWS:
        user = permissions.current_user_sid()
        _icacls(str(folder), "/inheritance:r", "/grant:r", f"*{user}:(OI)(CI)F")
        # /inheritance:r drops inherited entries only. A folder can also carry explicit ones - the Windows
        # continuous integration runner's temp folder grants SYSTEM explicitly - so remove every other identity.
        for sid in {sid for _, sid in permissions.windows_access_list(folder)[1] if sid and sid != user}:
            _icacls(str(folder), "/remove", f"*{sid}")
    else:
        os.chmod(folder, 0o700)
    assert permissions.folder_problem(folder) is None, "test setup did not make the folder private"
    before = _permissions_snapshot(folder)
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)

    paths.ensure_home()

    assert (folder / permissions.FOLDER_MARKER).exists()
    assert _permissions_snapshot(folder) == before


def _remove_every_direct_grant(path):
    # /inheritance:r drops inherited entries only, and /grant:r does not replace a direct grant whose inheritance
    # flags differ - it adds a second one beside it. On the Windows continuous integration runner a new file or
    # folder already carries direct grants (SYSTEM, and this user with inheritance), so every identity, this user
    # included, is removed before the test grants what it means to.
    _icacls(str(path), "/inheritance:r")
    for sid in {sid for _, sid in permissions.windows_access_list(path)[1] if sid}:
        _icacls(str(path), "/remove", f"*{sid}")


def _folder_granted_to_this_user_only(tmp_path, name, grant):
    folder = tmp_path / name
    folder.mkdir()
    user = permissions.current_user_sid()
    _remove_every_direct_grant(folder)
    _icacls(str(folder), "/grant", f"*{user}:{grant}")
    entries = permissions.windows_access_entries(folder)[1]
    assert len(entries) == 1 and entries[0][2] == user, f"test setup left other grants: {entries}"
    return folder


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_FolderWhoseGrantIsNotInheritedByNewFiles_IsNotPrivate(tmp_path):
    # The review's probe, steps 1-3: a "this folder only" grant (no (OI)(CI)).
    folder = _folder_granted_to_this_user_only(tmp_path, "private-no-inheritance", "F")

    assert "new files" in (permissions.folder_problem(folder) or "")


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_SaveIntoAFolderWithoutAnInheritableGrant_NeverSucceedsUnreadable(tmp_path, monkeypatch):
    # The review's probe, steps 5-6: the save used to succeed and leave a store nobody could read back.
    import json

    from src.storefile import UserOnlyFile

    folder = _folder_granted_to_this_user_only(tmp_path, "private-no-inheritance", "F")
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)
    data = json.dumps({"version": 1, "entries": []}).encode("utf-8")
    store_file = UserOnlyFile(folder / "secrets.json")

    try:
        store_file.write(data)
        saved = True
    except permissions.StorePermissionError:
        saved = False

    if saved:
        assert store_file.read() == data
    assert saved is False
    assert not (folder / "secrets.json").exists()


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_AdoptedFolderWithAnInheritableGrant_SavesAndReadsBack(tmp_path, monkeypatch):
    import json

    from src.storefile import UserOnlyFile

    folder = _folder_granted_to_this_user_only(tmp_path, "private-inheriting", "(OI)(CI)F")
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)
    data = json.dumps({"version": 1, "entries": []}).encode("utf-8")
    store_file = UserOnlyFile(folder / "secrets.json")

    store_file.write(data)

    assert store_file.read() == data
    assert permissions.file_problem(folder / "secrets.json") is None


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_FileWithAnEmptyAccessList_IsNotPrivate(tmp_path):
    locked_out = tmp_path / "locked-out.txt"
    locked_out.write_text("x", encoding="utf-8")
    _remove_every_direct_grant(locked_out)
    try:
        assert permissions.windows_access_list(locked_out)[1] == [], "test setup left grants on the file"
        assert "empty access list" in (permissions.file_problem(locked_out) or "")
    finally:
        _icacls(str(locked_out), "/reset")


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_FolderGrantsOnlyTheCurrentUser_AndInheritsNothing(store):
    add_entry(store)

    protected, entries = permissions.windows_access_list(paths.secrets_home())
    _, file_entries = permissions.windows_access_list(paths.store_path())

    assert protected is True
    assert {sid for _, sid in entries} == {permissions.current_user_sid()}
    assert {sid for _, sid in file_entries} == {permissions.current_user_sid()}


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_LoosenedFolderItCreated_IsDetected_ThenTightened(store):
    add_entry(store)
    folder = paths.secrets_home()
    _icacls(str(folder), "/grant", f"*{BUILTIN_USERS_SID}:(OI)(CI)R")

    assert BUILTIN_USERS_SID in (permissions.folder_problem(folder) or "")
    note = permissions.ensure_private_folder(folder)

    assert note is not None
    assert permissions.folder_problem(folder) is None


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_InheritingFolder_IsDetected(tmp_path):
    folder = tmp_path / "inherits"
    folder.mkdir()

    assert "inherits permissions" in (permissions.folder_problem(folder) or "")


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_LoosenedStoreFile_IsTightenedOnRead(store):
    add_entry(store)
    _icacls(str(paths.store_path()), "/grant", f"*{BUILTIN_USERS_SID}:R")
    assert permissions.file_problem(paths.store_path()) is not None

    store.entries()

    assert permissions.file_problem(paths.store_path()) is None


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_TempFile_IsPrivateBeforeAnyByteIsWritten(home):
    folder = paths.ensure_home()
    temp = folder / "probe.tmp"

    descriptor = permissions.create_private_file(temp)
    try:
        assert permissions.file_problem(temp) is None
        assert {sid for _, sid in permissions.windows_access_list(temp)[1]} == {permissions.current_user_sid()}
    finally:
        os.close(descriptor)


@pytest.mark.skipif(WINDOWS, reason="POSIX mode check")
def test_Posix_FolderIs0700_AndFileIs0600(store):
    add_entry(store)

    assert stat.S_IMODE(os.stat(paths.secrets_home()).st_mode) == 0o700
    assert stat.S_IMODE(os.stat(paths.store_path()).st_mode) == 0o600


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_NewFileThatWouldNotBePrivate_IsRefusedBeforeAnythingIsWritten(tmp_path):
    # The review's probe, step 4: in a folder whose grant new files do not inherit, a new file gets the
    # process's default permissions. Creating it must be refused, and nothing left behind.
    folder = _folder_granted_to_this_user_only(tmp_path, "private-no-inheritance", "F")
    probe = folder / "probe.tmp"

    with pytest.raises(permissions.StorePermissionError, match="would not be private"):
        permissions.create_private_file(probe)

    assert not probe.exists()


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_SaveThatCannotBeReadBack_IsReportedAsFailed(home, monkeypatch):
    # Defence behind the folder check: whatever leaves the saved file unreadable, the save must not report success.
    import json

    from src.storefile import UserOnlyFile

    def lock_everyone_out(path):
        _icacls(str(path), "/inheritance:r")
        return None

    monkeypatch.setattr(permissions, "ensure_private_file", lock_everyone_out)
    data = json.dumps({"version": 1, "entries": []}).encode("utf-8")
    store_file = UserOnlyFile(paths.store_path())

    with pytest.raises(permissions.StorePermissionError, match="cannot be read back"):
        store_file.write(data)
    _icacls(str(paths.store_path()), "/reset")


@pytest.mark.skipif(WINDOWS, reason="POSIX mode check")
def test_Posix_LoosenedFile_IsDetected_ThenTightenedOnRead(store):
    add_entry(store)
    os.chmod(paths.store_path(), 0o644)
    assert permissions.file_problem(paths.store_path()) is not None

    store.entries()

    assert stat.S_IMODE(os.stat(paths.store_path()).st_mode) == 0o600


def _chmod_acl(*args):
    subprocess.run(["/bin/chmod", *args], check=True, capture_output=True)


def _grant_other_account(path, rule=f"user:{OTHER_MAC_ACCOUNT} allow read"):
    _chmod_acl("+a", rule, str(path))
    assert permissions.mac_access_entries(path), f"test setup did not add an access control list entry to {path}"


def _private_mode_folder(tmp_path, name):
    folder = tmp_path / name
    folder.mkdir()
    os.chmod(folder, 0o700)
    return folder


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_FolderWithPrivateModeButAnAccessListGrant_IsNotPrivate(tmp_path):
    folder = _private_mode_folder(tmp_path, "grants-another-account")
    assert permissions.folder_problem(folder) is None, "test setup: mode 0700 alone must pass"

    _grant_other_account(folder)

    assert OTHER_MAC_ACCOUNT in (permissions.folder_problem(folder) or "")


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_FolderGrantingAGroup_IsNotPrivate(tmp_path):
    folder = _private_mode_folder(tmp_path, "grants-everyone")

    _grant_other_account(folder, "group:everyone allow list")

    assert "group everyone" in (permissions.folder_problem(folder) or "")


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_FolderGrantOnlyPassedOnToNewFiles_IsNotPrivate(tmp_path):
    # An inherit-only entry grants nothing on the folder itself, but every file created inside is born with it.
    folder = _private_mode_folder(tmp_path, "inherit-only-grant")

    _grant_other_account(folder, f"user:{OTHER_MAC_ACCOUNT} allow read,file_inherit,only_inherit")

    assert OTHER_MAC_ACCOUNT in (permissions.folder_problem(folder) or "")


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_FileWithPrivateModeButAnAccessListGrant_IsNotPrivate(tmp_path):
    path = tmp_path / "file.json"
    path.write_text("x", encoding="utf-8")
    os.chmod(path, 0o600)
    assert permissions.file_problem(path) is None, "test setup: mode 0600 alone must pass"

    _grant_other_account(path)

    assert OTHER_MAC_ACCOUNT in (permissions.file_problem(path) or "")


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_DenyEntriesAndGrantsToThisUser_StayPrivate(tmp_path):
    import pwd

    folder = _private_mode_folder(tmp_path, "deny-and-self")
    _chmod_acl("+a", "group:everyone deny delete", str(folder))
    _chmod_acl("+a", f"user:{pwd.getpwuid(os.getuid()).pw_name} allow read,file_inherit", str(folder))
    assert len(permissions.mac_access_entries(folder)) == 2, "test setup did not add both entries"

    assert permissions.folder_problem(folder) is None


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_LoosenedFolderItCreated_IsDetected_ThenStripped(store):
    add_entry(store)
    folder = paths.secrets_home()
    _grant_other_account(folder, f"user:{OTHER_MAC_ACCOUNT} allow read,list,file_inherit,directory_inherit")
    assert OTHER_MAC_ACCOUNT in (permissions.folder_problem(folder) or "")

    note = permissions.ensure_private_folder(folder)

    assert note is not None and OTHER_MAC_ACCOUNT in note
    assert permissions.mac_access_entries(folder) == []
    assert stat.S_IMODE(os.stat(folder).st_mode) == 0o700
    assert permissions.folder_problem(folder) is None


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_ExistingFolderWithAnAccessListGrant_IsRefused_AndLeftExactlyAsItWas(tmp_path, monkeypatch):
    folder = _private_mode_folder(tmp_path, "someone-elses-shared-folder")
    _grant_other_account(folder)
    before = _permissions_snapshot(folder)
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)

    with pytest.raises(permissions.StorePermissionError, match="created itself"):
        paths.ensure_home()

    assert _permissions_snapshot(folder) == before
    assert OTHER_MAC_ACCOUNT in str(before)
    assert list(folder.iterdir()) == []


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_NewFolderUnderAParentThatPassesOnAGrant_IsCreatedPrivate(tmp_path, monkeypatch):
    parent = _private_mode_folder(tmp_path, "parent")
    _grant_other_account(parent, f"user:{OTHER_MAC_ACCOUNT} allow list,read,file_inherit,directory_inherit")
    folder = parent / "secrets-home"
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)

    paths.ensure_home()

    assert permissions.mac_access_entries(folder) == []
    assert permissions.folder_problem(folder) is None


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_LoosenedStoreFile_IsStrippedOnRead(store):
    add_entry(store)
    _grant_other_account(paths.store_path())
    assert OTHER_MAC_ACCOUNT in (permissions.file_problem(paths.store_path()) or "")

    store.entries()

    assert permissions.mac_access_entries(paths.store_path()) == []
    assert permissions.file_problem(paths.store_path()) is None


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_NewFileInAFolderThatPassesOnAGrant_IsRefusedBeforeAnythingIsWritten(tmp_path):
    folder = _private_mode_folder(tmp_path, "passes-on-a-grant")
    _grant_other_account(folder, f"user:{OTHER_MAC_ACCOUNT} allow read,file_inherit,only_inherit")
    # The premise: a file created with mode 0600 in this folder is still born carrying the grant.
    premise = folder / "premise.tmp"
    os.close(os.open(premise, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600))
    assert stat.S_IMODE(os.stat(premise).st_mode) == 0o600
    assert any("inherited" in entry.flags for entry in permissions.mac_access_entries(premise)
               if entry.who == f"user {OTHER_MAC_ACCOUNT}")
    premise.unlink()
    probe = folder / "probe.tmp"

    with pytest.raises(permissions.StorePermissionError, match="would not be private"):
        permissions.create_private_file(probe)

    assert not probe.exists()


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_SaveIntoAFolderThatPassesOnAGrant_WritesNothing(tmp_path, monkeypatch):
    # The check on the folder is bypassed so the save reaches the new-file check, the last line of defence.
    import json

    from src.storefile import UserOnlyFile

    folder = _private_mode_folder(tmp_path, "passes-on-a-grant")
    (folder / permissions.FOLDER_MARKER).write_text("x", encoding="utf-8")
    _grant_other_account(folder, f"user:{OTHER_MAC_ACCOUNT} allow read,file_inherit,only_inherit")
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", folder)
    store_file = UserOnlyFile(folder / "secrets.json")

    with pytest.raises(permissions.StorePermissionError, match="would not be private"):
        store_file.write(json.dumps({"version": 1, "entries": []}).encode("utf-8"))

    assert sorted(p.name for p in folder.iterdir()) == [permissions.FOLDER_MARKER]


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_Put_LeavesNoAccessListOnFolderOrFile(store):
    add_entry(store)

    assert permissions.mac_access_entries(paths.secrets_home()) == []
    assert permissions.mac_access_entries(paths.store_path()) == []


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_EntriesAreReadWithTheirKindWhoAndFlags(tmp_path):
    folder = _private_mode_folder(tmp_path, "read-back")
    _chmod_acl("+a", "group:everyone deny delete", str(folder))
    _grant_other_account(folder, f"user:{OTHER_MAC_ACCOUNT} allow read,file_inherit,directory_inherit,only_inherit")
    child = folder / "child"
    child.write_text("x", encoding="utf-8")

    entries = permissions.mac_access_entries(folder)
    child_entries = permissions.mac_access_entries(child)

    assert [(e.allow, e.who, e.flags) for e in entries] == [
        (False, "group everyone", frozenset()),
        (True, f"user {OTHER_MAC_ACCOUNT}", frozenset({"file_inherit", "directory_inherit", "only_inherit"})),
    ]
    assert [(e.allow, e.who, e.flags) for e in child_entries] == [(True, f"user {OTHER_MAC_ACCOUNT}", frozenset({"inherited"}))]
    assert child_entries[0].identifier == entries[1].identifier != permissions.current_user_uuid()


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_EntryThatListsNoPermissions_IsJudgedNotAnError(tmp_path):
    # Review of pull request 2960: acl_to_text drops the permissions field of such an entry, and a text parser
    # raised on it instead of judging it.
    folder = _private_mode_folder(tmp_path, "no-permissions-entry")
    _grant_other_account(folder, f"user:{OTHER_MAC_ACCOUNT} allow file_inherit")

    assert OTHER_MAC_ACCOUNT in (permissions.folder_problem(folder) or "")


@pytest.mark.skipif(not MACOS, reason="macOS access control list check")
def test_MacOS_FolderItCreatedWithANoPermissionsEntry_IsStripped_AndStaysUsable(store):
    add_entry(store)
    folder = paths.secrets_home()
    _grant_other_account(folder, f"user:{OTHER_MAC_ACCOUNT} allow file_inherit")

    note = permissions.ensure_private_folder(folder)
    add_entry(store, name="second")

    assert note is not None
    assert permissions.mac_access_entries(folder) == []
    assert {entry.name for entry in store.entries()} == {"devlinux", "second"}


@pytest.fixture
def ownership_ignored_volume(tmp_path):
    """A small disk image, attached by this user. macOS mounts such an image with ownership ignored."""
    image = tmp_path / "ownership.dmg"
    mount_point = tmp_path / "volume"
    mount_point.mkdir()
    subprocess.run(["hdiutil", "create", "-size", "20m", "-fs", "APFS", "-volname", "ccsecrets-test", str(image)],
                   check=True, capture_output=True, timeout=120)
    subprocess.run(["hdiutil", "attach", str(image), "-nobrowse", "-mountpoint", str(mount_point)],
                   check=True, capture_output=True, timeout=120)
    try:
        mounts = subprocess.run(["mount"], check=True, capture_output=True, text=True).stdout
        line = next(line for line in mounts.splitlines() if f" on {mount_point} " in line or
                    f" on {os.path.realpath(mount_point)} " in line)
        assert "noowners" in line, f"test setup: the image was not mounted with ownership ignored: {line}"
        yield mount_point
    finally:
        subprocess.run(["hdiutil", "detach", str(mount_point), "-force"], capture_output=True, timeout=120)


@pytest.mark.skipif(not MACOS, reason="macOS volume ownership check")
def test_MacOS_PrivateLookingFolderAndFileOnAVolumeThatIgnoresOwnership_AreNotPrivate(ownership_ignored_volume):
    folder = ownership_ignored_volume / "store"
    folder.mkdir()
    os.chmod(folder, 0o700)
    path = folder / "secrets.json"
    path.write_text("{}", encoding="utf-8")
    os.chmod(path, 0o600)
    assert permissions.mac_access_entries(folder) == [] and os.stat(path).st_uid == os.getuid()

    assert "ignore ownership" in (permissions.folder_problem(folder) or "")
    assert "ignore ownership" in (permissions.file_problem(path) or "")


@pytest.mark.skipif(not MACOS, reason="macOS volume ownership check")
def test_MacOS_StoreOnAVolumeThatIgnoresOwnership_IsRefused_AndNothingIsSaved(ownership_ignored_volume, monkeypatch):
    folder = ownership_ignored_volume / "secrets-home"
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    monkeypatch.setattr(paths, "_checked_home", None)
    store = SecretStore(UserOnlyFile(paths.store_path()))

    with pytest.raises(permissions.StorePermissionError, match="ignore ownership"):
        add_entry(store)

    assert not paths.store_path().exists()


def test_MacOS_StatfsSymbol_MatchesTheDeclaredLayoutOnEachArchitecture():
    # Second review of pull request 2960: on x86_64 plain statfs returns the legacy layout.
    assert permissions._statfs_symbol("arm64") == "statfs"
    assert permissions._statfs_symbol("x86_64") == "statfs$INODE64"
    with pytest.raises(permissions.StorePermissionError, match="does not know"):
        permissions._statfs_symbol("ppc")


def _legacy_x86_64_statfs(ctypes):
    # struct statfs from <sys/mount.h> when __DARWIN_64_BIT_INO_T is 0 - what plain statfs fills on x86_64.
    class LegacyStatFs(ctypes.Structure):
        _fields_ = [("f_otype", ctypes.c_short), ("f_oflags", ctypes.c_short), ("f_bsize", ctypes.c_long),
                    ("f_iosize", ctypes.c_long), ("f_blocks", ctypes.c_long), ("f_bfree", ctypes.c_long),
                    ("f_bavail", ctypes.c_long), ("f_files", ctypes.c_long), ("f_ffree", ctypes.c_long),
                    ("f_fsid", ctypes.c_int32 * 2), ("f_owner", ctypes.c_uint32), ("f_reserved1", ctypes.c_short),
                    ("f_type", ctypes.c_short), ("f_flags", ctypes.c_long), ("f_reserved2", ctypes.c_long * 2),
                    ("f_fstypename", ctypes.c_char * 15), ("f_mntonname", ctypes.c_char * 90),
                    ("f_mntfromname", ctypes.c_char * 90), ("f_reserved3", ctypes.c_char),
                    ("f_reserved4", ctypes.c_long * 4)]

    return LegacyStatFs


@pytest.mark.skipif(not MACOS, reason="macOS volume ownership check")
def test_MacOS_LegacyLayoutReadAsTheNewOne_IsRefusedNotTrusted(tmp_path):
    # What plain statfs does on an Intel Mac: the kernel fills the legacy layout, for a volume that ignores
    # ownership, and the reader interprets it with the 64-bit layout.
    import ctypes

    legacy_type = _legacy_x86_64_statfs(ctypes)
    assert ctypes.sizeof(ctypes.c_long) == 8 and legacy_type.f_fsid.offset == 64 and legacy_type.f_flags.offset == 80
    real = os.statvfs(tmp_path)
    legacy = legacy_type(f_otype=26, f_bsize=real.f_frsize, f_iosize=real.f_bsize, f_type=26,
                         f_flags=permissions._MNT_IGNORE_OWNERSHIP, f_fstypename=b"apfs",
                         f_mntonname=b"/Volumes/external")
    legacy.f_fsid[0], legacy.f_fsid[1] = 16777239, 26
    raw = bytes(legacy)

    def legacy_statfs(path, buffer):
        ctypes.memmove(buffer, raw, len(raw))
        return 0

    new_type = permissions._statfs_structure(ctypes)
    # The premise: reading f_flags at the 64-bit layout's offset misses the ownership flag entirely.
    assert not new_type.from_buffer_copy(raw.ljust(ctypes.sizeof(new_type), b"\0")).f_flags & permissions._MNT_IGNORE_OWNERSHIP

    with pytest.raises(permissions.StorePermissionError, match="does not match the volume"):
        permissions._read_volume(ctypes, legacy_statfs, new_type, tmp_path)


@pytest.mark.skipif(not MACOS, reason="macOS volume ownership check")
def test_MacOS_VolumeRead_DescribesThisPathsVolume(tmp_path):
    import platform

    ctypes_module, libc, structure = permissions._mac_api()
    statfs_function = getattr(libc, permissions._statfs_symbol(platform.machine()))

    info = permissions._read_volume(ctypes_module, statfs_function, structure, tmp_path)

    assert info.f_bsize == os.statvfs(tmp_path).f_frsize
    assert info.f_fstypename in (b"apfs", b"hfs")
    assert not info.f_flags & permissions._MNT_IGNORE_OWNERSHIP
