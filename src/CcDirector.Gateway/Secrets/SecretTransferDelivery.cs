using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Secrets;

/// <summary>
/// MOVES ONE APPROVED SECRET TRANSFER (the Secret Handoff mission, issue #2943): asks the Director on the holding
/// machine to seal the entry for the receiving machine, then hands the sealed envelope to the Director on the receiving
/// machine to open and store, and records how it ended.
///
/// THE ENVELOPE IS NEVER AT REST HERE. It exists only in a local variable of <see cref="DeliverAsync"/> between the two
/// commands - never on a row, never in a log line, never in an error sentence - and the Gateway holds no key that opens
/// it. Every log line names the transfer, the step and the outcome only.
///
/// It goes only to a Director that offered a key in its Hello, which only a build that carries the two verbs does, so an
/// older Director is never sent a transfer.
/// </summary>
internal sealed class SecretTransferDelivery
{
    public const string SendVerb = "secret-transfer-send";
    public const string ReceiveVerb = "secret-transfer-receive";

    /// <summary>How long the receiving machine has to store it, counted from when delivery starts. Bound into the
    /// envelope, so an envelope held back and replayed later is refused.</summary>
    public static readonly TimeSpan DeliverWithin = TimeSpan.FromMinutes(5);

    /// <summary>Each Director runs cc-secrets with a 60 second limit; this is the backstop around it.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SecretTransferStore _transfers;
    private readonly SecretMachineRegistry _machines;
    private readonly Func<TenantId, string, bool> _isConnected;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _send;
    private readonly Func<TenantId, IDisposable> _enterScope;
    private readonly Func<DateTime> _nowUtc;

    public SecretTransferDelivery(SecretTransferStore transfers, SecretMachineRegistry machines,
        Func<TenantId, string, bool> isConnected, DirectorCommandRouter.SendDirectorCommandAsync send,
        Func<TenantId, IDisposable> enterScope, Func<DateTime> nowUtc)
    {
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _machines = machines ?? throw new ArgumentNullException(nameof(machines));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>Start moving an approved transfer, without making the answering request wait for it.</summary>
    public void Start(TenantId tenant, string transferId)
    {
        FileLog.Write($"[SecretTransferDelivery] Start: transfer={transferId}");
        _ = Task.Run(() => DeliverAsync(tenant, transferId, CancellationToken.None));
    }

    /// <summary>Move it now and record the outcome. Returns the outcome sentence (for tests and the log).</summary>
    public async Task<string> DeliverAsync(TenantId tenant, string transferId, CancellationToken ct)
    {
        using var scope = _enterScope(tenant);
        try
        {
            var outcome = await MoveAsync(tenant, transferId, ct);
            FileLog.Write($"[SecretTransferDelivery] DeliverAsync: transfer={transferId}, delivered={outcome.Delivered}");
            _transfers.TryFinish(tenant, transferId, outcome.Delivered, outcome.Sentence, _nowUtc());
            return outcome.Sentence;
        }
        catch (Exception ex)
        {
            // The entry point of a background task: nothing above it would ever see this. The transfer is ended as
            // failed with the exception's TYPE only - its message could quote a command result.
            FileLog.Write($"[SecretTransferDelivery] DeliverAsync FAILED: transfer={transferId}, {ex.GetType().Name}");
            var sentence = $"The Gateway failed while moving it ({ex.GetType().Name}). Check 'cc-secrets list' on the "
                           + "receiving machine before asking again.";
            _transfers.TryFinish(tenant, transferId, delivered: false, sentence, _nowUtc());
            return sentence;
        }
    }

    private readonly record struct Outcome(bool Delivered, string Sentence);

    private static Outcome Failed(string sentence) => new(false, sentence);

    private async Task<Outcome> MoveAsync(TenantId tenant, string transferId, CancellationToken ct)
    {
        var now = _nowUtc();
        var row = _transfers.Find(tenant, transferId, now);
        if (row is null || row.State != SecretTransferStates.Approved)
            return Failed("It was not waiting to be delivered. Nothing was moved.");

        var listed = _machines.Machines(tenant, id => _isConnected(tenant, id), now);
        var from = listed.FirstOrDefault(m => string.Equals(m.Machine, row.FromMachine, StringComparison.OrdinalIgnoreCase));
        var to = listed.FirstOrDefault(m => string.Equals(m.Machine, row.ToMachine, StringComparison.OrdinalIgnoreCase));
        if (from is null || from.Conflict is not null)
            return Failed($"{row.FromMachine} is not connected with a Director that can send it now. Nothing was moved.");
        if (to is null || to.Conflict is not null)
            return Failed($"{row.ToMachine} is not connected with a Director that can receive it now. Nothing was moved.");
        var fromDirector = _machines.DirectorFor(tenant, row.FromMachine, id => _isConnected(tenant, id), now);
        var toDirector = _machines.DirectorFor(tenant, row.ToMachine, id => _isConnected(tenant, id), now);
        if (fromDirector is null || toDirector is null)
            return Failed("A Director went away while the transfer started. Nothing was moved.");

        var deliverBy = (now + DeliverWithin).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var sendResult = await DirectorCommandRouter.TrySendAsync(_send, fromDirector, SendVerb, "", new SendCommand
        {
            TransferId = row.TransferId, Entry = row.EntryName, TargetName = row.TargetName, FromMachine = row.FromMachine,
            ToMachine = row.ToMachine, Replace = row.Replace, ExpiresAtUtc = deliverBy, ReceiverPublicKey = to.PublicKey,
            ReceiverFingerprint = to.Fingerprint, AcceptedReceiverFingerprint = row.AcceptedReceiverFingerprint,
            ApprovedWhere = row.AnsweredWhere ?? "", ApprovalWords = row.ApprovalWords,
        }, ct, CommandTimeout, row.FromMachine);
        if (Refused(sendResult, row.FromMachine, "seal it") is { } sendRefusal)
            return Failed(sendRefusal + " Nothing was moved.");
        var sealedEntry = Read<SendAnswer>(sendResult!);
        if (sealedEntry is null)
            return Failed($"The Director on {row.FromMachine} answered in a shape the Gateway does not read. Nothing was moved.");
        if (!sealedEntry.Ok)
            return Failed($"Not sent from {row.FromMachine}: {sealedEntry.Reason}");
        if (!string.Equals(sealedEntry.SenderFingerprint, from.Fingerprint, StringComparison.Ordinal)
            || !string.Equals(sealedEntry.SenderPublicKey, from.PublicKey, StringComparison.Ordinal)
            || string.IsNullOrEmpty(sealedEntry.Envelope))
            return Failed($"{row.FromMachine} answered with a key the Gateway does not list for it. Nothing was moved.");

        var receiveResult = await DirectorCommandRouter.TrySendAsync(_send, toDirector, ReceiveVerb, "", new ReceiveCommand
        {
            TransferId = row.TransferId, Entry = row.EntryName, TargetName = row.TargetName, FromMachine = row.FromMachine,
            ToMachine = row.ToMachine, Replace = row.Replace, ExpiresAtUtc = deliverBy, SenderPublicKey = from.PublicKey,
            SenderFingerprint = from.Fingerprint, ReceiverFingerprint = to.Fingerprint, Envelope = sealedEntry.Envelope,
            ApprovedWhere = row.AnsweredWhere ?? "", ApprovalWords = row.ApprovalWords,
        }, ct, CommandTimeout, row.ToMachine);
        if (receiveResult is { Status: DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped })
            return Failed($"{row.ToMachine} did not answer in time, so whether it was stored is not known. Check "
                          + $"'cc-secrets list' on {row.ToMachine} before asking again.");
        if (Refused(receiveResult, row.ToMachine, "store it") is { } receiveRefusal)
            return Failed(receiveRefusal + " Nothing was stored.");
        var stored = Read<ReceiveAnswer>(receiveResult!);
        if (stored is null)
            return Failed($"The Director on {row.ToMachine} answered in a shape the Gateway does not read. Check "
                          + $"'cc-secrets list' on {row.ToMachine}.");
        if (!stored.Ok)
            return Failed($"Not stored on {row.ToMachine}: {stored.Reason}");
        return new Outcome(true, $"Stored as {stored.Stored} on {row.ToMachine}.");
    }

    /// <summary>The sentence for a command that did not run to an answer, or null when it answered.</summary>
    private static string? Refused(DirectorCommandResult? result, string machine, string what)
    {
        if (result is null)
            return $"The Director on {machine} is not connected, so it could not {what}.";
        if (result.Status != DirectorCommandStatus.Ok)
            return $"The Director on {machine} could not {what}: {result.Error}";
        return null;
    }

    private static T? Read<T>(DirectorCommandResult result) where T : class
    {
        try
        {
            return string.IsNullOrEmpty(result.BodyJson) ? null : JsonSerializer.Deserialize<T>(result.BodyJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed class SendCommand
    {
        public string TransferId { get; set; } = "";
        public string Entry { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string FromMachine { get; set; } = "";
        public string ToMachine { get; set; } = "";
        public bool Replace { get; set; }
        public string ExpiresAtUtc { get; set; } = "";
        public string ReceiverPublicKey { get; set; } = "";
        public string ReceiverFingerprint { get; set; } = "";
        public string? AcceptedReceiverFingerprint { get; set; }
        public string ApprovedWhere { get; set; } = "";
        public string? ApprovalWords { get; set; }
    }

    internal sealed class ReceiveCommand
    {
        public string TransferId { get; set; } = "";
        public string Entry { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string FromMachine { get; set; } = "";
        public string ToMachine { get; set; } = "";
        public bool Replace { get; set; }
        public string ExpiresAtUtc { get; set; } = "";
        public string SenderPublicKey { get; set; } = "";
        public string SenderFingerprint { get; set; } = "";
        public string ReceiverFingerprint { get; set; } = "";
        public string Envelope { get; set; } = "";
        public string ApprovedWhere { get; set; } = "";
        public string? ApprovalWords { get; set; }
    }

    internal sealed class SendAnswer
    {
        public bool Ok { get; set; }
        public string? Reason { get; set; }
        public string? Envelope { get; set; }
        public string? SenderPublicKey { get; set; }
        public string? SenderFingerprint { get; set; }
    }

    internal sealed class ReceiveAnswer
    {
        public bool Ok { get; set; }
        public string? Reason { get; set; }
        public string? Stored { get; set; }
    }
}
