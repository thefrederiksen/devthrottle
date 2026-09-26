using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Machine;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tests;
using CcDirector.Core.UnitTests.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>These tests change this process's priority and pin it to one processor, so nothing else in the assembly may
/// run beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StarvedProcessCollection
{
    public const string Name = "Starved process";
}

/// <summary>
/// The verbs answer in time on a busy machine (Voice Delivery mission, phase 6).
///
/// WHAT WAS MEASURED. In the phase 4 QA run (case2f) the prompt verb answered 32 seconds after receipt - its budget is
/// 20 - and the delivery-state verb, a one-file read, 61 seconds after; the Gateway waits 30, so both answers were lost.
/// The Director under test had been started by a Windows scheduled task and was running at Below Normal priority
/// (read from the live process: cc-director6.exe, the same process number as that log file), while 24 threads at
/// Normal priority kept all 24 processors busy. Windows runs a ready Below Normal thread only when no Normal thread
/// wants the processor, or when its starvation boost comes round - about once every four seconds. The same verb code,
/// in the same load, answered at 20.0 seconds at Normal priority and at 33.8 seconds at Below Normal; its one-second
/// timer ran up to 20.3 seconds late. It was not the thread pool (its floor is 104 workers and one session held two),
/// not a lock (nothing else held the record's), and not the file.
///
/// THE REPRODUCTION. This process is set to Below Normal, as the scheduled task set the Director, and pinned to one
/// processor that a Normal-priority process keeps busy - the case2f machine in miniature, without loading the whole
/// machine. Then the Director's own start-up step runs (<see cref="ProcessPriorityFloor.Apply()"/>), and the verbs must
/// answer in time.
/// </summary>
[Collection(StarvedProcessCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class VerbAnswersWhenStarvedTests
{
    private const string WindowsOnly = "it reproduces the Windows scheduler's treatment of a Below Normal process beside a busy Normal one";

    [WindowsOnlyFact(WindowsOnly)]
    public async Task PromptVerb_DirectorStartedBelowNormalOnABusyMachine_AnswersInsideItsBudget()
    {
        using var busy = BusyProcessor.StarveThisProcess();
        ProcessPriorityFloor.Apply();   // what the Director does first thing at start (Program.Main)

        // Arrange: a send that runs far past the budget, as the case2f send to a busy agent did.
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = AgentKind.Gemini;
        terminal.Echo = false;
        terminal.OnWrite = text =>
        {
            if (text == "\r") return;
            var bytes = Encoding.UTF8.GetBytes(text);
            _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => terminal.Buffer!.Write(bytes), TaskScheduler.Default);
        };
        var budget = TimeSpan.FromSeconds(3);
        var deliveryId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        // Act
        var result = await ControlApi.SessionCommandExecutor.SendPromptAsync(session,
            new PromptRequest { Text = "Token STARVED1. Reply: ACK", AppendEnter = true, Surface = "cockpit", DeliveryUploadId = deliveryId, DeliveryId = deliveryId },
            SendSource.Delivery, busy.Record, budget);
        var answeredAfter = sw.Elapsed;

        // Assert: answered "delivering" at the budget - not seconds after it.
        Assert.True(result.Ok, result.Error);
        Assert.Contains("\"deliveryState\":\"delivering\"", result.BodyJson);
        Assert.True(answeredAfter < budget + TimeSpan.FromSeconds(1.5),
            $"the prompt verb answered {answeredAfter.TotalSeconds:F1}s after it was asked; its budget was {budget.TotalSeconds:F0}s");
    }

    [WindowsOnlyFact(WindowsOnly)]
    public async Task DeliveryStateVerb_DirectorStartedBelowNormalOnABusyMachine_AnswersInAboutAFileRead()
    {
        using var busy = BusyProcessor.StarveThisProcess();
        ProcessPriorityFloor.Apply();

        // Arrange: a record with history, and a send running on the same session, as in case2f.
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = AgentKind.Gemini;
        terminal.Echo = false;
        for (var i = 0; i < 10; i++) busy.Record.MarkDelivered(session.Id, Guid.NewGuid().ToString("N"));
        var deliveryId = Guid.NewGuid().ToString("N");
        _ = ControlApi.SessionCommandExecutor.SendPromptAsync(session,
            new PromptRequest { Text = "Token STARVED2. Reply: ACK", AppendEnter = true, Surface = "cockpit", DeliveryUploadId = deliveryId, DeliveryId = deliveryId },
            SendSource.Delivery, busy.Record, TimeSpan.FromSeconds(20));
        var command = new DirectorCommand
        {
            CommandId = "starved", Verb = DeliveryStateRequest.Verb, SessionId = session.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new DeliveryStateRequest { DeliveryId = deliveryId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };

        // Act: ask ten times, a moment apart, each from the pool as the Gateway's command handler does. The pause matters:
        // the Gateway's commands arrive spaced out, so each one wakes a pool thread that has gone idle - and waking is
        // exactly what a starved process waits for. Asked back to back, a thread still spinning from the last read can
        // take the next and hide the wait. Stops at the first slow answer, so a starved run does not take minutes.
        var took = new List<TimeSpan>();
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(300);
            var sw = Stopwatch.StartNew();
            var answer = await Task.Run(() => ControlApi.SessionReadExecutor.DeliveryStateOf(command, busy.Record));
            took.Add(sw.Elapsed);
            Assert.True(answer.Ok, answer.Error);
            if (sw.Elapsed >= TimeSpan.FromSeconds(1)) break;
        }

        // Assert
        Assert.True(took.Max() < TimeSpan.FromSeconds(1),
            "a delivery-state answer is a read of one small file; these took " +
            string.Join(", ", took.Select(t => $"{t.TotalSeconds:F2}s")));
    }

    /// <summary>
    /// This process at Below Normal, pinned to one processor that a Normal-priority process keeps busy. Put back as it
    /// was on dispose, and the busy process - this test's own child - is stopped.
    /// </summary>
    private sealed class BusyProcessor : IDisposable
    {
        private readonly Process _me = Process.GetCurrentProcess();
        private readonly ProcessPriorityClass _priority;
        private readonly IntPtr _affinity;
        private readonly Process _load;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cc-starved-" + Guid.NewGuid().ToString("N"));

        public DeliveryRecord Record { get; }

        private BusyProcessor()
        {
            Record = new DeliveryRecord(_directory);
            _priority = _me.PriorityClass;
            _affinity = _me.ProcessorAffinity;
            _me.ProcessorAffinity = (IntPtr)1;
            _load = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"$end=(Get-Date).AddSeconds(90); while((Get-Date) -lt $end){}\"")
                { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new InvalidOperationException("the busy process did not start");
            _load.ProcessorAffinity = (IntPtr)1;
            _load.PriorityClass = ProcessPriorityClass.Normal;

            // It must be burning the processor before this process is lowered beneath it.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                _load.Refresh();
                var before = _load.TotalProcessorTime;
                Thread.Sleep(500);
                _load.Refresh();
                if (_load.TotalProcessorTime - before > TimeSpan.FromMilliseconds(300)) break;
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException("the busy process never kept the processor busy");
            }
            _me.PriorityClass = ProcessPriorityClass.BelowNormal;   // what a default Windows scheduled task gives it
        }

        public static BusyProcessor StarveThisProcess() => new();

        public void Dispose()
        {
            _me.PriorityClass = _priority;
            _me.ProcessorAffinity = _affinity;
            if (!_load.HasExited) _load.Kill();
            _load.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }
}
