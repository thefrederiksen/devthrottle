# Plan: Mac Support for CC Director

## Executive Summary

Replace Windows-specific ConPty and named pipes with cross-platform alternatives to enable Mac support while preserving the existing `ISessionBackend` abstraction.

**Decisions:**
- **UI:** Avalonia UI (cross-platform WPF-like)
- **PTY bindings:** Manual P/Invoke (no external dependencies)

---

## Current Windows-Specific Components

| Component | Location | Windows API | Purpose |
|-----------|----------|-------------|---------|
| **ConPty** | `src/CcDirector.Core/ConPty/` | `CreatePseudoConsole`, `ResizePseudoConsole` | Terminal emulation |
| **ProcessHost** | `src/CcDirector.Core/ConPty/ProcessHost.cs` | `CreateProcessW` with PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE | Process spawning attached to PTY |
| **Named Pipes** | `src/CcDirector.Core/Pipes/DirectorPipeServer.cs` | `NamedPipeServerStream` | Hook event IPC from Claude Code |
| **Hook Relay** | `src/CcDirector.Wpf/Hooks/hook-relay.ps1` | PowerShell + System.IO.Pipes | Relay hook JSON to Director |
| **EmbeddedBackend** | `src/CcDirector.Wpf/Backends/EmbeddedBackend.cs` | Real console window overlay | Legacy console mode (Windows-only) |
| **WPF UI** | `src/CcDirector.Wpf/` | Windows Presentation Foundation | UI layer |

---

## Implementation Phases

### Phase 1: Unix PTY Backend (Core)

Create Unix PTY implementation with manual P/Invoke bindings.

**New Files:**

| File | Purpose |
|------|---------|
| `src/CcDirector.Core/UnixPty/UnixNativeMethods.cs` | P/Invoke to libc (openpty, ioctl, fork, exec, etc.) |
| `src/CcDirector.Core/UnixPty/UnixPseudoConsole.cs` | PTY wrapper class |
| `src/CcDirector.Core/UnixPty/UnixProcessHost.cs` | Process lifecycle with PTY attachment |
| `src/CcDirector.Core/Backends/UnixPtyBackend.cs` | `ISessionBackend` implementation |

**Key P/Invoke Definitions:**

```csharp
internal static class UnixNativeMethods
{
    private const string LibC = "libc";

    [DllImport(LibC, SetLastError = true)]
    public static extern int openpty(out int master, out int slave,
        IntPtr name, IntPtr termios, IntPtr winsize);

    [DllImport(LibC, SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, ref Winsize ws);

    [DllImport(LibC, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(LibC, SetLastError = true)]
    public static extern int fork();

    [DllImport(LibC, SetLastError = true)]
    public static extern int setsid();

    [DllImport(LibC, SetLastError = true)]
    public static extern int dup2(int oldfd, int newfd);

    [DllImport(LibC, SetLastError = true)]
    public static extern int execvp(string file, string[] argv);

    [DllImport(LibC, SetLastError = true)]
    public static extern IntPtr read(int fd, byte[] buf, IntPtr count);

    [DllImport(LibC, SetLastError = true)]
    public static extern IntPtr write(int fd, byte[] buf, IntPtr count);

    // ioctl requests (differ by platform)
    public const ulong TIOCSWINSZ_LINUX = 0x5414;
    public const ulong TIOCSWINSZ_MACOS = 0x80087467;

    [StructLayout(LayoutKind.Sequential)]
    public struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }
}
```

**UnixPseudoConsole Pattern:**

```csharp
public sealed class UnixPseudoConsole : IDisposable
{
    public int MasterFd { get; }
    public int SlaveFd { get; }

    public static UnixPseudoConsole Create(short cols, short rows)
    {
        int master, slave;
        if (UnixNativeMethods.openpty(out master, out slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == -1)
            throw new InvalidOperationException($"openpty failed: {Marshal.GetLastWin32Error()}");

        var console = new UnixPseudoConsole(master, slave);
        console.Resize(cols, rows);
        return console;
    }

    public void Resize(short cols, short rows)
    {
        var ws = new UnixNativeMethods.Winsize { ws_col = (ushort)cols, ws_row = (ushort)rows };
        UnixNativeMethods.ioctl(MasterFd, GetTiocswinszValue(), ref ws);
    }

    public void Dispose()
    {
        UnixNativeMethods.close(MasterFd);
        UnixNativeMethods.close(SlaveFd);
    }

    private static ulong GetTiocswinszValue()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? UnixNativeMethods.TIOCSWINSZ_MACOS
            : UnixNativeMethods.TIOCSWINSZ_LINUX;
    }
}
```

---

### Phase 2: Cross-Platform IPC (Unix Domain Sockets)

Replace Windows named pipes with Unix domain sockets on Mac/Linux.

**New Files:**

| File | Purpose |
|------|---------|
| `src/CcDirector.Core/Pipes/IDirectorServer.cs` | Interface extracted from DirectorPipeServer |
| `src/CcDirector.Core/Pipes/UnixSocketServer.cs` | Unix domain socket implementation |

**Interface Definition:**

```csharp
public interface IDirectorServer : IDisposable
{
    event Action<PipeMessage>? OnMessageReceived;
    void Start();
    void Stop();
}
```

**UnixSocketServer Implementation:**

```csharp
public sealed class UnixSocketServer : IDirectorServer
{
    private static readonly string SocketPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cc_director", "director.sock");

    private Socket? _listener;
    private CancellationTokenSource? _cts;

    public event Action<PipeMessage>? OnMessageReceived;

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SocketPath)!);
        if (File.Exists(SocketPath)) File.Delete(SocketPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(10);

        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await _listener!.AcceptAsync(ct);
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        using (client)
        using (var stream = new NetworkStream(client))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var line = await reader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                var msg = JsonSerializer.Deserialize<PipeMessage>(line);
                if (msg != null) OnMessageReceived?.Invoke(msg);
            }
        }
    }
}
```

**Factory Method:**

```csharp
public static class DirectorServerFactory
{
    public static IDirectorServer Create(Action<string>? log)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new DirectorPipeServer(log);
        else
            return new UnixSocketServer(log);
    }
}
```

---

### Phase 3: Hook Relay Script (Python)

Create Mac/Linux hook relay to replace PowerShell.

**New File:** `hooks/hook-relay.py`

```python
#!/usr/bin/env python3
"""CC Director Hook Relay (Unix) - Reads hook JSON from stdin, sends to socket."""
import socket, sys, os

sock_path = os.path.expanduser("~/.cc_director/director.sock")
if not os.path.exists(sock_path):
    sys.exit(0)

data = sys.stdin.read().strip()
if not data:
    sys.exit(0)

try:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
        s.connect(sock_path)
        s.sendall((data + "\n").encode())
except:
    sys.exit(0)  # Silent fail
```

---

### Phase 4: SessionManager Platform Detection

**Modified File:** `src/CcDirector.Core/Sessions/SessionManager.cs`

```csharp
public Session CreateSession(string repoPath, string? claudeArgs, SessionBackendType backendType, ...)
{
    ISessionBackend backend = backendType switch
    {
        SessionBackendType.ConPty when RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            => new ConPtyBackend(_options.DefaultBufferSizeBytes),
        SessionBackendType.ConPty
            => new UnixPtyBackend(_options.DefaultBufferSizeBytes),
        SessionBackendType.Pipe
            => new PipeBackend(_options.DefaultBufferSizeBytes),
        SessionBackendType.Embedded when RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            => throw new InvalidOperationException("Use CreateEmbeddedSession"),
        SessionBackendType.Embedded
            => throw new PlatformNotSupportedException("Embedded mode is Windows-only"),
        _ => throw new ArgumentOutOfRangeException(nameof(backendType))
    };
    // ...
}
```

---

### Phase 5: Avalonia UI Project

**New Project Structure:**

```
src/
├── CcDirector.Core/           # Shared backend (already exists)
├── CcDirector.ViewModels/     # NEW: Extract ViewModels from WPF
├── CcDirector.Wpf/            # Windows UI (existing)
└── CcDirector.Avalonia/       # NEW: Mac/Linux/Windows UI
    ├── CcDirector.Avalonia.csproj
    ├── App.axaml
    ├── App.axaml.cs
    ├── MainWindow.axaml
    ├── MainWindow.axaml.cs
    └── Controls/
        └── TerminalControl.axaml
```

**Key Avalonia Differences:**

| WPF | Avalonia |
|-----|----------|
| `xmlns="http://schemas..."` | `xmlns="https://github.com/avaloniaui"` |
| `DrawingVisual` | `DrawingContext` (similar API) |
| `Dispatcher.BeginInvoke` | `Dispatcher.UIThread.Post` |
| `DependencyProperty` | `StyledProperty` or `DirectProperty` |

---

## File Changes Summary

### New Files (13 total)

| File | Phase |
|------|-------|
| `src/CcDirector.Core/UnixPty/UnixNativeMethods.cs` | 1 |
| `src/CcDirector.Core/UnixPty/UnixPseudoConsole.cs` | 1 |
| `src/CcDirector.Core/UnixPty/UnixProcessHost.cs` | 1 |
| `src/CcDirector.Core/Backends/UnixPtyBackend.cs` | 1 |
| `src/CcDirector.Core/Pipes/IDirectorServer.cs` | 2 |
| `src/CcDirector.Core/Pipes/UnixSocketServer.cs` | 2 |
| `hooks/hook-relay.py` | 3 |
| `src/CcDirector.ViewModels/CcDirector.ViewModels.csproj` | 5 |
| `src/CcDirector.ViewModels/SessionViewModel.cs` | 5 |
| `src/CcDirector.Avalonia/CcDirector.Avalonia.csproj` | 5 |
| `src/CcDirector.Avalonia/App.axaml` | 5 |
| `src/CcDirector.Avalonia/MainWindow.axaml` | 5 |
| `src/CcDirector.Avalonia/Controls/TerminalControl.axaml` | 5 |

### Modified Files (3 total)

| File | Changes |
|------|---------|
| `src/CcDirector.Core/Sessions/SessionManager.cs` | Platform detection |
| `src/CcDirector.Core/Pipes/DirectorPipeServer.cs` | Implement IDirectorServer |
| `src/CcDirector.Wpf/App.xaml.cs` | Use DirectorServerFactory |

---

## Testing Without a Mac

You can fully test Mac/Linux support without owning Apple hardware:

### Option 1: GitHub Actions CI (Recommended)

Add a macOS runner to your workflow. This is free for public repos and included in GitHub Actions minutes for private repos.

```yaml
# .github/workflows/build.yml
name: Build and Test

on: [push, pull_request]

jobs:
  build-windows:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet build
      - run: dotnet test

  build-macos:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet build src/CcDirector.Core/
      - run: dotnet test src/CcDirector.Core.Tests/
      # Integration test: spawn bash via UnixPtyBackend
      - name: PTY Integration Test
        run: dotnet test --filter "Category=UnixPty"

  build-linux:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet build src/CcDirector.Core/
      - run: dotnet test src/CcDirector.Core.Tests/
```

### Option 2: WSL2 for Linux Testing

Windows Subsystem for Linux lets you test Linux PTY code locally:

```powershell
# Install WSL2 with Ubuntu
wsl --install -d Ubuntu

# Inside WSL
cd /mnt/d/ReposFred/cc_director
dotnet build src/CcDirector.Core/
dotnet test src/CcDirector.Core.Tests/ --filter "Category=UnixPty"
```

**Important:** WSL2 uses the Linux kernel, so `openpty`, Unix sockets, and all POSIX APIs work identically to a real Linux machine. The only difference from macOS is the `TIOCSWINSZ` ioctl value.

### Option 3: Docker for Linux Testing

Run Linux tests in a container:

```dockerfile
# Dockerfile.test
FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /app
COPY . .
RUN dotnet test src/CcDirector.Core.Tests/ --filter "Category=UnixPty"
```

```powershell
docker build -f Dockerfile.test -t cc-director-test .
docker run --rm cc-director-test
```

### Option 4: macOS Cloud VMs

For full macOS testing including Avalonia UI:

| Service | Notes |
|---------|-------|
| **MacStadium** | Dedicated Mac minis, ~$99/month |
| **AWS EC2 Mac** | mac1.metal instances, pay-per-hour |
| **MacinCloud** | Hourly Mac VMs, good for occasional testing |
| **GitHub Actions macos-latest** | Free for CI, 10GB storage |

### Testing Strategy by Phase

| Phase | Can Test on Windows | Can Test on WSL2/Linux | Needs macOS |
|-------|---------------------|------------------------|-------------|
| Phase 1 (Unix PTY) | No | Yes (full test) | Only for TIOCSWINSZ value |
| Phase 2 (Unix Sockets) | No | Yes (full test) | No (identical to Linux) |
| Phase 3 (Hook relay) | No | Yes | No |
| Phase 4 (Platform detection) | Partial | Yes | No |
| Phase 5 (Avalonia UI) | Yes (cross-compile) | Yes (headless) | Yes (for visual QA) |

### Recommended Approach

1. **Develop on Windows** - Write the Unix code, use `#if` guards
2. **Test on WSL2** - Full integration tests for PTY and sockets
3. **CI on GitHub Actions** - Automated builds for macOS + Linux + Windows
4. **Final QA** - Use MacinCloud or GitHub Codespaces for Avalonia visual testing

### Conditional Compilation Example

```csharp
public static class PlatformHelper
{
    public static bool IsUnix =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static bool IsMacOS =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
}

// In UnixNativeMethods.cs
public static ulong GetTiocswinszValue()
{
    // This is the ONLY macOS-specific difference in PTY code
    return PlatformHelper.IsMacOS ? 0x80087467UL : 0x5414UL;
}
```

The vast majority of Unix code (95%+) is identical between Linux and macOS. WSL2 gives you a real Linux environment that will catch almost all issues before you need actual Mac hardware.

---

## Implementation Order

1. **Phase 1** - Unix PTY (develop on Windows, test on WSL2)
2. **Phase 2** - Unix Socket IPC (test on WSL2)
3. **Phase 3** - Hook relay script (test on WSL2)
4. **Phase 4** - SessionManager platform detection
5. **Phase 5** - Avalonia UI (largest effort, can be parallel)

---

## Verification Checklist

- [ ] Unix PTY builds on Linux/macOS
- [ ] PTY spawn `bash`, write command, read output
- [ ] PTY resize works (TIOCSWINSZ)
- [ ] Unix socket server accepts connections
- [ ] Hook relay script sends JSON to socket
- [ ] SessionManager creates correct backend per platform
- [ ] Avalonia app launches on macOS
- [ ] Full Claude Code integration test on macOS
