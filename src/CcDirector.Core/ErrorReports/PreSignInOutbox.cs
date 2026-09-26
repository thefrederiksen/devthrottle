using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// Where a launcher's or a Director's errors go while the machine has NO Gateway credential (issue #3311,
/// part B1). Before this, <see cref="ErrorReporter"/> dropped every one of them - so the errors of a fresh
/// install, the ones we were blindest to, never left the machine.
///
/// WHAT IT DOES.
///   - <see cref="Keep"/> writes the errors to a small file on disk FIRST, so a process that dies a moment
///     later has lost nothing. The same error seen again is one entry with a larger count, so the file grows
///     with the number of DISTINCT errors, and it holds at most <see cref="MaxKept"/> of those.
///   - <see cref="SendBeforeSignInAsync"/> sends what is kept to the hosted Gateway's public
///     <c>POST /install-reports</c> - the route the installer uses, which needs no credential - as ONE report
///     carrying as many errors as fit in its diagnostics field. That route allows ten reports an hour per
///     install id, and the installer, the launcher and every Director on the machine share that one id, so this
///     sends at most <see cref="MaxReportsPerHour"/> an hour and backs off when the Gateway says 429.
///   - <see cref="SendSignedInAsync"/> hands whatever is still kept to the device's own route once the machine
///     HAS signed in, so an error from before sign-in reaches the account it belongs to.
///   - An entry leaves the file only when the Gateway has accepted it. A failure, a refusal or a missing route
///     keeps it for the next try. Nothing is dropped; when the file is full, the count of distinct errors that
///     did not fit is kept instead and said in the next report.
///
/// ONE WRITER. The file is per storage root and per component, and a launcher and a Director are each one
/// process per storage root (their single-instance guards), so no two processes write the same file. Calls
/// from within the process are serialised by <see cref="ErrorReporter"/>'s send gate and by the lock here.
/// </summary>
public sealed class PreSignInOutbox
{
    /// <summary>The step every report from here carries, so the owner can tell these from installer failures.</summary>
    public const string Step = "errors-before-sign-in";

    internal const int MaxKept = 500;
    internal const int MaxReportsPerHour = 3;
    internal const int MaxStackInReport = 1500;
    // Below the route's own cap, so the Gateway's scrub (which can lengthen a redacted value) never cuts a
    // report we believed was whole.
    internal const int DiagnosticsBudget = InstallReportLimits.MaxDiagnostics - 1000;
    internal static readonly TimeSpan PauseAfterRateLimit = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan PauseAfterMissingRoute = TimeSpan.FromHours(1);

    private static readonly Regex Digits = new(@"\d+", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

    private readonly string _path;
    private readonly string _component;
    private readonly Func<string> _installId;
    private readonly Func<string> _gatewayUrl;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _clock;

    private readonly object _lock = new();
    private readonly Queue<DateTime> _sentTimes = new();
    private DateTime _pausedUntilUtc = DateTime.MinValue;

    internal sealed record FileShape(
        [property: JsonPropertyName("items")] List<ErrorReportItem> Items,
        [property: JsonPropertyName("not_kept")] long NotKept);

    /// <param name="path">The outbox file.</param>
    /// <param name="component"><see cref="ErrorReportLimits.Director"/> or <see cref="ErrorReportLimits.Launcher"/>.</param>
    /// <param name="installId">The machine's install id (<see cref="InstallId.ReadOrCreate"/>).</param>
    /// <param name="gatewayUrl">The hosted Gateway (<see cref="Configuration.HostedGateway.ResolveUrl"/>).</param>
    public PreSignInOutbox(string path, string component, Func<string> installId, Func<string> gatewayUrl,
        HttpClient http, Func<DateTime> clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        _path = path;
        _component = component;
        _installId = installId ?? throw new ArgumentNullException(nameof(installId));
        _gatewayUrl = gatewayUrl ?? throw new ArgumentNullException(nameof(gatewayUrl));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The outbox file, for a log line that tells a person where the errors are.</summary>
    public string FilePath => _path;

    /// <summary>Distinct errors waiting on disk.</summary>
    public int Count { get { lock (_lock) return Read().Items.Count; } }

    /// <summary>
    /// Add errors to the file, folding each into an entry for the same error when there is one. Throws when
    /// the file cannot be written; the caller keeps the errors in memory and says so.
    /// </summary>
    public void Keep(IReadOnlyList<ErrorReportItem> items)
    {
        if (items.Count == 0) return;
        lock (_lock)
        {
            var file = Read();
            var kept = file.Items;
            var notKept = file.NotKept;
            foreach (var item in items)
            {
                var signature = SignatureOf(item);
                var index = kept.FindIndex(k => SignatureOf(k) == signature);
                if (index >= 0)
                {
                    var existing = kept[index];
                    kept[index] = existing with
                    {
                        RepeatCount = existing.RepeatCount + item.RepeatCount,
                        FirstSeenUtc = existing.FirstSeenUtc < item.FirstSeenUtc ? existing.FirstSeenUtc : item.FirstSeenUtc,
                        LastSeenUtc = existing.LastSeenUtc > item.LastSeenUtc ? existing.LastSeenUtc : item.LastSeenUtc,
                    };
                }
                else if (kept.Count < MaxKept)
                {
                    kept.Add(item);
                }
                else
                {
                    notKept++;
                }
            }
            Write(new FileShape(kept, notKept));
        }
    }

    /// <summary>
    /// Send what is kept as one report to <c>POST /install-reports</c>. Returns how many errors the Gateway
    /// accepted. Within <see cref="MaxReportsPerHour"/> and any back-off, unless <paramref name="final"/> -
    /// the last try before the process dies, when a crash is exactly the report worth sending. Never throws
    /// except for cancellation.
    /// </summary>
    public async Task<int> SendBeforeSignInAsync(bool final, CancellationToken ct)
    {
        InstallReportPayload payload;
        List<(string Signature, int Count)> sent;
        long notKeptSent;
        var now = _clock();
        try
        {
            lock (_lock)
            {
                var file = Read();
                if (file.Items.Count == 0) return 0;
                if (!final && (now < _pausedUntilUtc || SentInLastHour(now) >= MaxReportsPerHour)) return 0;
                var (composed, used) = Compose(_component, _installId(), file.Items, file.NotKept);
                payload = composed;
                sent = file.Items.Take(used).Select(i => (SignatureOf(i), i.RepeatCount)).ToList();
                notKeptSent = file.NotKept;
                _sentTimes.Enqueue(now);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The install id could not be read or made, or the file could not be read. The errors stay where
            // they are; the next tick tries again.
            FileLog.Write($"{ErrorLine.ReporterTag} errors before sign-in not sent ({ex.GetType().Name}): {ex.Message}");
            return 0;
        }

        string url;
        try
        {
            url = _gatewayUrl();
        }
        catch (InvalidOperationException ex)
        {
            FileLog.Write($"{ErrorLine.ReporterTag} errors before sign-in not sent: {ex.Message}");
            return 0;
        }

        var outcome = await InstallReportClient.PostAsync(_http, url, payload, ct).ConfigureAwait(false);
        if (outcome.Accepted)
        {
            TryRemove(sent, notKeptSent);
            FileLog.Write($"{ErrorLine.ReporterTag} {sent.Count} error(s) from before sign-in sent to DevThrottle ({outcome})");
            return sent.Count;
        }

        if (outcome.RateLimited)
        {
            Pause(PauseAfterRateLimit);
            FileLog.Write($"{ErrorLine.ReporterTag} DevThrottle asked for fewer reports from this machine (HTTP 429); {sent.Count} error(s) kept on disk, next try in {PauseAfterRateLimit.TotalMinutes:0} minutes");
        }
        else if (outcome.RouteMissing)
        {
            Pause(PauseAfterMissingRoute);
            FileLog.Write($"{ErrorLine.ReporterTag} the Gateway has no {InstallReportLimits.Path} route (HTTP 404); {sent.Count} error(s) kept on disk, next try in {PauseAfterMissingRoute.TotalMinutes:0} minutes");
        }
        else
        {
            FileLog.Write($"{ErrorLine.ReporterTag} {sent.Count} error(s) from before sign-in kept on disk: {outcome}");
        }
        return 0;
    }

    /// <summary>
    /// The machine has signed in: hand up to <paramref name="max"/> kept errors to <paramref name="send"/>
    /// (the device's own route) and remove them when it reports success. Returns how many were delivered.
    /// </summary>
    public async Task<int> SendSignedInAsync(int max, Func<IReadOnlyList<ErrorReportItem>, Task<bool>> send)
    {
        if (max <= 0) return 0;
        List<ErrorReportItem> batch;
        try
        {
            lock (_lock) batch = Read().Items.Take(Math.Min(max, ErrorReportLimits.MaxReportsPerBatch)).ToList();
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // Runs on every tick of a signed-in machine whose file still exists: a file that cannot be read
            // must cost this tick, never the send loop.
            FileLog.Write($"{ErrorLine.ReporterTag} errors kept from before sign-in could not be read ({ex.GetType().Name}): {ex.Message}; next tick tries again");
            return 0;
        }
        if (batch.Count == 0) return 0;
        if (!await send(batch).ConfigureAwait(false)) return 0;
        TryRemove(batch.Select(i => (SignatureOf(i), i.RepeatCount)).ToList(), notKeptSent: 0);
        FileLog.Write($"{ErrorLine.ReporterTag} {batch.Count} error(s) kept from before sign-in sent with this machine's credential");
        return batch.Count;
    }

    /// <summary>
    /// One report from the front of <paramref name="items"/>: as many whole errors as fit the diagnostics
    /// budget, and always at least the first (cut to fit). Returns the payload and how many errors it carries.
    /// </summary>
    internal static (InstallReportPayload Payload, int Used) Compose(string component, string installId,
        IReadOnlyList<ErrorReportItem> items, long notKept)
    {
        var diagnostics = new StringBuilder();
        var used = 0;
        foreach (var item in items)
        {
            var block = Describe(item);
            if (used > 0 && diagnostics.Length + block.Length > DiagnosticsBudget) break;
            diagnostics.Append(block);
            used++;
        }
        var text = diagnostics.Length > DiagnosticsBudget ? diagnostics.ToString(0, DiagnosticsBudget) : diagnostics.ToString();

        var first = items[0];
        var occurrences = items.Take(used).Sum(i => (long)Math.Max(1, i.RepeatCount));
        var message = new StringBuilder()
            .Append($"{used} distinct error(s), {occurrences} in all, logged by the {component} before this machine signed in.");
        if (used < items.Count) message.Append($" {items.Count - used} more are waiting for the next report.");
        if (notKept > 0) message.Append($" {notKept} further distinct error(s) were not kept because the queue on disk was full.");
        message.Append($" First: [{first.Source}] {first.Message}");
        var messageText = message.ToString();
        if (messageText.Length > InstallReportLimits.MaxMessage) messageText = messageText[..InstallReportLimits.MaxMessage];

        return (new InstallReportPayload(
            InstallId: installId,
            Installer: component,
            Component: component,
            Step: Step,
            Message: messageText,
            Diagnostics: text,
            Os: first.Os ?? "",
            OsVersion: first.OsVersion ?? "",
            Arch: first.Arch ?? "",
            ProductVersion: first.ProductVersion ?? ""), used);
    }

    private static string Describe(ErrorReportItem item)
    {
        var sb = new StringBuilder();
        sb.Append($"--- [{item.Kind}] {item.Source} x{Math.Max(1, item.RepeatCount)}, first {item.FirstSeenUtc:yyyy-MM-dd HH:mm:ss}Z, last {item.LastSeenUtc:yyyy-MM-dd HH:mm:ss}Z, version {item.ProductVersion}\n");
        sb.Append(item.Message).Append('\n');
        if (!string.IsNullOrEmpty(item.ExceptionType)) sb.Append(item.ExceptionType).Append('\n');
        if (!string.IsNullOrEmpty(item.Stack))
        {
            var stack = item.Stack.Length > MaxStackInReport ? item.Stack[..MaxStackInReport] + "\n(stack cut)" : item.Stack;
            sb.Append(stack).Append('\n');
        }
        return sb.Append('\n').ToString();
    }

    internal static string SignatureOf(ErrorReportItem item)
        => string.Join('|', item.Component, item.Source, item.Kind, item.ExceptionType, Digits.Replace(item.Message ?? "", "#"));

    /// <summary>
    /// <see cref="Remove"/>, where a file that cannot be read or written costs duplicates, never the send loop:
    /// an exception here used to escape into the reporter's loop, which then stopped for the life of the
    /// process (review of #3425). The delivered errors stay in the file and are sent again next time.
    /// </summary>
    private void TryRemove(List<(string Signature, int Count)> delivered, long notKeptSent)
    {
        try
        {
            lock (_lock) Remove(delivered, notKeptSent);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            FileLog.Write($"{ErrorLine.ReporterTag} {delivered.Count} delivered error(s) could not be taken out of {_path} ({ex.GetType().Name}): {ex.Message}; they will be sent again as duplicates");
        }
    }

    private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>Take what was delivered out of the file. An entry that was seen AGAIN while its report was in
    /// flight keeps the occurrences the report did not carry, so a count is never lost.</summary>
    private void Remove(List<(string Signature, int Count)> delivered, long notKeptSent)
    {
        var file = Read();
        var sentCounts = delivered.GroupBy(d => d.Signature, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(d => d.Count), StringComparer.Ordinal);
        var remaining = new List<ErrorReportItem>();
        foreach (var item in file.Items)
        {
            if (!sentCounts.TryGetValue(SignatureOf(item), out var count))
                remaining.Add(item);
            else if (item.RepeatCount > count)
                remaining.Add(item with { RepeatCount = item.RepeatCount - count });
        }
        Write(new FileShape(remaining, Math.Max(0, file.NotKept - notKeptSent)));
    }

    private int SentInLastHour(DateTime now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        while (_sentTimes.Count > 0 && _sentTimes.Peek() < cutoff) _sentTimes.Dequeue();
        return _sentTimes.Count;
    }

    private void Pause(TimeSpan span)
    {
        lock (_lock) _pausedUntilUtc = _clock() + span;
    }

    /// <summary>
    /// Read the file. A file that is not readable JSON is moved aside, never deleted - it may hold the very
    /// errors somebody needs - and the outbox starts again empty.
    /// </summary>
    private FileShape Read()
    {
        if (!File.Exists(_path)) return new FileShape(new List<ErrorReportItem>(), 0);
        var text = File.ReadAllText(_path);
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(text, FileJson);
            if (shape?.Items is not null) return shape;
        }
        catch (JsonException)
        {
        }
        var aside = _path + ".unreadable-" + _clock().ToString("yyyyMMddHHmmss");
        File.Move(_path, aside, overwrite: true);
        FileLog.Write($"{ErrorLine.ReporterTag} the errors-before-sign-in file was not readable; moved to {aside} and started again");
        return new FileShape(new List<ErrorReportItem>(), 0);
    }

    private void Write(FileShape shape)
    {
        if (shape.Items.Count == 0 && shape.NotKept == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(shape, FileJson));
        File.Move(temp, _path, overwrite: true);
    }
}
