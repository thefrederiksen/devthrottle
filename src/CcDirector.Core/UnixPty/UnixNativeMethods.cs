using System.Runtime.InteropServices;

namespace CcDirector.Core.UnixPty;

/// <summary>
/// P/Invoke bindings for Unix/macOS PTY operations.
/// These are libc functions for pseudo-terminal management.
/// </summary>
internal static class UnixNativeMethods
{
    private const string LibC = "libc";
    private const string LibUtil = "libutil"; // macOS uses libutil for openpty

    // ioctl request codes differ by platform
    // Linux: 0x5414, macOS: 0x80087467
    public static ulong TIOCSWINSZ => RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? 0x80087467UL
        : 0x5414UL;

    // Standard file descriptors
    public const int STDIN_FILENO = 0;
    public const int STDOUT_FILENO = 1;
    public const int STDERR_FILENO = 2;

    // Signal constants
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;

    /// <summary>
    /// Create a pseudo-terminal pair.
    /// On success, master and subordinate contain file descriptors for the PTY.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int openpty(
        out int master,
        out int subordinate,
        IntPtr name,      // char* name - can be null
        IntPtr termios,   // struct termios* - can be null
        IntPtr winsize);  // struct winsize* - can be null

    /// <summary>
    /// Perform I/O control operations on a file descriptor.
    /// Used for resizing the terminal (TIOCSWINSZ).
    ///
    /// ioctl is a C VARIADIC function: int ioctl(int, unsigned long, ...). On Apple
    /// Silicon (arm64) macOS the variadic argument MUST be passed on the stack, not in
    /// a register. A plain `ref Winsize` parameter is passed in a register, so ioctl
    /// reads garbage from the stack and sets a nonsense window size (e.g. 28302 cols),
    /// which makes TUIs like Claude Code draw full-width rules that wrap hundreds of
    /// times. Calling through __arglist uses the genuine varargs calling convention and
    /// is correct on every platform/architecture.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, IntPtr argp);

    /// <summary>
    /// ioctl for arm64 macOS, where the variadic argument must arrive on the STACK,
    /// not in a register. AArch64 passes the first 8 integer/pointer arguments in
    /// x0-x7 and spills the 9th onto the stack -- exactly where a variadic callee
    /// reads its first vararg. By padding with 6 dummy register arguments we force
    /// <paramref name="argp"/> (the 9th argument) onto the stack. The dummies land in
    /// x2-x7 and are ignored by ioctl. This is the same EntryPoint ("ioctl"); only the
    /// managed call shape differs.
    /// </summary>
    [DllImport(LibC, SetLastError = true, EntryPoint = "ioctl")]
    public static extern int ioctl_stackarg(
        int fd, ulong request,
        long d2, long d3, long d4, long d5, long d6, long d7,
        IntPtr argp);

    /// <summary>
    /// Close a file descriptor.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int close(int fd);

    /// <summary>
    /// Read from a file descriptor.
    /// Returns number of bytes read, 0 on EOF, -1 on error.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern IntPtr read(int fd, byte[] buf, IntPtr count);

    /// <summary>
    /// Write to a file descriptor.
    /// Returns number of bytes written, -1 on error.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern IntPtr write(int fd, byte[] buf, IntPtr count);

    /// <summary>
    /// Create a child process.
    /// Returns 0 in child, child PID in parent, -1 on error.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int fork();

    /// <summary>
    /// Create a new session and set the process as session leader.
    /// Required before setting a controlling terminal.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int setsid();

    /// <summary>
    /// Duplicate a file descriptor.
    /// Makes newfd refer to the same file as oldfd.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int dup2(int oldfd, int newfd);

    /// <summary>
    /// Execute a program, searching PATH.
    /// argv must be null-terminated array.
    /// Does not return on success.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int execvp(
        [MarshalAs(UnmanagedType.LPStr)] string file,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv);

    /// <summary>
    /// Wait for child process to change state.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int waitpid(int pid, out int status, int options);

    /// <summary>
    /// Send a signal to a process.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int kill(int pid, int sig);

    // ---- posix_spawn: launch a child with stdio bound to a PTY subordinate ----
    //
    // We use posix_spawn rather than fork()+exec() because calling fork() in a
    // multi-threaded managed runtime (CoreCLR) is unsafe: only the calling
    // thread survives in the child, and any non-async-signal-safe call (managed
    // allocation, GC, libc malloc) between fork and exec can deadlock. posix_spawn
    // performs the fork/exec inside libc with all marshaling done in the parent.
    //
    // The opaque types posix_spawnattr_t and posix_spawn_file_actions_t are passed
    // as pointers to caller-allocated, zeroed native buffers (see UnixProcessHost).
    // On macOS these typedefs are themselves pointers; libc stores its handle in
    // the first word of the buffer. A generously sized buffer also satisfies the
    // struct layout used by glibc on Linux.

    public const int O_RDWR = 0x0002;

    // POSIX_SPAWN_SETSID (child becomes a new session leader) has a DIFFERENT numeric value on each
    // platform: 0x0400 on macOS (Darwin) and 0x0080 on Linux glibc. Passing the wrong platform's value
    // to posix_spawnattr_setflags fails with EINVAL (rc=22), which is exactly what a macOS-only 0x0400
    // did on Linux. Select the right one at run time.
    public const short POSIX_SPAWN_SETSID_MACOS = 0x0400;
    public const short POSIX_SPAWN_SETSID_LINUX = 0x0080;

    /// <summary>POSIX_SPAWN_SETSID for the current platform (macOS 0x0400, Linux glibc 0x0080).</summary>
    public static short POSIX_SPAWN_SETSID =>
        OperatingSystem.IsMacOS() ? POSIX_SPAWN_SETSID_MACOS : POSIX_SPAWN_SETSID_LINUX;

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawnp(
        out int pid,
        [MarshalAs(UnmanagedType.LPStr)] string file,
        IntPtr fileActions,
        IntPtr attrp,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] envp);

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_init(IntPtr fileActions);

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_adddup2(IntPtr fileActions, int filedes, int newfiledes);

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addclose(IntPtr fileActions, int filedes);

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addopen(
        IntPtr fileActions, int filedes,
        [MarshalAs(UnmanagedType.LPStr)] string path, int oflag, uint mode);

    /// <summary>Set the child's working directory (macOS 10.15+ / glibc 2.29+).</summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addchdir_np(
        IntPtr fileActions, [MarshalAs(UnmanagedType.LPStr)] string path);

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawnattr_destroy(IntPtr attr);

    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

    /// <summary>
    /// POSIX_SPAWN_SETPGROUP: put the child in the process group named by
    /// <see cref="posix_spawnattr_setpgroup"/>. 0x0002 on both macOS and Linux glibc - unlike
    /// POSIX_SPAWN_SETSID above, this one does NOT differ by platform.
    /// </summary>
    public const short POSIX_SPAWN_SETPGROUP = 0x0002;

    /// <summary>
    /// Set the child's process group. Zero means "the child becomes the leader of a new group whose
    /// id is its own pid", which is what lets a caller kill the child AND everything it started with
    /// a single kill(-pid). Takes effect only with POSIX_SPAWN_SETPGROUP in the flags.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawnattr_setpgroup(IntPtr attr, int pgroup);

    /// <summary>
    /// Create a pipe. On return fds[0] is the read end and fds[1] is the write end.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int pipe([Out] int[] fds);

    // fcntl commands and flags. F_SETFD is 2 and FD_CLOEXEC is 1 on both macOS and Linux.
    public const int F_SETFD = 2;
    public const int FD_CLOEXEC = 1;

    /// <summary>
    /// Manipulate a file descriptor. Used here only as fcntl(fd, F_SETFD, FD_CLOEXEC) to stop a
    /// descriptor being inherited across an exec.
    ///
    /// fcntl is a C VARIADIC function: int fcntl(int, int, ...) - the SAME hazard documented for
    /// <see cref="ioctl"/> above, and it bites in exactly the same way. On Apple Silicon (arm64)
    /// macOS the variadic argument must be passed on the STACK, not in a register, so a plain
    /// <c>int arg</c> parameter leaves fcntl reading garbage off the stack. It then sets the
    /// descriptor's flags to whatever it found, WITHOUT failing, so the call appears to work and
    /// the descriptor is simply not marked close-on-exec. Measured: a child started while this was
    /// declared the plain way inherited four of the parent's pipe descriptors, and the write end of
    /// its own standard input among them - so it never saw end of input and never exited.
    /// Call <see cref="SetCloseOnExec"/> rather than either of these directly.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int fcntl(int fd, int cmd, int arg);

    /// <summary>
    /// fcntl for arm64 macOS, where the variadic argument must arrive on the STACK. Six dummy
    /// register arguments push <paramref name="arg"/> into the ninth position, which is the first
    /// one AArch64 spills to the stack - precisely where a variadic callee looks for it. The
    /// dummies land in x3-x7 and fcntl ignores them. Same entry point; only the call shape differs.
    /// </summary>
    [DllImport(LibC, SetLastError = true, EntryPoint = "fcntl")]
    public static extern int fcntl_stackarg(
        int fd, int cmd,
        long d2, long d3, long d4, long d5, long d6, long d7,
        int arg);

    /// <summary>
    /// Mark a descriptor close-on-exec, so no child started anywhere in this process can inherit
    /// it. Returns false if the operating system refused, which a caller must not ignore: an
    /// unmarked pipe end leaking into an unrelated child is a read that never reaches end of file.
    /// </summary>
    public static bool SetCloseOnExec(int fd)
    {
        var rc = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? fcntl_stackarg(fd, F_SETFD, 0, 0, 0, 0, 0, 0, FD_CLOEXEC)
            : fcntl(fd, F_SETFD, FD_CLOEXEC);
        return rc != -1;
    }

    /// <summary>
    /// The operating system's own sentence for an error number. Used so a launch that failed for a
    /// reason other than a missing file reports itself in the system's words - "Permission denied",
    /// "Exec format error" - rather than being handed to a person as a bare number.
    /// </summary>
    [DllImport(LibC)]
    public static extern IntPtr strerror(int errnum);

    /// <summary>The message for an error number, or the number itself if the system has no text for it.</summary>
    public static string StrError(int errnum)
    {
        try
        {
            var p = strerror(errnum);
            var text = p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
            return string.IsNullOrWhiteSpace(text) ? $"error {errnum}" : text!;
        }
        catch (DllNotFoundException) { return $"error {errnum}"; }
        catch (EntryPointNotFoundException) { return $"error {errnum}"; }
    }

    /// <summary>Return the name of the subordinate PTY device for a master fd.</summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern IntPtr ptsname(int fd);

    /// <summary>
    /// Set environment variable.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int setenv(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        [MarshalAs(UnmanagedType.LPStr)] string value,
        int overwrite);

    /// <summary>
    /// Change current working directory.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int chdir([MarshalAs(UnmanagedType.LPStr)] string path);

    // waitpid options
    public const int WNOHANG = 1;

    /// <summary>
    /// Window size structure for terminal resize.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Winsize
    {
        public ushort ws_row;     // rows (characters)
        public ushort ws_col;     // columns (characters)
        public ushort ws_xpixel;  // horizontal size in pixels (unused)
        public ushort ws_ypixel;  // vertical size in pixels (unused)
    }

    /// <summary>
    /// Extract exit status from waitpid status.
    /// </summary>
    public static int WEXITSTATUS(int status) => (status >> 8) & 0xFF;

    /// <summary>
    /// Check if process exited normally.
    /// </summary>
    public static bool WIFEXITED(int status) => (status & 0x7F) == 0;

    /// <summary>Check if the process was terminated by a signal.</summary>
    public static bool WIFSIGNALED(int status) => ((sbyte)((status & 0x7F) + 1) >> 1) > 0;

    /// <summary>The signal number that terminated the process.</summary>
    public static int WTERMSIG(int status) => status & 0x7F;
}
