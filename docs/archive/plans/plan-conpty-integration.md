# Plan: Integrate ConPTY as Alternative Session Backend

**Issue:** #16 - Integrate ConPTY method as an alternative session backend

## Current State Analysis

### Main Application (cc_director)
The main app already has **three session modes**:
1. **ConPty Mode** - Uses PseudoConsole, renders via TerminalControl
2. **PipeMode** - Spawns `claude -p`, communicates via stdin/stdout
3. **Embedded Mode** - Real console window overlaid on WPF, many workarounds

### Test Application (ConPtyTest)
- Clean ConPTY implementation with multi-session support
- `ClaudeSession` class encapsulating all session state
- Simpler scrolling logic
- No Z-order hacks or console window embedding complexity

### Key Insight
The main app's ConPty mode exists but shares code paths with embedded mode workarounds. The test app proved ConPTY works cleanly without those hacks.

---

## Implementation Plan

### Phase 1: Audit and Document Current State
**Goal:** Understand exactly what code is shared vs mode-specific

**Tasks:**
- [ ] Map which code in `Session.cs` is ConPty-specific vs Embedded-specific
- [ ] Identify all EmbeddedConsoleHost workarounds that DON'T apply to pure ConPty
- [ ] Document the differences between main app's ConPty mode and test app's implementation
- [ ] Create a matrix: Feature x Mode (ConPty/Embedded/Pipe)

**Files to analyze:**
- `src/CcDirector/Models/Session.cs`
- `src/CcDirector/Services/SessionManager.cs`
- `src/CcDirector/ConPty/ProcessHost.cs`
- `src/CcDirector/ConPty/PseudoConsole.cs`
- `src/CcDirector/Interop/EmbeddedConsoleHost.cs`
- `src/CcDirector/Controls/TerminalControl.cs`

---

### Phase 2: Extract Session Backend Interface
**Goal:** Create clean abstraction for different session backends

**Tasks:**
- [ ] Define `ISessionBackend` interface:
  ```csharp
  public interface ISessionBackend : IDisposable
  {
      int ProcessId { get; }
      string Status { get; }
      bool IsRunning { get; }

      event Action<string> StatusChanged;
      event Action<int> ProcessExited;
      event Action<byte[]> OutputReceived;

      void Start(string executable, string args, string workingDir, short cols, short rows);
      void Write(byte[] data);
      void Resize(short cols, short rows);
      Task GracefulShutdownAsync(int timeoutMs);
  }
  ```

- [ ] Create `ConPtyBackend` implementing `ISessionBackend`
  - Port clean implementation from test app's `ClaudeSession`
  - Owns: PseudoConsole, ProcessHost, CircularTerminalBuffer
  - No EmbeddedConsoleHost dependencies

- [ ] Create `EmbeddedBackend` implementing `ISessionBackend`
  - Wraps existing EmbeddedConsoleHost logic
  - Keeps all the TOPMOST flash, Z-order, border stripping workarounds
  - This is the "legacy" mode that works but has quirks

- [ ] Create `PipeBackend` implementing `ISessionBackend`
  - Port existing pipe mode logic
  - Stateless per-prompt spawning

---

### Phase 3: Refactor Session Class
**Goal:** Session delegates to backend, doesn't know implementation details

**Tasks:**
- [ ] Change `Session` to hold `ISessionBackend` instead of direct ConPty/Embedded references
- [ ] Move mode-specific initialization into backend factories
- [ ] Update `SessionManager` creation methods:
  - `CreateSession(backend: SessionBackendType)`
  - Enum: `ConPty`, `Embedded`, `Pipe`

- [ ] Ensure all existing functionality works through the interface:
  - `SendTextAsync()` -> `_backend.Write()`
  - Process exit handling
  - Activity state tracking (via hook events, unchanged)
  - Buffer access for UI

---

### Phase 4: Verify Embedded Mode Still Works
**Goal:** No regressions in current embedded mode

**Tasks:**
- [ ] Create automated test checklist:
  - [ ] Start embedded session
  - [ ] Console window appears embedded correctly
  - [ ] Text input works (both WriteConsoleInput and clipboard fallback)
  - [ ] Z-order stays correct when clicking around
  - [ ] Session persistence/restore works
  - [ ] Graceful shutdown works

- [ ] Run through checklist manually
- [ ] Fix any regressions introduced by refactoring

---

### Phase 5: Integrate Test App's ConPty Improvements
**Goal:** Bring learnings from test app into main app's ConPty backend

**Tasks:**
- [ ] Port improved scrolling logic from test app to main TerminalControl
- [ ] Port `ClaudeSession` patterns into `ConPtyBackend`
- [ ] Ensure multi-session works (already does, but verify with new backend)
- [ ] Add `--dangerously-skip-permissions` as configurable option

**Test checklist for ConPty mode:**
- [ ] Start ConPty session
- [ ] Terminal renders correctly (colors, cursor, ANSI)
- [ ] Scrolling works smoothly (scroll up into history, auto-scroll on new output)
- [ ] Input via TerminalControl keyboard works
- [ ] Input via bottom TextBox works
- [ ] Resize works (terminal dimensions update)
- [ ] Multiple sessions can run simultaneously
- [ ] Switching between sessions works
- [ ] Graceful shutdown works

---

### Phase 6: Add Backend Selection UI
**Goal:** User can choose preferred backend

**Tasks:**
- [ ] Add setting: `PreferredSessionBackend` (enum: ConPty, Embedded, Pipe)
- [ ] Store in app settings (registry or JSON config)
- [ ] Add UI in Settings dialog:
  ```
  Session Backend:
  ( ) ConPTY Terminal (Recommended - native terminal emulation)
  ( ) Embedded Console (Legacy - real console window overlay)
  ( ) Pipe Mode (Lightweight - no terminal, just text I/O)
  ```

- [ ] Add per-session override option in New Session dialog (optional)
- [ ] Show current backend type in session header/tooltip

---

### Phase 7: Documentation and Cleanup
**Goal:** Clean codebase, documented architecture

**Tasks:**
- [ ] Update README with backend options explanation
- [ ] Add XML doc comments to ISessionBackend and implementations
- [ ] Remove dead code paths (if any)
- [ ] Update issue #16 with completion notes
- [ ] Consider: Should Embedded mode be deprecated eventually?

---

## File Changes Summary

### New Files
```
src/CcDirector/Backends/ISessionBackend.cs
src/CcDirector/Backends/ConPtyBackend.cs
src/CcDirector/Backends/EmbeddedBackend.cs
src/CcDirector/Backends/PipeBackend.cs
src/CcDirector/Backends/SessionBackendType.cs
```

### Modified Files
```
src/CcDirector/Models/Session.cs           - Delegate to ISessionBackend
src/CcDirector/Services/SessionManager.cs  - Backend factory methods
src/CcDirector/Controls/TerminalControl.cs - Improved scrolling
src/CcDirector/Views/SettingsWindow.xaml   - Backend selection UI
src/CcDirector/Views/NewSessionDialog.xaml - Optional backend override
```

### Potentially Deprecated (Phase 7 decision)
```
src/CcDirector/Interop/EmbeddedConsoleHost.cs - Keep but wrap in EmbeddedBackend
```

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Embedded mode breaks during refactor | Phase 4 explicit verification before proceeding |
| ConPty mode has edge cases not in test app | Extensive testing in Phase 5 |
| Users confused by backend options | Clear UI labels and defaults |
| Performance regression | Profile before/after, especially buffer polling |

---

## Success Criteria

1. All three backends work through unified interface
2. Embedded mode has zero regressions
3. ConPty mode works as well as test app
4. User can switch backends via settings
5. Architecture supports adding new backends (e.g., SSH, WSL)

---

## Estimated Phases

| Phase | Description |
|-------|-------------|
| 1 | Audit and Document |
| 2 | Extract Interface |
| 3 | Refactor Session |
| 4 | Verify Embedded |
| 5 | Integrate ConPty Improvements |
| 6 | Backend Selection UI |
| 7 | Documentation and Cleanup |

---

## Notes

- The test app (`tests/ConPtyTest/`) should be kept as a standalone sandbox for future experiments
- Consider: Hook events work the same regardless of backend (named pipe communication is separate from terminal I/O)
- The `--dangerously-skip-permissions` flag should be a user setting, not hardcoded
