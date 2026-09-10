using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Turns SeDebugPrivilege off in THIS process for a while, and puts it back.
///
/// WHY A TEST NEEDS THIS. SeDebugPrivilege exists to let a debugger open any process on the machine,
/// and it does that by bypassing the target's access control list entirely. A test that builds a
/// process it must not be able to open - by giving that process a list which denies everyone - is
/// therefore defeated by the privilege: the deny is written, and the open succeeds anyway.
///
/// That is not hypothetical. <see cref="SessionCommandExecutorLivenessTests"/> failed on every CI run
/// from at least 2026-09-08 with "this host still granted the test PROCESS_QUERY_LIMITED_INFORMATION
/// on a process whose access control list denies everyone". The GitHub Windows runner executes the
/// job elevated, so it holds SeDebugPrivilege; a developer machine running as a standard user does
/// not, which is why the same test passed locally and failed in CI for days. An explicit deny ACE for
/// that access right has no other way of being overridden - the object's owner is implicitly granted
/// READ_CONTROL and WRITE_DAC, and nothing else.
///
/// THE POINT IS TO KEEP THE TEST, NOT TO SKIP IT. Suppressing the privilege lets the real assertion
/// run on an elevated host instead of being reported as an unreachable precondition. Where the state
/// still cannot be produced, the caller fails loudly as it always did.
///
/// SCOPE. A privilege belongs to the process token, so this affects the whole test host while it is
/// held. Keep the window to the test that needs it and dispose promptly. Disposal restores exactly the
/// prior state, including the case where the privilege was already disabled.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DebugPrivilege : IDisposable
{
    private const string SeDebugName = "SeDebugPrivilege";
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes { public Luid Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint PrivilegeCount; public LuidAndAttributes Privilege; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, out TokenPrivileges previousState, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly IntPtr _token;
    private TokenPrivileges _previous;
    private readonly bool _restore;
    private bool _disposed;

    private DebugPrivilege(IntPtr token, TokenPrivileges previous, bool restore)
    {
        _token = token;
        _previous = previous;
        _restore = restore;
    }

    /// <summary>
    /// True when this process's token carries SeDebugPrivilege and it is currently ENABLED - the state
    /// in which an access control list is bypassed. Reported in failure messages so a host that cannot
    /// produce the unreadable state says whether this was the reason.
    /// </summary>
    public static bool IsEnabledForThisProcess()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, SeDebugName, out var luid)) return false;

            // Ask for a no-op adjustment: it reports the PREVIOUS state without changing anything that
            // matters, and its "not all assigned" error says the privilege is absent from the token.
            var request = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled },
            };
            if (!AdjustTokenPrivileges(token, false, ref request,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
                return false;
            if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned) return false;

            var wasEnabled = previous.PrivilegeCount == 1
                && (previous.Privilege.Attributes & SePrivilegeEnabled) == SePrivilegeEnabled;

            // Put back whatever was there before this question was asked.
            if (previous.PrivilegeCount == 1)
                AdjustTokenPrivileges(token, false, ref previous,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), out _, out _);
            return wasEnabled;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Disable SeDebugPrivilege until the returned object is disposed. Returns null when there is
    /// nothing to do or nothing can be done - the privilege is absent, already disabled, or the token
    /// refuses the adjustment - because in every one of those cases the caller's next attempt is
    /// unaffected by this and should proceed and be judged on its own result.
    /// </summary>
    public static DebugPrivilege? Suppress()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out var token))
            return null;

        if (!LookupPrivilegeValue(null, SeDebugName, out var luid))
        {
            CloseHandle(token);
            return null;
        }

        var request = new TokenPrivileges
        {
            PrivilegeCount = 1,
            Privilege = new LuidAndAttributes { Luid = luid, Attributes = 0 },   // 0 = disabled
        };
        if (!AdjustTokenPrivileges(token, false, ref request,
                (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _)
            || Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
        {
            CloseHandle(token);
            return null;
        }

        var wasEnabled = previous.PrivilegeCount == 1
            && (previous.Privilege.Attributes & SePrivilegeEnabled) == SePrivilegeEnabled;
        if (!wasEnabled)
        {
            // It was already off, so nothing was changed and there is nothing to restore.
            CloseHandle(token);
            return null;
        }

        return new DebugPrivilege(token, previous, restore: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_restore)
        {
            AdjustTokenPrivileges(_token, false, ref _previous,
                (uint)Marshal.SizeOf<TokenPrivileges>(), out _, out _);
        }
        CloseHandle(_token);
    }
}
