using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The Directors on the other end of a license command, for the sign-in tests (License Switching, contract section 8).
/// It stands where the host's <c>SendCommandAsync</c> stands, and behaves as that does: a command reaches a Director
/// only when a tenant's scope is in effect AND that Director is connected in THAT tenant - otherwise nothing is sent
/// and the answer is null. So a test that reaches another tenant's Director fails here as it would on the host.
/// Every send is recorded with the scope it was made in and the wait it was given.
/// </summary>
internal sealed class FakeLicenseDirectors
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>One send: the tenant scope in effect, the Director, the verb, the payload, the wait and the machine named.</summary>
    public sealed record Sent(TenantId? Scope, string DirectorId, string Verb, object Payload, TimeSpan Timeout, string MachineName);

    private readonly AsyncLocal<TenantId?> _scope = new();
    private readonly HashSet<(TenantId Tenant, string DirectorId)> _connected = new();

    public List<Sent> Sends { get; } = new();

    private readonly List<(Func<Sent, bool> Match, TaskCompletionSource<Sent> Seen)> _waiters = new();

    /// <summary>The first send that matches, whether it was already made or is made later. A test waits on this and
    /// never on a clock; the limit only ends a test whose send never comes.</summary>
    public Task<Sent> WaitFor(Func<Sent, bool> match)
    {
        lock (Sends)
        {
            if (Sends.FirstOrDefault(match) is { } already)
                return Task.FromResult(already);
            var seen = new TaskCompletionSource<Sent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, seen));
            return seen.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>What a connected Director answers. By default it refuses the verb, as a Director that does not know it would.</summary>
    public Func<Sent, Task<DirectorCommandResult?>> Answer { get; set; } =
        sent => Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {sent.Verb}"));

    public void Connect(TenantId tenant, string directorId)
    {
        lock (_connected) _connected.Add((tenant, directorId));
    }

    public void Disconnect(TenantId tenant, string directorId)
    {
        lock (_connected) _connected.Remove((tenant, directorId));
    }

    public bool IsConnected(TenantId tenant, string directorId)
    {
        lock (_connected) return _connected.Contains((tenant, directorId));
    }

    public IDisposable EnterScope(TenantId tenant)
    {
        var before = _scope.Value;
        _scope.Value = tenant;
        return new Restore(() => _scope.Value = before);
    }

    public Task<DirectorCommandResult?> SendAsync(string directorId, string verb, object payload, TimeSpan timeout, string machineName,
        CancellationToken ct)
    {
        var sent = new Sent(_scope.Value, directorId, verb, payload, timeout, machineName);
        lock (Sends)
        {
            Sends.Add(sent);
            foreach (var waiter in _waiters.Where(w => w.Match(sent)).ToList())
            {
                _waiters.Remove(waiter);
                waiter.Seen.SetResult(sent);
            }
        }
        return _scope.Value is { } tenant && IsConnected(tenant, directorId)
            ? Answer(sent)
            : Task.FromResult<DirectorCommandResult?>(null);
    }

    public void AnswerWith(Func<Sent, DirectorCommandResult?> answer) => Answer = sent => Task.FromResult(answer(sent));

    public List<Sent> Of(string verb)
    {
        lock (Sends) return Sends.Where(s => s.Verb == verb).ToList();
    }

    public static DirectorCommandResult Ok(object body) => DirectorCommandResult.Success(JsonSerializer.Serialize(body, Web));

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

internal static class LicenseSignInTestData
{
    /// <summary>A public half as a Director says it in Hello: base64 of 32 bytes. Not a real key.</summary>
    public static readonly string PublicHalf = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    public const string SignIn1 = "11111111111111111111111111111111";
    public const string SignIn2 = "22222222222222222222222222222222";

    /// <summary>A report from a machine that holds no sign-in at all - a machine that only approves (contract 2.4).</summary>
    public static LicenseUsageReport HoldsNothing(string machine, DateTime sentAt) => Report(machine, sentAt);
}
