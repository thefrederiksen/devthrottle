using System.Globalization;
using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Removal;

/// <summary>
/// Resolves a path to its final form, the form the operating system itself acts on.
///
/// On Windows, <see cref="Path.GetFullPath"/> leaves short names (DOCUME~1) alone and keeps trailing
/// dots and alternate separators, so a rule could examine one path while the gate was about to act on
/// another that spells the same place differently. The operating system's own final-path answer
/// resolves all of that, which is why refusal 10 - a path that is not canonical after resolution -
/// uses it. On the other platforms <see cref="Path.GetFullPath"/> is the whole answer, and it is the
/// whole answer here too rather than a promise to write the other platforms later.
///
/// A path that cannot be resolved throws, and the refusal gate treats that as a refusal with the
/// reason - never as a pass.
/// </summary>
public static class CanonicalPath
{
    /// <summary>
    /// Resolve a path to its final form.
    /// </summary>
    /// <param name="path">The path as it was spelled.</param>
    /// <exception cref="IOException">The path cannot be resolved to any final form.</exception>
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileLog.Write($"[CanonicalPath] Resolve: path={path}");

        string resolved;
        if (OperatingSystem.IsWindows())
        {
            resolved = ResolveOnWindows(path);
        }
        else
        {
            try
            {
                resolved = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                throw new IOException($"the path {path} would not resolve: {ex.Message}");
            }
        }

        FileLog.Write($"[CanonicalPath] Resolve done: path={path}, result={resolved}");
        return resolved;
    }

    private static string ResolveOnWindows(string path)
    {
        // GetFullPath first, so a relative path resolves against the process working directory the
        // same way it would anywhere else, and so an unresolvable spelling fails before anything is
        // opened.
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new IOException($"the path {path} would not resolve: {ex.Message}");
        }

        using var handle = NativeMethods.CreateFileW(
            full,
            desiredAccess: 0,
            shareMode: NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            securityAttributes: IntPtr.Zero,
            creationDisposition: NativeMethods.OpenExisting,
            flagsAndAttributes: NativeMethods.FileFlagBackupSemantics, // without this a folder cannot be opened
            templateFile: IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            throw new IOException(
                $"the path {full} would not resolve; the operating system would not open it, code " +
                $"{code.ToString(CultureInfo.InvariantCulture)}");
        }

        var buffer = new char[NativeMethods.MaximumPathLength];
        var length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            var code = Marshal.GetLastWin32Error();
            throw new IOException(
                $"the path {full} would not resolve; the operating system would not name its final form, code " +
                $"{code.ToString(CultureInfo.InvariantCulture)}");
        }

        var named = new string(buffer, 0, (int)length);

        // The final-path answer is prefixed with the device form: \\?\C:\... and, for shares,
        // \\?\UNC\server\share\... Neither is the spelling any other part of this tool uses, so the
        // prefix is taken off rather than every comparison being taught about it.
        if (named.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + named[@"\\?\UNC\".Length..];
        if (named.StartsWith(@"\\?\", StringComparison.Ordinal))
            return named[@"\\?\".Length..];

        return named;
    }

    private static class NativeMethods
    {
        public const int FileShareRead = 1;
        public const int FileShareWrite = 2;
        public const int FileShareDelete = 4;
        public const int OpenExisting = 3;
        public const int FileFlagBackupSemantics = 0x02000000;
        public const int MaximumPathLength = 32768;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            int shareMode,
            IntPtr securityAttributes,
            int creationDisposition,
            int flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            char[] buffer,
            uint bufferLength,
            uint flags);
    }
}
