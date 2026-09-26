using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Storage;
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
///     beyond that, new ones are counted and dropped, and one line says so for each time it fills up.
///     A line longer than <see cref="MaxLineChars"/> is cut before it is parsed or scrubbed.
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
/// the Director and launcher already use - so reports land in the device's own account.
///
/// BEFORE SIGN-IN there is no credential, and that is when a fresh install fails. Errors then go to a
/// <see cref="PreSignInOutbox"/>: kept on disk first, sent to the hosted Gateway's public install-report route
/// within that route's per-machine limit, and handed to the device route once the machine signs in. They are
/// never dropped for want of a credential.
/// </summary>
public sealed class ErrorReporter : IDisposable
{
    public const string Path = "/gateway/director-errors";

    internal const int MaxPending = 200;
    internal const int MaxSentPerHour = 60;
    internal const int MaxAttempts = 3;
    internal const int MaxLineChars = 4 * ErrorReportLimits.MaxStack;
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
    private readonly PreSignInOutbox? _preSignIn;

    private readonly object _lock = new();
    // Insertion-ordered, so the oldest error goes first.
    private readonly LinkedList<Pending> _order = new();
    private readonly Dictionary<string, LinkedListNode<Pending>> _bySignature = new(StringComparer.Ordinal);
    private readonly Queue<(DateTime At, int Count)> _sentWindow = new();
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private long _dropped;
    private long _sent;
    private bool _loggedNoGateway;
    private bool _tableFullLogged;
    private readonly bool _ownsHttp;

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
        var reporter = new ErrorReporter(component, preSignIn: DefaultOutbox);
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
            reporter.FlushAsync(cts.Token).Wait(budget);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{ErrorLine.ReporterTag} flush before exit did not finish: {ex.Message}");
        }
    }

    public ErrorReporter(string component, Func<GatewayConfig>? config = null, HttpClient? http = null,
        Func<DateTime>? clock = null, string? machineName = null, string? productVersion = null,
        Func<ErrorReporter, PreSignInOutbox>? preSignIn = null)
    {
        if (!ErrorReportLimits.DeviceComponents.Contains(component))
            throw new ArgumentException($"component must be one of: {string.Join(", ", ErrorReportLimits.DeviceComponents)}", nameof(component));
        _component = component;
        _config = config ?? GatewayConfig.Load;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(10) };
        _clock = clock ?? (() => DateTime.UtcNow);
        _productVersion = productVersion ?? ProductVersionOf(Assembly.GetEntryAssembly() ?? typeof(ErrorReporter).Assembly);
        _os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";
        _osVersion = ErrorTextScrubber.Clean(RuntimeInformation.OSDescription, ErrorReportLimits.MaxShortField);
        _arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        _machineId = ErrorReportMachineId.Of(machineName ?? Environment.MachineName);
        _preSignIn = preSignIn?.Invoke(this);
    }

    /// <summary>The outbox <see cref="Start"/> gives a real process: a file under this storage root's logs,
    /// one per component, sending to the hosted Gateway with the machine's install id.</summary>
    internal static PreSignInOutbox DefaultOutbox(ErrorReporter reporter) => new(
        System.IO.Path.Combine(CcStorage.Logs(), "error-outbox", $"{reporter._component}-before-sign-in.json"),
        reporter._component,
        () => InstallId.ReadOrCreate(CcStorage.MachineRoot()),
        HostedGateway.ResolveUrl,
        reporter._http,
        reporter._clock);

    /// <summary>The outbox, when this reporter has one.</summary>
    internal PreSignInOutbox? Outbox => _preSignIn;

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
            // Cap BEFORE parsing and scrubbing: an unhandled-exception dump can run to many kilobytes, and
            // everything past this is cut by the field caps anyway. Keeps the work on the logging thread small.
            if (message.Length > MaxLineChars) message = message[..MaxLineChars];
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
        var cleanStack = ErrorTextScrubber.Clean(stack, ErrorReportLimits.MaxStack);
        var signature = string.Join('|', cleanSource, kind, cleanType, Digits.Replace(cleanMessage, "#"));
        var now = _clock();
        var announceFull = false;

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
                // Once per episode: the table filling up is worth one line, not one per dropped error.
                announceFull = !_tableFullLogged;
                _tableFullLogged = true;
            }
            else
            {
                _tableFullLogged = false;
                AddPendingLocked(signature, cleanSource, kind, cleanMessage, cleanType, cleanStack, now);
            }
        }

        // Written outside the lock: nothing is ever logged while the table is held.
        if (announceFull)
            FileLog.Write($"{ErrorLine.ReporterTag} {MaxPending} distinct errors are waiting to be sent; new ones are dropped until some are sent");
    }

    private void AddPendingLocked(string signature, string cleanSource, string kind, string cleanMessage,
        string cleanType, string cleanStack, DateTime now)
    {
        var pending = new Pending
        {
            Signature = signature,
            Source = cleanSource,
            Kind = kind,
            Message = cleanMessage,
            ExceptionType = cleanType,
            Stack = cleanStack,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        _bySignature[signature] = _order.AddLast(pending);
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
    internal Task<int> SendPendingAsync(CancellationToken ct) => SendAsync(ct, final: false);

    /// <summary>
    /// The last send before the process dies. Unlike the periodic send it WAITS for a send already in flight
    /// (until <paramref name="ct"/> expires) instead of skipping, and it ignores the hourly budget and any
    /// pause: a crash that happened after a noisy hour is exactly the one worth sending.
    /// </summary>
    internal Task<int> FlushAsync(CancellationToken ct) => SendAsync(ct, final: true);

    private async Task<int> SendAsync(CancellationToken ct, bool final)
    {
        if (!await _sendGate.WaitAsync(final ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, ct).ConfigureAwait(false)) return 0;
        try
        {
            var config = _config();
            if (!config.IsEnabled || string.IsNullOrWhiteSpace(config.Token))
                return await SendBeforeSignInAsync(final, ct).ConfigureAwait(false);

            var delivered = await SendPendingSignedInAsync(config, final, ct).ConfigureAwait(false);
            // Errors kept on disk from before this machine signed in go to its own account now - after the
            // fresh ones, inside the same hourly budget, and never on the last send before exit.
            if (!final && _preSignIn is not null)
                delivered += await SendKeptSignedInAsync(config, ct).ConfigureAwait(false);
            return delivered;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// No Gateway credential on this machine. Everything pending moves to the disk outbox (issue #3311, B1) and
    /// the outbox sends what it can to the public install-report route. Without an outbox - a reporter a test
    /// built without one - the errors are counted as dropped, which is what the tests of that case pin.
    /// </summary>
    private async Task<int> SendBeforeSignInAsync(bool final, CancellationToken ct)
    {
        List<Pending> all;
        lock (_lock)
        {
            all = _order.ToList();
            _order.Clear();
            _bySignature.Clear();
        }

        if (_preSignIn is null)
        {
            if (all.Count == 0) return 0;
            if (!_loggedNoGateway)
            {
                _loggedNoGateway = true;
                FileLog.Write($"{ErrorLine.ReporterTag} no Gateway credential on this machine and no outbox; errors stay in the local log only");
            }
            Drop(all.Count);
            return 0;
        }

        if (all.Count > 0)
        {
            try
            {
                _preSignIn.Keep(all.Select(ToItem).ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The disk refused. The errors go back into memory, where the next tick tries again.
                FileLog.Write($"{ErrorLine.ReporterTag} could not keep {all.Count} error(s) on disk ({ex.GetType().Name}): {ex.Message}");
                Requeue(all);
                return 0;
            }
        }

        if (!_loggedNoGateway)
        {
            _loggedNoGateway = true;
            FileLog.Write($"{ErrorLine.ReporterTag} no Gateway credential on this machine yet; errors are kept in {_preSignIn.FilePath} and sent to DevThrottle's install-report route until it signs in");
        }

        var sent = await _preSignIn.SendBeforeSignInAsync(final, ct).ConfigureAwait(false);
        Interlocked.Add(ref _sent, sent);
        return sent;
    }

    private async Task<int> SendPendingSignedInAsync(GatewayConfig config, bool final, CancellationToken ct)
    {
        var now = _clock();
        List<Pending> batch;
        lock (_lock)
        {
            if (_order.Count == 0 || (!final && now < _pausedUntilUtc)) return 0;
            var budget = final ? ErrorReportLimits.MaxReportsPerBatch : MaxSentPerHour - SentInLastHour(now);
            if (budget <= 0) return 0;
            batch = _order.Take(Math.Min(budget, ErrorReportLimits.MaxReportsPerBatch)).ToList();
            foreach (var p in batch)
            {
                _order.Remove(_bySignature[p.Signature]);
                _bySignature.Remove(p.Signature);
            }
        }

        HttpStatusCode status;
        try
        {
            status = await PostAsync(config, batch.Select(ToItem).ToList(), ct).ConfigureAwait(false);
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
            RecordSent(now, batch.Count);
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

    /// <summary>
    /// Send errors kept on disk from before sign-in through the device route. A failure of any kind leaves them
    /// on disk: unlike a fresh error they have waited for this, so they are never dropped for a refusal.
    /// </summary>
    private async Task<int> SendKeptSignedInAsync(GatewayConfig config, CancellationToken ct)
    {
        var now = _clock();
        int budget;
        lock (_lock)
        {
            if (now < _pausedUntilUtc) return 0;
            budget = MaxSentPerHour - SentInLastHour(now);
        }
        var sent = await _preSignIn!.SendSignedInAsync(budget, async items =>
        {
            try
            {
                var status = await PostAsync(config, items, ct).ConfigureAwait(false);
                if ((int)status is >= 200 and < 300) return true;
                FileLog.Write($"{ErrorLine.ReporterTag} {items.Count} error(s) kept from before sign-in not accepted (HTTP {(int)status}); still on disk");
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                FileLog.Write($"{ErrorLine.ReporterTag} {items.Count} error(s) kept from before sign-in not delivered ({ex.GetType().Name}): {ex.Message}");
                return false;
            }
        }).ConfigureAwait(false);
        if (sent > 0) RecordSent(now, sent);
        return sent;
    }

    private async Task<HttpStatusCode> PostAsync(GatewayConfig config, IReadOnlyList<ErrorReportItem> items, CancellationToken ct)
    {
        var url = config.Url.TrimEnd('/') + Path;
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new ErrorReportBatch(items)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.StatusCode;
    }

    private void RecordSent(DateTime at, int count)
    {
        lock (_lock) _sentWindow.Enqueue((at, count));
        Interlocked.Add(ref _sent, count);
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
        if (_ownsHttp) _http.Dispose();
    }
}
