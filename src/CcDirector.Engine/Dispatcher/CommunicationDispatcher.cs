using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Engine.Archival;
using CcDirector.Engine.Events;
using Microsoft.Data.Sqlite;

namespace CcDirector.Engine.Dispatcher;

public sealed class CommunicationDispatcher : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _communicationsDbPath;
    private readonly EmailRoutingTable _routingTable;
    private readonly int _pollIntervalSeconds;
    private readonly VaultArchiver _archiver = new();
    private readonly ToolProcessRunner _processRunner;
    // Serializes on-demand by-id dispatches (issue #329) so two concurrent POST /dispatch
    // calls for the same item cannot both observe 'approved' and double-send it.
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);
#pragma warning disable CS0649 // Timer field not assigned until auto-dispatch is re-enabled
    private Timer? _timer;
#pragma warning restore CS0649
    private int _polling; // 0 = idle, 1 = polling (used with Interlocked for thread safety)

    public event Action<EngineEvent>? OnEvent;

    public CommunicationDispatcher(
        string communicationsDbPath,
        EmailRoutingTable routingTable,
        int pollIntervalSeconds = 5,
        ToolProcessRunner? processRunner = null)
    {
        _communicationsDbPath = communicationsDbPath;
        _routingTable = routingTable;
        _pollIntervalSeconds = pollIntervalSeconds;
        // Default = the real channel (argument-list Process.Start). Tests inject a mock
        // channel here so no test can ever produce a real outbound send.
        _processRunner = processRunner ?? RunToolProcessAsync;

        FileLog.Write($"[CommunicationDispatcher] Initialized with {routingTable.Count} email routes");
    }

    public void Start()
    {
        FileLog.Write("[CommunicationDispatcher] Starting (auto-dispatch disabled -- use manual Send button)");
        // TODO: Re-enable auto-dispatch timer once manual testing is complete
        // _timer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromSeconds(_pollIntervalSeconds));
    }

    public void Stop()
    {
        FileLog.Write("[CommunicationDispatcher] Stopping");
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    // Timer callback -- async void is correct here (entry point/boundary, same as event handler)
    private async void Poll(object? state)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;

        try
        {
            if (!File.Exists(_communicationsDbPath))
            {
                FileLog.Write($"[CommunicationDispatcher] DB not found: {_communicationsDbPath}");
                return;
            }

            var approved = GetApprovedEmails();
            foreach (var email in approved)
            {
                await DispatchEmailAsync(email);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationDispatcher] Poll error: {ex.Message}");
            RaiseEvent(new EngineEvent(EngineEventType.Error, Message: $"Dispatcher poll error: {ex.Message}"));
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private List<ApprovedEmail> GetApprovedEmails()
    {
        var emails = new List<ApprovedEmail>();
        var connectionString = $"Data Source={_communicationsDbPath};Mode=ReadWrite";

        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ticket_number, content, email_specific, persona, send_from
            FROM communications
            WHERE status = 'approved' AND platform = 'email'
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var ticket = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var body = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var emailSpecific = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var persona = reader.IsDBNull(4) ? "personal" : reader.GetString(4);
            var sendFrom = reader.IsDBNull(5) ? null : reader.GetString(5);

            var email = TryBuildEmail(id, ticket, body, emailSpecific, persona, sendFrom);
            if (email != null)
                emails.Add(email);
        }

        return emails;
    }

    /// <summary>
    /// Builds the sendable email from a communications row, or null when the row is not
    /// sendable (unparseable email_specific or no recipients). Shared by the poll path
    /// and the by-id verb so both interpret a row identically.
    /// </summary>
    private static ApprovedEmail? TryBuildEmail(
        string id, int ticket, string body, string emailSpecific, string persona, string? sendFrom)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<EmailSpecific>(emailSpecific, JsonOptions);

            if (spec?.To == null || spec.To.Count == 0)
            {
                FileLog.Write($"[CommunicationDispatcher] Ticket #{ticket} has no recipients in email_specific");
                return null;
            }

            return new ApprovedEmail
            {
                Id = id,
                TicketNumber = ticket,
                Body = body,
                To = string.Join(",", spec.To),
                Cc = spec.Cc != null && spec.Cc.Count > 0 ? string.Join(",", spec.Cc) : null,
                Bcc = spec.Bcc != null && spec.Bcc.Count > 0 ? string.Join(",", spec.Bcc) : null,
                Subject = spec.Subject ?? "(no subject)",
                Attachments = spec.Attachments ?? new List<string>(),
                Persona = persona,
                SendFrom = sendFrom
            };
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CommunicationDispatcher] Failed to parse email_specific for ticket #{ticket}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Dispatch ONE queue item by id, on demand (issue #329, the <c>POST /dispatch</c> verb).
    /// Mechanical execution of an already-made approval decision: the HARD GATE is that the
    /// item must currently be in the 'approved' state of the approval workflow - anything
    /// else (pending_review, rejected, already posted, unknown) is refused and NOTHING is
    /// sent. A dispatched item advances to 'posted' exactly as the poll path does
    /// (status + posted_at/posted_by, vault archive attempt, CommunicationDispatched event).
    /// </summary>
    public async Task<QueueDispatchResult> DispatchByIdAsync(string queueItemId)
    {
        if (string.IsNullOrWhiteSpace(queueItemId))
            throw new ArgumentException("queueItemId is required", nameof(queueItemId));

        FileLog.Write($"[CommunicationDispatcher] DispatchById: id={queueItemId}");

        if (!File.Exists(_communicationsDbPath))
        {
            FileLog.Write($"[CommunicationDispatcher] DispatchById REFUSED: communications DB not found: {_communicationsDbPath}");
            return new QueueDispatchResult(QueueDispatchOutcome.NotFound, queueItemId,
                Error: "communications database not found on this Director");
        }

        await _dispatchLock.WaitAsync();
        try
        {
            var row = LoadItemById(queueItemId);
            if (row == null)
            {
                FileLog.Write($"[CommunicationDispatcher] DispatchById NOT FOUND: id={queueItemId}");
                return new QueueDispatchResult(QueueDispatchOutcome.NotFound, queueItemId,
                    Error: $"no queue item with id '{queueItemId}'");
            }

            // THE approval gate. The verb never decides; it only executes a decision the
            // human already made in the approval workflow. Audit the refusal explicitly.
            if (!string.Equals(row.Status, "approved", StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[CommunicationDispatcher] DispatchById REFUSED (approval gate): id={queueItemId} ticket=#{row.TicketNumber} status={row.Status} - nothing sent");
                return new QueueDispatchResult(QueueDispatchOutcome.NotApproved, queueItemId,
                    row.TicketNumber, ItemStatus: row.Status,
                    Error: $"item is '{row.Status}', not 'approved' - only approved items dispatch; nothing was sent");
            }

            if (!string.Equals(row.Platform, "email", StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[CommunicationDispatcher] DispatchById REFUSED (platform): id={queueItemId} ticket=#{row.TicketNumber} platform={row.Platform} - no machine-bound sender");
                return new QueueDispatchResult(QueueDispatchOutcome.UnsupportedPlatform, queueItemId,
                    row.TicketNumber, ItemStatus: row.Status,
                    Error: $"platform '{row.Platform}' has no machine-bound dispatch path on this Director (only email)");
            }

            var email = TryBuildEmail(row.Id, row.TicketNumber, row.Body, row.EmailSpecific, row.Persona, row.SendFrom);
            if (email == null)
            {
                FileLog.Write($"[CommunicationDispatcher] DispatchById REFUSED (invalid item): id={queueItemId} ticket=#{row.TicketNumber} - email_specific missing/unparseable or no recipients");
                return new QueueDispatchResult(QueueDispatchOutcome.InvalidItem, queueItemId,
                    row.TicketNumber, ItemStatus: row.Status,
                    Error: "item is approved but not sendable: email_specific is missing/unparseable or has no recipients");
            }

            var result = await DispatchEmailAsync(email);
            FileLog.Write($"[CommunicationDispatcher] DispatchById result: id={queueItemId} ticket=#{row.TicketNumber} outcome={result.Outcome} channel={result.Channel ?? "(none)"}");
            return result;
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    private QueueItemRow? LoadItemById(string queueItemId)
    {
        using var conn = new SqliteConnection($"Data Source={_communicationsDbPath};Mode=ReadWrite");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ticket_number, content, email_specific, persona, send_from, status, platform
            FROM communications
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", queueItemId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new QueueItemRow(
            Id: reader.GetString(0),
            TicketNumber: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            Body: reader.IsDBNull(2) ? "" : reader.GetString(2),
            EmailSpecific: reader.IsDBNull(3) ? "" : reader.GetString(3),
            Persona: reader.IsDBNull(4) ? "personal" : reader.GetString(4),
            SendFrom: reader.IsDBNull(5) ? null : reader.GetString(5),
            Status: reader.IsDBNull(6) ? "" : reader.GetString(6),
            Platform: reader.IsDBNull(7) ? "" : reader.GetString(7));
    }

    /// <summary>
    /// Sends one approved email through its routed channel tool, marking the item
    /// posted/failed and raising events exactly as before. Returns the outcome so the
    /// by-id verb can report it; the poll path ignores the return value.
    /// </summary>
    private async Task<QueueDispatchResult> DispatchEmailAsync(ApprovedEmail email)
    {
        var sendFrom = email.SendFrom ?? email.Persona;
        var route = _routingTable.FindRoute(sendFrom);

        if (route == null)
        {
            var knownEmails = string.Join(", ", _routingTable.AllRoutes.Select(r => r.EmailAddress));
            var error = $"No email route found for '{sendFrom}'. Known routes: [{knownEmails}]";
            MarkFailed(email.Id, error);
            FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} FAILED: {error}");
            RaiseEvent(new EngineEvent(EngineEventType.Error,
                Message: $"Email ticket #{email.TicketNumber} failed: {error}"));
            return new QueueDispatchResult(QueueDispatchOutcome.SendFailed, email.Id,
                email.TicketNumber, ItemStatus: "approved", Error: error);
        }

        FileLog.Write($"[CommunicationDispatcher] Sending ticket #{email.TicketNumber} to {email.To} via {route.ToolName} (send_from={sendFrom}, account={route.AccountName})");

        try
        {
            var args = BuildSendArgs(email, route);
            var result = await _processRunner(route.ToolPath, args);

            if (result.ExitCode == 0)
            {
                HandleSendSuccess(email, route.ToolName);
                return new QueueDispatchResult(QueueDispatchOutcome.Dispatched, email.Id,
                    email.TicketNumber, ItemStatus: "posted", Channel: route.ToolName);
            }

            var error = string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stderr;
            MarkFailed(email.Id, error);
            FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} FAILED via {route.ToolName}: {error}");
            RaiseEvent(new EngineEvent(EngineEventType.Error,
                Message: $"Email ticket #{email.TicketNumber} failed via {route.ToolName}: {error}"));
            return new QueueDispatchResult(QueueDispatchOutcome.SendFailed, email.Id,
                email.TicketNumber, ItemStatus: "approved", Channel: route.ToolName, Error: error);
        }
        catch (Exception ex)
        {
            MarkFailed(email.Id, ex.Message);
            FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} exception: {ex.Message}");
            return new QueueDispatchResult(QueueDispatchOutcome.SendFailed, email.Id,
                email.TicketNumber, ItemStatus: "approved", Channel: route.ToolName, Error: ex.Message);
        }
    }

    private static List<string> BuildSendArgs(ApprovedEmail email, EmailRoute route)
    {
        var args = new List<string>();

        args.Add("--account");
        args.Add(route.AccountName);

        // Convert plain text newlines to HTML tags before sending with --html flag.
        // Without this, email clients ignore \n and recipients see a wall of text.
        var htmlBody = HtmlFormatter.ConvertPlainTextToHtml(email.Body);

        args.AddRange(new[]
        {
            "send",
            "-t", email.To,
            "-s", email.Subject,
            "-b", htmlBody,
            "--html"
        });

        if (!string.IsNullOrEmpty(email.Cc))
        {
            args.Add("--cc");
            args.Add(email.Cc);
        }

        if (!string.IsNullOrEmpty(email.Bcc))
        {
            args.Add("--bcc");
            args.Add(email.Bcc);
        }

        foreach (var attachment in email.Attachments)
        {
            if (File.Exists(attachment))
            {
                args.Add("--attach");
                args.Add(attachment);
            }
        }

        return args;
    }

    private static async Task<ToolProcessResult> RunToolProcessAsync(string toolPath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"Failed to start process: {toolPath}");

        // Read both streams concurrently to avoid deadlock when both buffers fill
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await stderrTask;
        await process.WaitForExitAsync();

        return new ToolProcessResult(process.ExitCode, stdout, stderr);
    }

    private void HandleSendSuccess(ApprovedEmail email, string toolName)
    {
        MarkPosted(email.Id);
        try
        {
            _archiver.ArchiveEmail(email.To, email.Subject, email.Body);
        }
        catch (Exception archiveEx)
        {
            FileLog.Write($"[CommunicationDispatcher] Vault archive FAILED (email still sent): {archiveEx.Message}");
        }
        FileLog.Write($"[CommunicationDispatcher] Ticket #{email.TicketNumber} sent OK via {toolName}");
        RaiseEvent(new EngineEvent(EngineEventType.CommunicationDispatched,
            Message: $"Email ticket #{email.TicketNumber} sent to {email.To} via {toolName}"));
    }

    private void MarkPosted(string id)
    {
        using var conn = new SqliteConnection($"Data Source={_communicationsDbPath};Mode=ReadWrite");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE communications
            SET status = 'posted', posted_at = @now, posted_by = 'cc-director'
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void MarkFailed(string id, string error)
    {
        using var conn = new SqliteConnection($"Data Source={_communicationsDbPath};Mode=ReadWrite");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE communications
            SET notes = @error
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@error", $"Send failed: {error}");
        cmd.ExecuteNonQuery();
    }

    private void RaiseEvent(EngineEvent e)
    {
        try { OnEvent?.Invoke(e); }
        catch (Exception ex) { FileLog.Write($"[CommunicationDispatcher] Event handler error: {ex.Message}"); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _dispatchLock.Dispose();
    }

    /// <summary>One communications row as loaded for the by-id verb (pre-gate, any status).</summary>
    private sealed record QueueItemRow(
        string Id,
        int TicketNumber,
        string Body,
        string EmailSpecific,
        string Persona,
        string? SendFrom,
        string Status,
        string Platform);

    private class ApprovedEmail
    {
        public string Id { get; set; } = "";
        public int TicketNumber { get; set; }
        public string Body { get; set; } = "";
        public string To { get; set; } = "";
        public string? Cc { get; set; }
        public string? Bcc { get; set; }
        public string Subject { get; set; } = "";
        public List<string> Attachments { get; set; } = new();
        public string Persona { get; set; } = "personal";
        public string? SendFrom { get; set; }
    }

    private class EmailSpecific
    {
        public List<string>? To { get; set; }
        public List<string>? Cc { get; set; }
        public List<string>? Bcc { get; set; }
        public string? Subject { get; set; }
        public List<string>? Attachments { get; set; }
    }

}
