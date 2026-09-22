using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// Sends the errors a Director or a launcher logs to its Gateway (issue #3311), so an error on somebody's
/// machine is visible to the owner without asking them for screenshots or log files.
///
/// HOW ERRORS ARRIVE. <see cref="FileLog"/> hands every error line (see <see cref="ErrorLine"/>) to
/// <see cref="OnLogLine"/>. That covers both the errors the code logs and the unhandled exceptions, because
/// the start-up hooks (application domain, task scheduler, user-interface thread) already log those as
/// <c>UNHANDLED</c> / <c>UNOBSERVED</c> lines. No call site has to remember to report.
///
/// WHY IT CAN NEVER HURT THE DIRECTOR:
///   - <see cref="OnLogLine"/> runs on the logging thread and only adds to an in-memory table under a lock:
///     no input or output, no waiting. The table holds at most <see cref="MaxPending"/> distinct errors;
///     beyond that, new ones are counted and dropped.
///   - The same error repeated is ONE entry with a count, so an error loop costs one row, not thousands.
///     Digits are ignored when deciding "the same", so "retry 3" and "retry 4" collapse too.
///   - Sending happens on a background timer, in batches of at most
///     <see cref="ErrorReportLimits.MaxReportsPerBatch"/>, and at most <see cref="MaxSentPerHour"/> reports
///     an hour leave the process. The Gateway enforces its own per-device limit as well.
///   - A batch that fails is tried at most <see cref="MaxAttempts"/> times, then dropped and the drop logged
///     locally. Nothing waits on a send, and nothing a send does can throw into the caller.
///   - The reporter's own log lines start with <see cref="ErrorLine.ReporterTag"/>, which is never treated
///     as an error, so a failing send cannot report itself.
///
/// THE CREDENTIAL. The device's existing Gateway credential (<see cref="GatewayConfig.Token"/>) - the same one
/// the Director and launcher already use - so reports land in the device's own account. A machine with no
/// Gateway configured sends nothing.
/// </summary>
public sealed class ErrorReporter : IDisposable
{
    public const string Path = "/gateway/director-errors";

    internal const int MaxPending = 200;
    internal const int MaxSentPerHour = 60;
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan PauseAfterRefusal = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan PauseAfterMissingRoute = TimeSpan.FromHours(1);

    private static readonly Regex Digits = new(@"\d+", RegexOptions.CultureInvariant);

    private readonly string _component;
    private readonly Func<GatewayConfig> _config;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _clock;
    private readonly string _productVersion;
    private readonly string _os;
    private readonly string _osVersion;
    private readonly string _arch;
    private readonly string _machineId;

    private readonly object _lock = new();
    // Insertion-ordered, so the oldest error goes first.
    private readonly LinkedList<Pending> _order = new();
    private readonly Dictionary<string, LinkedListNode<Pending>> _bySignature = new(StringComparer.Ordinal);
    private readonly Queue<(DateTime At, int Count)> _sentWindow = new();
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private long _dropped;
    private long _sent;
    private bool _loggedNoGateway;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    [ThreadStatic] private static bool _inObserver;

    private sealed class Pending
    {
        public required string Signature;
        public required string Source;
        public required string Kind;
        public required string Message;
        public required string ExceptionType;
        public required string Stack;
        public required DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
        public int Count = 1;
        public int Attempts;
    }

    /// <summary>The running reporter for this process, once <see cref="Start"/> has run.</summary>
    public static ErrorReporter? Current { get; private set; }

    /// <summary>
    /// Start reporting this process's errors to its Gateway, as <paramref name="component"/>
    /// (<see cref="ErrorReportLimits.Director"/> or <see cref="ErrorReportLimits.Launcher"/>). Call once, after
    /// <see cref="FileLog.Start"/>. A second call is ignored.
    /// </summary>
    public static void Start(string component)
    {
        if (Current is not null) return;
        var reporter = new ErrorReporter(component);
        Current = reporter;
        FileLog.ErrorObserver = reporter.OnLogLine;
        reporter.StartLoop();
        FileLog.Write($"{ErrorLine.ReporterTag} started: component={component}, version={reporter._productVersion}, os={reporter._os}, arch={reporter._arch}");
    }

    /// <summary>
    /// Try to send what is pending before the process dies - from a terminating unhandled-exception hook.
    /// Bounded by <paramref name="budget"/>; never throws.
    /// </summary>
    public static void FlushBeforeExit(TimeSpan budget)
    {
        var reporter = Current;
        if (reporter is null) return;
        try
        {
            using var cts = new CancellationTokenSource(budget);
            reporter.SendPendingAsync(cts.Token).Wait(budget);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{ErrorLine.ReporterTag} flush before exit did not finish: {ex.Message}");
        }
    }

    public ErrorReporter(string component, Func<GatewayConfig>? config = null, HttpClient? http = null,
        Func<DateTime>? clock = null, string? machineName = null, string? productVersion = null)
    {
        if (!ErrorReportLimits.DeviceComponents.Contains(component))
            throw new ArgumentException($"component must be one of: {string.Join(", ", ErrorReportLimits.DeviceComponents)}", nameof(component));
        _component = component;
        _config = config ?? GatewayConfig.Load;
        _http = http ?? new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(10) };
        _clock = clock ?? (() => DateTime.UtcNow);
        _productVersion = productVersion ?? ProductVersionOf(Assembly.GetEntryAssembly() ?? typeof(ErrorReporter).Assembly);
        _os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";
        _osVersion = ErrorTextScrubber.Clean(RuntimeInformation.OSDescription, ErrorReportLimits.MaxShortField);
        _arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        _machineId = ErrorReportMachineId.Of(machineName ?? Environment.MachineName);
    }

    /// <summary>Errors dropped because the table was full or a batch ran out of attempts.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Reports the Gateway accepted.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    internal int PendingCount { get { lock (_lock) return _order.Count; } }

    /// <summary>
    /// The FileLog observer. Called on whatever thread logged the line. Adds or counts; never blocks on
    /// anything but the table lock, never throws.
    /// </summary>
    public void OnLogLine(string message)
    {
        if (_inObserver) return;
        _inObserver = true;
        try
        {
            if (!ErrorLine.IsError(message)) return;
            var (source, text, exceptionType, stack) = ErrorLine.Parse(message);
            Add(source, ErrorLine.KindOf(message), text, exceptionType, stack);
        }
        catch (Exception ex)
        {
            // An observer that throws would throw into every caller of FileLog.Write. Recorded where a
            // developer sees it, and not through FileLog, which is what called us.
            System.Diagnostics.Debug.WriteLine($"{ErrorLine.ReporterTag} could not queue a line: {ex.Message}");
        }
        finally
        {
            _inObserver = false;
        }
    }

    internal void Add(string source, string kind, string message, string exceptionType, string stack)
    {
        var cleanSource = ErrorTextScrubber.Clean(source, ErrorReportLimits.MaxShortField);
        var cleanMessage = ErrorTextScrubber.Clean(message, ErrorReportLimits.MaxMessage);
        var cleanType = ErrorTextScrubber.Clean(exceptionType, ErrorReportLimits.MaxShortField);
        var signature = string.Join('|', cleanSource, kind, cleanType, Digits.Replace(cleanMessage, "#"));
        var now = _clock();

        lock (_lock)
        {
            if (_bySignature.TryGetValue(signature, out var node))
            {
                node.Value.Count++;
                node.Value.LastSeenUtc = now;
                return;
            }
            if (_order.Count >= MaxPending)
            {
                _dropped++;
                return;
            }
            var pending = new Pending
            {
                Signature = signature,
                Source = cleanSource,
                Kind = kind,
                Message = cleanMessage,
                ExceptionType = cleanType,
                Stack = ErrorTextScrubber.Clean(stack, ErrorReportLimits.MaxStack),
                FirstSeenUtc = now,
                LastSeenUtc = now,
            };
            _bySignature[signature] = _order.AddLast(pending);
        }
    }

    private void StartLoop()
    {
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(SendInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                    await SendPendingAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                FileLog.Write($"{ErrorLine.ReporterTag} send loop stopped ({ex.GetType().Name}): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Send one batch of what is pending, if the budget and the Gateway allow. Returns how many reports the
    /// Gateway accepted. Never throws except for cancellation.
    /// </summary>
    internal async Task<int> SendPendingAsync(CancellationToken ct)
    {
        if (!await _sendGate.WaitAsync(0, ct).ConfigureAwait(false)) return 0;
        try
        {
            var now = _clock();
            List<Pending> batch;
            lock (_lock)
            {
                if (_order.Count == 0 || now < _pausedUntilUtc) return 0;
                var budget = MaxSentPerHour - SentInLastHour(now);
                if (budget <= 0) return 0;
                batch = _order.Take(Math.Min(budget, ErrorReportLimits.MaxReportsPerBatch)).ToList();
                foreach (var p in batch)
                {
                    _order.Remove(_bySignature[p.Signature]);
                    _bySignature.Remove(p.Signature);
                }
            }

            var config = _config();
            if (!config.IsEnabled || string.IsNullOrWhiteSpace(config.Token))
            {
                if (!_loggedNoGateway)
                {
                    _loggedNoGateway = true;
                    FileLog.Write($"{ErrorLine.ReporterTag} no Gateway credential on this machine; errors stay in the local log only");
                }
                Drop(batch.Count);
                return 0;
            }

            var body = new ErrorReportBatch(batch.Select(ToItem).ToList());
            var url = config.Url.TrimEnd('/') + Path;
            HttpStatusCode status;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                status = response.StatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Requeue(batch);
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Write($"{ErrorLine.ReporterTag} {batch.Count} report(s) not delivered ({ex.GetType().Name}): {ex.Message}");
                Requeue(batch);
                return 0;
            }

            var code = (int)status;
            if (code is >= 200 and < 300)
            {
                lock (_lock) _sentWindow.Enqueue((now, batch.Count));
                Interlocked.Add(ref _sent, batch.Count);
                return batch.Count;
            }

            if (status == HttpStatusCode.NotFound)
            {
                // A Gateway older than this route. Not worth retrying every twenty seconds.
                Pause(PauseAfterMissingRoute);
                FileLog.Write($"{ErrorLine.ReporterTag} the Gateway has no {Path} route (HTTP 404); {batch.Count} report(s) dropped, next try in {PauseAfterMissingRoute.TotalMinutes:0} minutes");
                Drop(batch.Count);
            }
            else if (code is 401 or 403 or 413 or 429 || (code >= 400 && code < 500))
            {
                Pause(PauseAfterRefusal);
                FileLog.Write($"{ErrorLine.ReporterTag} the Gateway refused {batch.Count} report(s) (HTTP {code}); dropped, next try in {PauseAfterRefusal.TotalMinutes:0} minutes");
                Drop(batch.Count);
            }
            else
            {
                FileLog.Write($"{ErrorLine.ReporterTag} {batch.Count} report(s) not delivered (HTTP {code})");
                Requeue(batch);
            }
            return 0;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private ErrorReportItem ToItem(Pending p) => new(
        Component: _component,
        Source: p.Source,
        Kind: p.Kind,
        Message: p.Message,
        ExceptionType: p.ExceptionType,
        Stack: p.Stack,
        RepeatCount: p.Count,
        FirstSeenUtc: p.FirstSeenUtc,
        LastSeenUtc: p.LastSeenUtc,
        ProductVersion: _productVersion,
        Os: _os,
        OsVersion: _osVersion,
        Arch: _arch,
        MachineId: _machineId);

    /// <summary>Put a failed batch back at the front, except what has used up its attempts.</summary>
    private void Requeue(List<Pending> batch)
    {
        var givenUp = 0;
        lock (_lock)
        {
            for (var i = batch.Count - 1; i >= 0; i--)
            {
                var p = batch[i];
                p.Attempts++;
                if (_bySignature.TryGetValue(p.Signature, out var again))
                {
                    // The same error happened again while this batch was in flight: fold the two together.
                    again.Value.Count += p.Count;
                    if (p.FirstSeenUtc < again.Value.FirstSeenUtc) again.Value.FirstSeenUtc = p.FirstSeenUtc;
                    continue;
                }
                if (p.Attempts >= MaxAttempts || _order.Count >= MaxPending)
                {
                    givenUp++;
                    continue;
                }
                _bySignature[p.Signature] = _order.AddFirst(p);
            }
        }
        if (givenUp > 0)
        {
            Drop(givenUp);
            FileLog.Write($"{ErrorLine.ReporterTag} gave up on {givenUp} report(s) after {MaxAttempts} attempts");
        }
    }

    private void Drop(int count) => Interlocked.Add(ref _dropped, count);

    private void Pause(TimeSpan span)
    {
        lock (_lock) _pausedUntilUtc = _clock() + span;
    }

    private int SentInLastHour(DateTime now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        while (_sentWindow.Count > 0 && _sentWindow.Peek().At < cutoff) _sentWindow.Dequeue();
        return _sentWindow.Sum(e => e.Count);
    }

    private static string ProductVersionOf(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? assembly.GetName().Version?.ToString()
           ?? "unknown";

    public void Dispose()
    {
        if (ReferenceEquals(Current, this))
        {
            FileLog.ErrorObserver = null;
            Current = null;
        }
        _stop.Cancel();
    }
}
