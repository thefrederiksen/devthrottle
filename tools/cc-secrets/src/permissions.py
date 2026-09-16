"""User-only permissions for the secret store folder and its files.

The store is a plain JSON file (owner decision on issue #2889), so these permissions are its whole
protection at rest:

- Windows: the folder's access list has inheritance removed and grants the current user alone; files
  created inside it inherit that single grant from the moment they exist.
- Linux: the folder is 0700 and each file is created 0600.
- macOS: the same modes, AND no access control list entry that allows anyone but this user, AND a volume
  that does not ignore ownership (on one that does, every account opens every file, so it is refused). An
  access control list entry can let another account read a file whose mode is 0600 (review of pull request
  2891), and a file created in a folder whose list has an inheritable entry is born carrying it, whatever
  mode it was created with. Tightening strips the whole list (chmod -N) and sets the mode. A new file is checked the moment it
  exists, before a byte is written, and refused if it inherited anything.

Every access checks the permissions, tightens them when they have been loosened (and logs that it did),
and refuses to go on if they are still open to anyone else. Checking reads the access list directly through
the operating system's own interface (the Windows security API, the macOS acl functions), so it costs no
subprocess; tightening runs icacls on Windows and chmod on macOS.

What this does not stop, stated plainly: an administrator or root can read any file on the machine, and
any process running as this same user - an agent's shell included - can read it too. The rule that the
model never sees a secret is kept by the tool never printing one, not by the file being unreadable to
the user the agent runs as.
"""

from __future__ import annotations

import os
import stat
import subprocess
import sys
from pathlib import Path
from typing import FrozenSet, List, NamedTuple, Optional, Tuple

from .errors import CcSecretsError

# Written into a folder once cc-secrets has created and locked it. Only such a folder is ever tightened.
FOLDER_MARKER = ".cc-secrets-folder"


class StorePermissionError(CcSecretsError, RuntimeError):
    """The store's folder or file is open to someone other than this user and could not be tightened."""


# --------------------------------------------------------------------------------------------------
# Windows
# --------------------------------------------------------------------------------------------------

_ACCESS_ALLOWED_ACE_TYPE = 0
_ACCESS_DENIED_ACE_TYPE = 1
_SE_DACL_PROTECTED = 0x1000
_user_sid_cache: Optional[str] = None


def _win_api():
    import ctypes
    from ctypes import wintypes

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    advapi32.GetNamedSecurityInfoW.argtypes = [
        ctypes.c_wchar_p, ctypes.c_int, wintypes.DWORD, ctypes.c_void_p, ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    advapi32.GetNamedSecurityInfoW.restype = wintypes.DWORD
    advapi32.GetSecurityDescriptorControl.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(wintypes.WORD), ctypes.POINTER(wintypes.DWORD)]
    advapi32.GetSecurityDescriptorControl.restype = wintypes.BOOL
    advapi32.GetAce.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p)]
    advapi32.GetAce.restype = wintypes.BOOL
    advapi32.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    advapi32.ConvertSidToStringSidW.restype = wintypes.BOOL
    advapi32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi32.OpenProcessToken.restype = wintypes.BOOL
    advapi32.GetTokenInformation.argtypes = [
        wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    advapi32.GetTokenInformation.restype = wintypes.BOOL
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    kernel32.LocalFree.restype = ctypes.c_void_p
    return ctypes, wintypes, advapi32, kernel32


def _sid_to_string(api, sid_pointer: int) -> str:
    ctypes, _, advapi32, kernel32 = api
    out = ctypes.c_void_p()
    if not advapi32.ConvertSidToStringSidW(sid_pointer, ctypes.byref(out)):
        raise StorePermissionError(f"ConvertSidToStringSidW failed (Windows error {ctypes.get_last_error()}).")
    try:
        return ctypes.wstring_at(out.value)
    finally:
        kernel32.LocalFree(out)


def current_user_sid() -> str:
    """The security identifier of the user this process runs as."""
    global _user_sid_cache
    if _user_sid_cache is not None:
        return _user_sid_cache
    api = _win_api()
    ctypes, wintypes, advapi32, kernel32 = api
    TOKEN_QUERY = 0x0008
    TOKEN_USER_CLASS = 1
    token = wintypes.HANDLE()
    if not advapi32.OpenProcessToken(kernel32.GetCurrentProcess(), TOKEN_QUERY, ctypes.byref(token)):
        raise StorePermissionError(f"OpenProcessToken failed (Windows error {ctypes.get_last_error()}).")
    try:
        size = wintypes.DWORD()
        advapi32.GetTokenInformation(token, TOKEN_USER_CLASS, None, 0, ctypes.byref(size))
        buffer = ctypes.create_string_buffer(size.value)
        if not advapi32.GetTokenInformation(token, TOKEN_USER_CLASS, buffer, size, ctypes.byref(size)):
            raise StorePermissionError(f"GetTokenInformation failed (Windows error {ctypes.get_last_error()}).")
        sid_pointer = ctypes.c_void_p.from_buffer(buffer).value
        _user_sid_cache = _sid_to_string(api, sid_pointer)
        return _user_sid_cache
    finally:
        kernel32.CloseHandle(token)


def windows_access_entries(path: Path) -> Tuple[bool, Optional[List[Tuple[int, int, Optional[str]]]]]:
    """(inheritance removed, [(entry type, entry flags, security identifier)]). The list is None for a null
    access list, which Windows treats as full access for everyone."""
    api = _win_api()
    ctypes, wintypes, advapi32, kernel32 = api
    SE_FILE_OBJECT = 1
    DACL_SECURITY_INFORMATION = 0x4
    dacl = ctypes.c_void_p()
    descriptor = ctypes.c_void_p()
    rc = advapi32.GetNamedSecurityInfoW(str(path), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, None, None,
                                        ctypes.byref(dacl), None, ctypes.byref(descriptor))
    if rc != 0:
        raise StorePermissionError(f"Could not read the permissions of {path} (Windows error {rc}).")
    try:
        control = wintypes.WORD()
        revision = wintypes.DWORD()
        if not advapi32.GetSecurityDescriptorControl(descriptor, ctypes.byref(control), ctypes.byref(revision)):
            raise StorePermissionError(f"GetSecurityDescriptorControl failed (Windows error {ctypes.get_last_error()}).")
        protected = bool(control.value & _SE_DACL_PROTECTED)
        if not dacl.value:
            return protected, None

        class AclHeader(ctypes.Structure):
            _fields_ = [("AclRevision", ctypes.c_ubyte), ("Sbz1", ctypes.c_ubyte), ("AclSize", wintypes.WORD),
                        ("AceCount", wintypes.WORD), ("Sbz2", wintypes.WORD)]

        class AceHeader(ctypes.Structure):
            _fields_ = [("AceType", ctypes.c_ubyte), ("AceFlags", ctypes.c_ubyte), ("AceSize", wintypes.WORD)]

        count = ctypes.cast(dacl, ctypes.POINTER(AclHeader)).contents.AceCount
        entries: List[Tuple[int, Optional[str]]] = []
        for index in range(count):
            ace = ctypes.c_void_p()
            if not advapi32.GetAce(dacl, index, ctypes.byref(ace)):
                raise StorePermissionError(f"GetAce failed (Windows error {ctypes.get_last_error()}).")
            header = ctypes.cast(ace, ctypes.POINTER(AceHeader)).contents
            ace_type, ace_flags = header.AceType, header.AceFlags
            sid = None
            if ace_type in (_ACCESS_ALLOWED_ACE_TYPE, _ACCESS_DENIED_ACE_TYPE):
                # ACCESS_ALLOWED_ACE / ACCESS_DENIED_ACE: 4-byte header, 4-byte mask, then the SID.
                sid = _sid_to_string(api, ace.value + 8)
            entries.append((ace_type, ace_flags, sid))
        return protected, entries
    finally:
        kernel32.LocalFree(descriptor)


def windows_access_list(path: Path) -> Tuple[bool, Optional[List[Tuple[int, Optional[str]]]]]:
    """(inheritance removed, [(entry type, security identifier)]): windows_access_entries without the flags."""
    protected, entries = windows_access_entries(path)
    return protected, None if entries is None else [(ace_type, sid) for ace_type, _, sid in entries]


_OBJECT_INHERIT_ACE = 0x1
_CONTAINER_INHERIT_ACE = 0x2
_INHERIT_ONLY_ACE = 0x8


def _windows_problem(path: Path, is_folder: bool) -> Optional[str]:
    """Why `path` is not private to this user, or None.

    Private means: an access list that is protected from inheritance (folders), not empty, granting nobody
    but this user, granting this user access to the object itself - and, for a folder, passing that grant on
    to new files and folders created inside it. Without the last rule a folder can look private while every
    file created in it gets the process's default permissions instead (review of pull request 2891).
    """
    protected, entries = windows_access_entries(path)
    if entries is None:
        return f"{path} has no access list, which gives everyone full access"
    if not entries:
        return f"{path} has an empty access list, so not even this user can open it"
    if is_folder and not protected:
        return f"{path} still inherits permissions from its parent folder"
    user = current_user_sid()
    for ace_type, _, sid in entries:
        if ace_type == _ACCESS_DENIED_ACE_TYPE:
            continue
        if ace_type != _ACCESS_ALLOWED_ACE_TYPE or sid != user:
            return f"{path} grants access to {sid or 'an entry of type ' + str(ace_type)}, not only to this user"
    user_grants = [flags for ace_type, flags, sid in entries if ace_type == _ACCESS_ALLOWED_ACE_TYPE and sid == user]
    if not any(not flags & _INHERIT_ONLY_ACE for flags in user_grants):
        return f"{path} does not grant this user access to it"
    if is_folder and not any(flags & _OBJECT_INHERIT_ACE and flags & _CONTAINER_INHERIT_ACE for flags in user_grants):
        return (f"{path} grants this user access to the folder only, not to new files inside it, so a file "
                "created there would not be private to this user")
    return None


def _icacls(*args: str) -> None:
    icacls = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "icacls.exe")
    result = subprocess.run([icacls, *args], capture_output=True, text=True, timeout=30)
    if result.returncode != 0:
        raise StorePermissionError(f"icacls {' '.join(args)} failed: {(result.stdout + result.stderr).strip()}")


def _tighten_windows_folder(folder: Path) -> None:
    user = current_user_sid()
    _icacls(str(folder), "/inheritance:r", "/grant:r", f"*{user}:(OI)(CI)F")
    _, entries = windows_access_list(folder)
    for sid in {sid for ace_type, sid in (entries or []) if sid and sid != user}:
        _icacls(str(folder), "/remove", f"*{sid}")


def _tighten_windows_file(path: Path) -> None:
    # Back to only what it inherits, which inside the locked folder is the single grant to this user.
    _icacls(str(path), "/reset")


# --------------------------------------------------------------------------------------------------
# macOS
# --------------------------------------------------------------------------------------------------

_ACL_TYPE_EXTENDED = 0x00000100
_ACL_FIRST_ENTRY = 0
_ACL_NEXT_ENTRY = -1
_ACL_EXTENDED_ALLOW = 1
_ACL_EXTENDED_DENY = 2
# acl_flag_t values from <sys/acl.h>.
_MAC_ACL_FLAGS = {"inherited": 1 << 4, "file_inherit": 1 << 5, "directory_inherit": 1 << 6,
                  "limit_inherit": 1 << 7, "only_inherit": 1 << 8}
_ID_TYPE_UID = 0
_ID_TYPE_GID = 1
_MNT_IGNORE_OWNERSHIP = 0x00200000
_ENOENT = 2
_user_uuid_cache: Optional[bytes] = None
_mac_api_cache = None


class MacAccessEntry(NamedTuple):
    """One entry of a macOS access control list, read through the system's acl entry functions."""
    allow: bool
    identifier: str
    who: str
    flags: FrozenSet[str]


def _mac_api():
    global _mac_api_cache
    if _mac_api_cache is not None:
        return _mac_api_cache
    import ctypes
    import ctypes.util

    class StatFs(ctypes.Structure):
        # struct statfs from <sys/mount.h> (the 64-bit inode layout, the only one on current macOS).
        _fields_ = [("f_bsize", ctypes.c_uint32), ("f_iosize", ctypes.c_int32), ("f_blocks", ctypes.c_uint64),
                    ("f_bfree", ctypes.c_uint64), ("f_bavail", ctypes.c_uint64), ("f_files", ctypes.c_uint64),
                    ("f_ffree", ctypes.c_uint64), ("f_fsid", ctypes.c_int32 * 2), ("f_owner", ctypes.c_uint32),
                    ("f_type", ctypes.c_uint32), ("f_flags", ctypes.c_uint32), ("f_fssubtype", ctypes.c_uint32),
                    ("f_fstypename", ctypes.c_char * 16), ("f_mntonname", ctypes.c_char * 1024),
                    ("f_mntfromname", ctypes.c_char * 1024), ("f_flags_ext", ctypes.c_uint32),
                    ("f_reserved", ctypes.c_uint32 * 7)]

    libc = ctypes.CDLL(ctypes.util.find_library("c"), use_errno=True)
    void_p, c_int = ctypes.c_void_p, ctypes.c_int
    signatures = {
        "acl_get_file": ([ctypes.c_char_p, c_int], void_p),
        "acl_get_entry": ([void_p, c_int, ctypes.POINTER(void_p)], c_int),
        "acl_get_tag_type": ([void_p, ctypes.POINTER(c_int)], c_int),
        "acl_get_qualifier": ([void_p], void_p),
        "acl_get_flagset_np": ([void_p, ctypes.POINTER(void_p)], c_int),
        "acl_get_flag_np": ([void_p, c_int], c_int),
        "acl_free": ([void_p], c_int),
        "mbr_uid_to_uuid": ([ctypes.c_uint32, void_p], c_int),
        "mbr_uuid_to_id": ([void_p, ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(c_int)], c_int),
        "statfs": ([ctypes.c_char_p, ctypes.POINTER(StatFs)], c_int),
    }
    for name, (argtypes, restype) in signatures.items():
        function = getattr(libc, name)
        function.argtypes, function.restype = argtypes, restype
    _mac_api_cache = (ctypes, libc, StatFs)
    return _mac_api_cache


def _uuid_text(raw: bytes) -> str:
    import uuid

    return str(uuid.UUID(bytes=raw)).upper()


def current_user_uuid() -> str:
    """The directory-services identifier of the user this process runs as, as macOS keeps it in an access list."""
    return _uuid_text(_current_user_uuid_bytes())


def _current_user_uuid_bytes() -> bytes:
    global _user_uuid_cache
    if _user_uuid_cache is not None:
        return _user_uuid_cache
    ctypes, libc, _ = _mac_api()
    buffer = (ctypes.c_ubyte * 16)()
    rc = libc.mbr_uid_to_uuid(os.getuid(), buffer)
    if rc != 0:
        raise StorePermissionError(f"Could not look up the identifier of user {os.getuid()} (error {rc}).")
    _user_uuid_cache = bytes(buffer)
    return _user_uuid_cache


def _describe_mac_identity(api, qualifier: int, raw: bytes) -> str:
    """'user NAME' or 'group NAME' for a message. Used only to word a refusal, never to decide one."""
    import grp
    import pwd

    ctypes, libc, _ = api
    number = ctypes.c_uint32()
    kind = ctypes.c_int()
    if libc.mbr_uuid_to_id(qualifier, ctypes.byref(number), ctypes.byref(kind)) != 0:
        return f"unresolved identifier {_uuid_text(raw)}"
    try:
        if kind.value == _ID_TYPE_UID:
            return f"user {pwd.getpwuid(number.value).pw_name}"
        if kind.value == _ID_TYPE_GID:
            return f"group {grp.getgrgid(number.value).gr_name}"
    except KeyError:
        pass
    return f"identifier {_uuid_text(raw)} (id {number.value}, type {kind.value})"


def mac_access_entries(path: Path) -> List[MacAccessEntry]:
    """The access control list of `path`, empty when it has none.

    Read entry by entry through acl_get_entry and its companions, not by parsing acl_to_text: that text
    prints names unescaped (a name may contain the separator) and drops the permissions field of an entry
    that has none, so a parser can be made to fail on a folder that should simply be judged (review of pull
    request 2960).
    """
    api = _mac_api()
    ctypes, libc, _ = api
    acl = libc.acl_get_file(os.fsencode(str(path)), _ACL_TYPE_EXTENDED)
    if not acl:
        error = ctypes.get_errno()
        if error == _ENOENT:
            return []
        raise StorePermissionError(f"Could not read the access control list of {path} ({os.strerror(error)}).")
    entries: List[MacAccessEntry] = []
    try:
        entry = ctypes.c_void_p()
        which = _ACL_FIRST_ENTRY
        while libc.acl_get_entry(acl, which, ctypes.byref(entry)) == 0:
            which = _ACL_NEXT_ENTRY
            tag = ctypes.c_int()
            if libc.acl_get_tag_type(entry, ctypes.byref(tag)) != 0:
                raise StorePermissionError(f"Could not read an access control list entry type of {path}.")
            if tag.value not in (_ACL_EXTENDED_ALLOW, _ACL_EXTENDED_DENY):
                raise StorePermissionError(f"{path} has an access control list entry of unknown type {tag.value}.")
            qualifier = libc.acl_get_qualifier(entry)
            if not qualifier:
                raise StorePermissionError(f"Could not read who an access control list entry of {path} is for.")
            try:
                raw = ctypes.string_at(qualifier, 16)
                who = _describe_mac_identity(api, qualifier, raw)
            finally:
                libc.acl_free(qualifier)
            flagset = ctypes.c_void_p()
            if libc.acl_get_flagset_np(entry, ctypes.byref(flagset)) != 0:
                raise StorePermissionError(f"Could not read the flags of an access control list entry of {path}.")
            flags = frozenset(name for name, bit in _MAC_ACL_FLAGS.items() if libc.acl_get_flag_np(flagset, bit) == 1)
            entries.append(MacAccessEntry(tag.value == _ACL_EXTENDED_ALLOW, _uuid_text(raw), who, flags))
    finally:
        libc.acl_free(acl)
    return entries


def _mac_acl_problem(path: Path) -> Optional[str]:
    """Why the access control list of `path` lets someone else in, or None.

    Any entry that ALLOWS anyone but this user is a problem, whatever permissions it lists (none included),
    and whether it applies to the path itself or is only passed on to new files (an inherit-only entry on the
    folder is exactly how a new file is born open). Deny entries and entries for this user take nothing away
    from privacy.
    """
    user = current_user_uuid()
    for entry in mac_access_entries(path):
        if entry.allow and entry.identifier != user:
            how = " (inherited)" if "inherited" in entry.flags else ""
            return f"{path} has an access control list entry{how} that allows {entry.who}, not only this user"
    return None


def _mac_volume_problem(path: Path) -> Optional[str]:
    """Why the volume holding `path` cannot keep anything private, or None.

    With "Ignore ownership on this volume" set (an external drive, or a disk image attached by a user, which
    macOS mounts that way by default) every account is treated as the owner of every file, so neither the
    mode nor the owner means anything there (review of pull request 2960).
    """
    ctypes, libc, StatFs = _mac_api()
    info = StatFs()
    if libc.statfs(os.fsencode(str(path)), ctypes.byref(info)) != 0:
        error = ctypes.get_errno()
        raise StorePermissionError(f"Could not read the volume of {path} ({os.strerror(error)}).")
    if info.f_flags & _MNT_IGNORE_OWNERSHIP:
        volume = info.f_mntonname.decode("utf-8", "replace")
        return (f"{path} is on {volume}, a volume set to ignore ownership, where every account can open every "
                "file whatever its permissions")
    return None


def _tighten_mac(path: Path, mode: int) -> None:
    result = subprocess.run(["/bin/chmod", "-N", str(path)], capture_output=True, text=True, timeout=30)
    if result.returncode != 0:
        raise StorePermissionError(f"chmod -N {path} failed: {(result.stdout + result.stderr).strip()}")
    os.chmod(path, mode)


# --------------------------------------------------------------------------------------------------
# macOS and Linux
# --------------------------------------------------------------------------------------------------

def _posix_problem(path: Path) -> Optional[str]:
    if sys.platform == "darwin":
        problem = _mac_volume_problem(path)
        if problem is not None:
            return problem
    info = os.stat(path)
    if info.st_uid != os.getuid():
        return f"{path} is owned by another user"
    if info.st_mode & 0o077:
        return f"{path} has mode {stat.S_IMODE(info.st_mode):o}; group and others must have no access"
    if sys.platform == "darwin":
        return _mac_acl_problem(path)
    return None


# --------------------------------------------------------------------------------------------------
# Public
# --------------------------------------------------------------------------------------------------

def folder_problem(folder: Path) -> Optional[str]:
    """Why the folder is not private to this user, or None when it is."""
    if sys.platform == "win32":
        return _windows_problem(folder, is_folder=True)
    return _posix_problem(folder)


def file_problem(path: Path) -> Optional[str]:
    """Why the file is not private to this user, or None when it is."""
    if sys.platform == "win32":
        return _windows_problem(path, is_folder=False)
    return _posix_problem(path)


def ensure_private_folder(folder: Path) -> Optional[str]:
    """Create the folder if needed and make sure it is private to this user.

    Permissions are only ever CHANGED on a folder cc-secrets created itself (it carries FOLDER_MARKER).
    An existing folder that is already private is adopted; an existing folder that is not private is
    refused and left exactly as it is, because it belongs to someone else's arrangement - pointing
    CC_SECRETS_HOME at it must not strip other accounts' access.

    Returns a note when it had to tighten permissions (for the log), None when they were already right.
    Raises StorePermissionError when the folder is not private and may not or cannot be made so.
    """
    created = not folder.exists()
    folder.mkdir(parents=True, exist_ok=True)
    marker = folder / FOLDER_MARKER
    problem = folder_problem(folder)
    if problem is None:
        if not marker.exists():
            marker.write_text("This folder holds cc-secrets data and is kept private to its owner." + chr(10), encoding="utf-8")
        return None
    if not created and not marker.exists():
        raise StorePermissionError(
            f"{folder} already exists and is not private to this user ({problem}). cc-secrets only changes the "
            "permissions of a folder it created itself, so it has left this one unchanged. Point CC_SECRETS_HOME "
            "at a folder that does not exist yet, or make this folder private to your account first."
        )
    if sys.platform == "win32":
        _tighten_windows_folder(folder)
    elif sys.platform == "darwin":
        _tighten_mac(folder, 0o700)
    else:
        os.chmod(folder, 0o700)
    remaining = folder_problem(folder)
    if remaining is not None:
        raise StorePermissionError(f"The secrets folder is not private to this user: {remaining}")
    if not marker.exists():
        marker.write_text("This folder holds cc-secrets data and is kept private to its owner." + chr(10), encoding="utf-8")
    return f"tightened folder permissions ({problem})"


def ensure_private_file(path: Path) -> Optional[str]:
    """Make an existing file private to this user; same contract as ensure_private_folder."""
    problem = file_problem(path)
    if problem is None:
        return None
    if sys.platform == "win32":
        _tighten_windows_file(path)
    elif sys.platform == "darwin":
        _tighten_mac(path, 0o600)
    else:
        os.chmod(path, 0o600)
    remaining = file_problem(path)
    if remaining is not None:
        raise StorePermissionError(f"A secret store file is not private to this user: {remaining}")
    return f"tightened file permissions ({problem})"


def create_private_file(path: Path) -> int:
    """Create a new file that is private from its first byte, and return its descriptor.

    The caller must already have made the parent folder private: on Windows the file takes the folder's
    single grant by inheritance at creation, and on macOS and Linux it is created with mode 0600. On macOS a
    file also takes the folder's inheritable access control list entries, whatever its mode, so the new file
    is checked before anything is written and refused (and removed) if it carries one that lets anyone else in.
    """
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_BINARY", 0)
    descriptor = os.open(path, flags, 0o600)
    problem = file_problem(path)
    if problem is not None:
        os.close(descriptor)
        path.unlink()
        raise StorePermissionError(f"A new file in the secrets folder would not be private to this user ({problem}), "
                                   "so nothing was written.")
    return descriptor
