using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// A FAILED SEND, THEN THE NEXT SEND, ON THE SAME REAL SESSION (Voice Delivery mission, phase 6). On 25 September 2026
/// (case 2f) one send to a loaded Claude Code was typed and never echoed, so the Director gave it up and marked the
/// composer as possibly holding its text; from then on EVERY send to that session was refused, while its composer was
/// empty on screen - the Director was reading a stale row of a fixed 220 by 40 grid (issue #3406).
///
/// This makes the same failure happen on a real Claude Code, on purpose: the agent's processes are frozen, so the
/// typed text cannot be drawn, exactly as an agent starved of processor time cannot draw it. The send gives up with
/// the text typed once. The agent is thawed - it then reads the typed characters into its composer, as case 2f's agent
/// could have - and the next send is made to the same session. Both are counted in the agent's OWN conversation file:
/// the failed text must be there zero times and the next one exactly once. Then one more send, to show the session is
/// not wedged.
/// </summary>
public static class FailThenSendExperiment
{
    public static async Task<int> RunAsync(SessionManager manager, string args, string repo, string outDir, TimeSpan thawWait)
    {
        var provenance = SubmissionProvenance.Typed("delivery-qa-rig", "rig");
        var session = manager.CreateSession(repo, AgentKind.ClaudeCode, args, SessionBackendType.ConPty, null);
        var lines = new List<string>();
        void Report(string line) { Console.WriteLine(line); lock (lines) lines.Add(line); }
        void Screen(string label) =>
            Report($"[fail-then-send]   screen {label} ({session.CurrentCols}x{session.CurrentRows}):\n" +
                   string.Join("\n", session.SnapshotScreenRows().Select((r, i) => (r, i)).Where(x => x.r.Length > 0).Select(x => $"      {x.i,2}|{x.r}")));
        var problems = 0;
        try
        {
            var ready = await FirstPromptGate.WaitUntilAcceptingInputAsync(
                AgentKind.ClaudeCode, () => Frame(session), b => session.SendInput(b, null, provenance),
                () => session.ActivityState == ActivityState.Exited, TimeSpan.FromMinutes(3));
            Report($"[fail-then-send] Claude Code ready: {ready.Outcome}, pid={session.ProcessId}");
            if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(2)))
                Report("[fail-then-send] not idle after 2 minutes; going on");

            // 1. THE FAILED SEND: the agent frozen, so nothing it is sent can be drawn.
            var failedToken = Token();
            var failedText = $"Reply with only the word OK. Marker {failedToken}";
            var since = DateTime.UtcNow;
            var frozen = ProcessTree.Of(session.ProcessId);
            Report($"[fail-then-send] freezing the agent's {frozen.Count} process(es): {string.Join(",", frozen)}");
            ProcessTree.Suspend(frozen);
            string firstOutcome;
            var sw = Stopwatch.StartNew();
            try
            {
                await session.SendTextAsync(failedText, provenance);
                firstOutcome = "OK";
            }
            catch (Exception ex)
            {
                firstOutcome = $"FAILED ({ex.GetType().Name}): {ex.Message}";
            }
            finally
            {
                ProcessTree.Resume(frozen);
            }
            Report($"[fail-then-send] send 1 (agent frozen) after {sw.Elapsed.TotalSeconds:F1}s: {firstOutcome}");
            if (firstOutcome == "OK") { Report("[fail-then-send] send 1 was NOT refused - the failure was not forced; the run proves nothing"); problems++; }

            // The thawed agent reads the characters that were typed while it was frozen - or has not yet, when the next
            // send is made at once (--thaw-wait 0): then they are still unread in the terminal's input when it looks.
            await Task.Delay(thawWait);
            Screen("after the thaw");

            // 2. THE NEXT SEND, and 3. one more.
            var results = new List<(string Label, string Token, string Outcome, double Seconds)>();
            foreach (var label in new[] { "send 2 (after the failure)", "send 3" })
            {
                var token = Token();
                var text = $"Reply with only the word OK. Marker {token}";
                sw.Restart();
                string outcome;
                try
                {
                    await session.SendTextAsync(text, provenance);
                    outcome = "OK";
                }
                catch (Exception ex)
                {
                    outcome = $"FAILED ({ex.GetType().Name}): {ex.Message}";
                }
                Report($"[fail-then-send] {label} after {sw.Elapsed.TotalSeconds:F1}s: {outcome}");
                results.Add((label, token, outcome, sw.Elapsed.TotalSeconds));
                if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(2)))
                    Report("[fail-then-send] not idle 2 minutes after the send; counting anyway");
            }
            Screen("at the end");

            // The truth, from the agent's own conversation file.
            var failedCopies = BusyExperiment.CopiesInClaudeRecords(repo, since, failedToken);
            var failedOk = firstOutcome != "OK" && failedCopies == 0;
            if (!failedOk) problems++;
            Report($"[fail-then-send] RESULT send 1: director={(firstOutcome == "OK" ? "OK" : "FAILED")} copies-in-records={failedCopies} {(failedOk ? "PASS" : "FAIL")}");
            foreach (var (label, token, outcome, seconds) in results)
            {
                var copies = BusyExperiment.CopiesInClaudeRecords(repo, since, token);
                var ok = outcome == "OK" && copies == 1;
                if (!ok) problems++;
                Report($"[fail-then-send] RESULT {label}: director={(outcome == "OK" ? "OK" : "FAILED")} send={seconds:F1}s copies-in-records={copies} {(ok ? "PASS" : "FAIL")}");
            }
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[fail-then-send] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
            lock (lines) File.WriteAllLines(Path.Combine(outDir, "fail-then-send-summary.txt"), lines);
        }
        Report(problems == 0
            ? "[fail-then-send] THE FAILED SEND IS NOT IN THE RECORDS, AND EVERY LATER SEND TO THE SAME SESSION ARRIVED EXACTLY ONCE."
            : $"[fail-then-send] {problems} problem(s)");
        return problems == 0 ? 0 : 1;
    }

    private static string Token() => $"Q{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";

    private static ScreenFrame Frame(Session s)
    {
        var (rows, row, col, visible, _) = s.SnapshotLiveScreen();
        return new ScreenFrame(rows, row, col, visible);
    }

    /// <summary>Freeze and thaw a process and every process under it (Windows only; the rig drives Windows agents).</summary>
    private static class ProcessTree
    {
        public static List<int> Of(int root)
        {
            if (root <= 0) throw new InvalidOperationException("The session has no process id to freeze.");
            var parents = Snapshot();
            var tree = new List<int> { root };
            for (var i = 0; i < tree.Count; i++)
                tree.AddRange(parents.Where(p => p.Parent == tree[i] && !tree.Contains(p.Pid)).Select(p => p.Pid));
            return tree;
        }

        public static void Suspend(IEnumerable<int> pids) { foreach (var pid in pids) Act(pid, NtSuspendProcess, "suspend"); }

        public static void Resume(IEnumerable<int> pids) { foreach (var pid in pids) Act(pid, NtResumeProcess, "resume"); }

        private static void Act(int pid, Func<IntPtr, int> action, string what)
        {
            var handle = OpenProcess(ProcessSuspendResume, false, pid);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open process {pid} to {what} it.");
            try
            {
                var status = action(handle);
                if (status != 0) throw new InvalidOperationException($"Could not {what} process {pid}: NTSTATUS 0x{status:X8}.");
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static List<(int Pid, int Parent)> Snapshot()
        {
            var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not list processes.");
            try
            {
                var list = new List<(int, int)>();
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                for (var more = Process32First(snap, ref entry); more; more = Process32Next(snap, ref entry))
                    list.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
                return list;
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        private const uint ProcessSuspendResume = 0x0800;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
    }
}
