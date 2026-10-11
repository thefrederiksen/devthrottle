using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1848: the prompt log is TENANT-SCOPED on the hosted Gateway, proven over real HTTP with two accounts.
///
/// This is the CONTENT-disclosure surface. Before this, <c>GET /prompts</c> did not take an HttpContext at all -
/// so it could not resolve a request tenant even in principle - and it answered fleet-globally: one account
/// read every other account's full prompt TEXT. This drives the REAL mapped endpoints through the REAL auth
/// middleware, exactly as SessionServingReadIsolationTests does for the cockpit read path.
///
/// THE PRODUCTION LINES THESE TESTS PROTECT (each revert-provable - revert it, watch these go red):
///   - <c>PromptEndpoints.ResolveTenant</c>, and the <c>store.Read(tenant.Value, ...)</c> /
///     <c>store.Append(tenant.Value, ...)</c> calls in the two /prompts handlers. Revert the resolution to a
///     fixed TenantId.Local and both accounts share one partition, so "B never sees A's text" goes RED and the
///     403 deny collapses to a 200.
///   - <c>GatewayPromptLog.DirectoryFor</c> - the ONE helper every read and write path goes through, which is
///     what makes the partition structural rather than conventional. Return the root directory for every
///     tenant and the same assertion goes RED.
/// </summary>
public sealed class PromptLogTenantIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";

    // The thing one account must never be able to read out of the other.
    private const string SecretTextA = "alpha-account-secret-prompt-text";
    private const string SecretTextB = "bravo-account-secret-prompt-text";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "cc-prompt-iso-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // The prompt log is pinned into a throwaway directory; it otherwise defaults to the running user's real one.
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _tempDir,
            workListsPath: Path.Combine(_tempDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_tempDir, "snooze", "snooze.json"),
            promptLogPath: Path.Combine(_tempDir, "prompt-log"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts: two device keys, each bound to its OWN tenant, plus one registered-but-unbound key.
        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        // Bound to tenants MINTED BY THE REAL REGISTRY, exactly as POST /devices/enroll-hosted does, rather
        // than to invented strings. That matters here specifically: the prompt log turns a tenant id into a
        // DIRECTORY NAME and now enforces the real minted shape, so a test binding a made-up id would be
        // testing a partition production can never create.
        var tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task One_account_never_reads_another_accounts_prompt_text()
    {
        // BIDIRECTIONAL, on purpose. An earlier version of this test asserted only that B could not see A's
        // text. An implementation that leaked A's read while keeping B correctly scoped would have passed it
        // unnoticed - and a leak conditional on tenant, key or enrollment order hides in exactly the
        // direction you did not check. Every absence claim in this work is checked both ways.
        var today = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await PostPrompt(_keyA, SecretTextA, today)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostPrompt(_keyB, SecretTextB, today)).StatusCode);

        var bodyA = await ReadPromptsBody(_keyA, today);
        var bodyB = await ReadPromptsBody(_keyB, today);

        // Positive control in FRONT of each absence claim: each account really does read its own text back.
        // Without these, "A cannot see B's text" would also hold if the read returned nothing at all.
        Assert.Contains(SecretTextA, bodyA);
        Assert.Contains(SecretTextB, bodyB);

        // The absence claims, both directions. This is the whole defect: neither handler took an HttpContext,
        // so neither could resolve a tenant even in principle and this body was every account's prompt TEXT.
        Assert.DoesNotContain(SecretTextB, bodyA);   // A cannot see B
        Assert.DoesNotContain(SecretTextA, bodyB);   // B cannot see A

        // And neither read merely FILTERED a shared set down: each account sees exactly its own one record,
        // so nothing of the other's is present in any form - not the text, not the record.
        using var docA = JsonDocument.Parse(bodyA);
        using var docB = JsonDocument.Parse(bodyB);
        Assert.Equal(1, docA.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, docB.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void A_failed_read_never_writes_the_raw_tenant_id_to_the_log()
    {
        // The prompt-log partition IS the tenant's account id - it is a directory name - so any failure that
        // logs a path, or an exception message containing one, prints account identifiers into a log that is
        // otherwise free of them. Every other tenant-bearing log line uses the hashed form; these two did not.
        //
        // Driven behaviourally through the REAL log writer via its test seam, and through a REAL failing read
        // - a directory standing where the daily file should be, so the file read genuinely throws - rather
        // than by unit-testing the redaction helper, which would prove the helper and not the call site.
        //
        // Revert-prove: put {path} and the raw {ex.Message} back into the READ catch in GatewayPromptLog and
        // this goes RED on the raw tenant id appearing in the log. An earlier version of this comment said
        // "either catch block", which was FALSE - review reverted only the Append catch and all three tests
        // still passed, so that second leak could have regressed unnoticed. The Append catch has its own
        // test below now; a comment claiming coverage is not coverage.
        var root = Path.Combine(Path.GetTempPath(), "cc-plog-" + Guid.NewGuid().ToString("N"));
        var tenant = new CcDirector.Core.Tenancy.TenantId("11111111-2222-3333-4444-555555555555");
        try
        {
            var log = new CcDirector.Gateway.Prompts.GatewayPromptLog(root);
            var day = DateTime.UtcNow;
            // Make the daily file exist but be genuinely unreadable - another process holding it exclusively,
            // which is the ordinary way a real read fails. A directory in its place would NOT do: Read checks
            // File.Exists first and would skip the day without ever attempting a read, so nothing would be
            // logged and this test would pass while proving nothing. The positive control below caught exactly
            // that on the first attempt.
            var file = log.FileFor(tenant, day);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}");

            IReadOnlyList<string> lines;
            using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var capture = CcDirector.Core.Utilities.FileLog.RedirectForTests())
            {
                log.Read(tenant, day, day);
                lines = capture.DrainAndReadLines();
            }

            // Positive control FIRST: the failure really was logged. Without this, "the id is absent" would
            // also hold if nothing had been written at all - which is the shape of a vacuous privacy check.
            var failures = lines.Where(l => l.Contains("Read FAILED", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(failures);

            // The id itself never appears, in the path or inside the exception message.
            Assert.DoesNotContain(lines, l => l.Contains(tenant.Value, StringComparison.OrdinalIgnoreCase));

            // And the line is still USEFUL - it names the partition in hashed form, so a failure is
            // diagnosable. Redacted, not silenced.
            Assert.Contains(failures, l => l.Contains(tenant.ToLogString(), StringComparison.Ordinal));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void A_failed_append_never_writes_the_raw_tenant_id_to_the_log()
    {
        // The SECOND raw-account-id leak, and it existed unprotected while a comment claimed otherwise. Append
        // swallows its failure by design - a logging failure must not fail a Director's push - so the only
        // trace is the log line, and that line carried an exception message containing the full path, which on
        // hosted IS the account id.
        //
        // Driven the same way as the read proof: a real exclusive lock on the tenant's own daily file, so the
        // real Append catch runs, through the real log writer.
        //
        // Revert-prove: put the raw {ex.Message} back in the Append catch and this goes RED on the raw id.
        var root = Path.Combine(Path.GetTempPath(), "cc-plog-" + Guid.NewGuid().ToString("N"));
        var tenant = new CcDirector.Core.Tenancy.TenantId("11111111-2222-3333-4444-555555555555");
        try
        {
            var log = new CcDirector.Gateway.Prompts.GatewayPromptLog(root);
            var day = DateTime.UtcNow;
            var file = log.FileFor(tenant, day);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");

            IReadOnlyList<string> lines;
            var written = 0;
            using (var hold = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
            using (var capture = CcDirector.Core.Utilities.FileLog.RedirectForTests())
            {
                written = log.Append(tenant, new[] { new PromptRecord
                {
                    TsUtc = day,
                    Machine = "MA",
                    SessionId = "session-1",
                    ContextId = "ctx-1",
                    RepoPath = "/repo",
                    Agent = "ClaudeCode",
                    Role = "user",
                    TimestampFromAgent = true,
                    CharCount = 5,
                    WordCount = 1,
                    Text = "hello",
                } });
                lines = capture.DrainAndReadLines();
            }

            // Positive control FIRST: the append really did fail and really did log. Without this, "the id is
            // absent" would also hold if the write had quietly succeeded.
            Assert.Equal(0, written);
            var failures = lines.Where(l => l.Contains("Append FAILED", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(failures);

            Assert.DoesNotContain(lines, l => l.Contains(tenant.Value, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(failures, l => l.Contains(tenant.ToLogString(), StringComparison.Ordinal));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void A_tenant_id_that_is_not_a_minted_account_cannot_name_a_partition()
    {
        // Traversal canary. The validator used to be a character allow-list that accepted "..", and
        // Path.Combine(root, "tenants", "..") canonicalizes to exactly root - the LOCAL partition. So a tenant
        // id of ".." would have read and written the local tenant's prompt text. The dangerous values here are
        // STRUCTURAL, not exotic, which is why a "looks harmless" character rule is not a validator.
        //
        // Hosted mints GUIDs today, but this is a storage boundary taking the general TenantId type and device
        // bindings persist arbitrary strings, so the boundary enforces its own domain rather than trusting its
        // callers.
        var log = new CcDirector.Gateway.Prompts.GatewayPromptLog(
            Path.Combine(Path.GetTempPath(), "cc-plog-" + Guid.NewGuid().ToString("N")));

        // NOTE the verbatim string on the separator case. It was written "a\b" first, which in C# is a
        // BACKSPACE character, not a backslash - so that canary tested a control character and would have
        // passed however broken the separator handling was. A dead canary looks exactly like coverage.
        foreach (var bad in new[]
        {
            "..", ".", "a/b", @"a\b", "not-a-guid", "tenants",
            // The casing alias. The registry mints canonical LOWERCASE guids and the tenants table uses a
            // case-sensitive collation, so this is a DIFFERENT IDENTITY to the database - while Windows and
            // Azure Files name the SAME directory as its lowercase twin. Accepting it is one tenant reading
            // another's prompt text, with no special character involved at all.
            // NOTE the letters. This canary was first written against 1111...-5555, which is ALL DIGITS, so
            // ToUpperInvariant was a no-op and the "uppercase" case was really the valid lowercase id - the
            // test caught it by reporting that id as accepted. A casing canary needs a value that HAS a case.
            "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE",
            "aaaaaaaa-bbbb-4ccc-8ddd-EEEEEEEEEEEE",
            // Other spellings of a real guid that are not the minted form.
            "{11111111-2222-3333-4444-555555555555}",
            "11111111222233334444555555555555",
            // The reserved system tenant owns no prompt text, so it gets no partition rather than a folder.
            CcDirector.Core.Tenancy.TenantId.System.Value,
        })
        {
            var ex = Record.Exception(() => log.DirectoryFor(new CcDirector.Core.Tenancy.TenantId(bad)));
            Assert.True(ex is ArgumentException, $"tenant id {bad} was ACCEPTED as a partition name");
        }

        // Positive controls, so this is not passing because DirectoryFor refuses everything: the two shapes
        // that ARE real still work, and land in different places.
        var minted = new CcDirector.Core.Tenancy.TenantId("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        Assert.NotEqual(log.DirectoryFor(CcDirector.Core.Tenancy.TenantId.Local),
                        log.DirectoryFor(minted));
        Assert.Contains(minted.Value, log.DirectoryFor(minted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompts_deny_a_device_key_with_no_bound_tenant()
    {
        // Deny-by-default, on BOTH verbs: a tenant-unbound hosted credential is rejected at authentication
        // and never falls back to Local for either reads or writes.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadPrompts(_keyUnbound, DateTime.UtcNow)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostPrompt(_keyUnbound, "should never land", DateTime.UtcNow)).StatusCode);
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> ReadPrompts(string deviceKey, DateTime day)
        => Get($"prompts?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}", deviceKey);

    private async Task<string> ReadPromptsBody(string deviceKey, DateTime day)
    {
        var resp = await ReadPrompts(deviceKey, day);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private Task<HttpResponseMessage> PostPrompt(string deviceKey, string text, DateTime tsUtc)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "prompts")
        {
            Content = JsonContent.Create(new PromptIngestRequest
            {
                Records = new List<PromptRecord>
                {
                    new()
                    {
                        TsUtc = tsUtc,
                        Machine = "M",
                        SessionId = "s",
                        Agent = "ClaudeCode",
                        Role = "user",
                        TimestampFromAgent = true,
                        CharCount = text.Length,
                        WordCount = 1,
                        Text = text,
                    },
                },
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

}
