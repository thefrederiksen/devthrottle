using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.GatewayLoad;

/// <summary>
/// What one simulated account does. Every rate is per Director unless it says otherwise; where a rate came from is
/// written beside it, and anything not measured is marked ESTIMATE.
/// </summary>
internal sealed record LoadProfile(
    string Name,
    int Directors,
    int SessionsPerDirector,
    double WorkingShare,              // share of sessions that are taking turns at any moment (ESTIMATE)
    TimeSpan TurnEvery,               // a working session ends a turn this often (ESTIMATE)
    TimeSpan IdleDeltaEvery,          // an idle session still sends a change this often (ESTIMATE)
    TimeSpan SessionListEvery,        // Director GET /sessions
    TimeSpan CatalogEvery,            // Director skills (twice), workflows, injected text - measured 60 s cycle
    TimeSpan TriggersEvery,           // Director GET /directors/{id}/triggers - measured 120 an hour per Director
    TimeSpan AccountStatusEvery,      // Director GET /account/status - measured 120 an hour per Director
    TimeSpan PhonePollEvery,          // phone GET /sessions while the app is open - measured 2 s
    double PhoneOpenShare)            // share of the day the phone app is open (ESTIMATE for the light user)
{
    /// <summary>
    /// A fleet like the owner's, from the Gateway traffic meter's night rows on 4 October 2026 for one account with
    /// three Directors: Director session-list reads AFTER the desktop release that carries pull request 3519 (one per
    /// five-minute repository rescan instead of one per repository), and the catalog reads answered "not changed".
    /// </summary>
    public static LoadProfile Fleet(bool released) => new("fleet", 3, 10, 0.3,
        TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5),
        released ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(8.3),   // today: 1,298 an hour across 3 Directors
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(2), 8.0 / 24);

    /// <summary>A light user, as the owner defined it on 4 October 2026 (a DEFINITION, not a measurement): one
    /// computer, five sessions, the phone open about three hours a day, voice on for one session. Voice is not
    /// simulated here - this Gateway has no narration provider - so its clip downloads are added in the report.</summary>
    public static LoadProfile Light(bool released) => Fleet(released) with
    {
        Name = "light", Directors = 1, SessionsPerDirector = 5, PhoneOpenShare = 3.0 / 24,
    };
}

/// <summary>"drive": connect the first N enrolled accounts with a profile and hold them for a while.</summary>
internal static class DriveMode
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string runDir, LoadProfile profile, int count, TimeSpan hold)
    {
        var accounts = JsonSerializer.Deserialize<LoadAccounts>(File.ReadAllText(Path.Combine(runDir, HostMode.AccountsFile)))
            ?? throw new InvalidOperationException("accounts.json is empty");
        if (count > accounts.Accounts.Count)
        {
            Console.Error.WriteLine($"ERROR: {count} accounts asked for, the host enrolled {accounts.Accounts.Count}.");
            return 2;
        }
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "session-template.json"));
        var baseUrl = $"http://127.0.0.1:{accounts.Port}";
        // Real clients ask for compressed answers, and the meter counts bytes after compression, so this must too.
        using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(30),
        };
        using var stop = new CancellationTokenSource();
        var begun = DateTime.UtcNow;

        // The phone is open for a share of the day; in a steady run that is the same share of the accounts at once.
        // A whole number of phones cannot hit the share exactly at every step, so the summary charges phone traffic
        // per OPEN phone times the share, never per account as it happened to fall; at least one phone is open
        // whenever the share is above zero, so there is always a phone to measure.
        var phonesOpen = profile.PhoneOpenShare > 0 ? Math.Max(1, (int)Math.Round(count * profile.PhoneOpenShare)) : 0;
        Console.WriteLine($"[drive] {count} {profile.Name} account(s), {profile.Directors} Director(s) x {profile.SessionsPerDirector} " +
                          $"session(s) each, {phonesOpen} phone(s) open, holding {hold.TotalMinutes:0} minute(s)");

        var running = new List<Task>();
        var connections = new List<HubConnection>();
        for (var i = 0; i < count; i++)
        {
            var account = accounts.Accounts[i];
            for (var d = 0; d < profile.Directors; d++)
            {
                var directorId = $"{account.Subject}-dir-{d}";
                var conn = await ConnectDirectorAsync(baseUrl, account.DirectorKeys[d], directorId, $"LOAD{i:D3}D{d}");
                connections.Add(conn);
                var sessions = Enumerable.Range(0, profile.SessionsPerDirector)
                    .Select(_ => NewSession(template, directorId, $"LOAD{i:D3}D{d}")).ToArray();
                var director = new SimulatedDirector(conn, sessions);
                await director.PushSnapshotAsync();
                running.Add(director.RunTurnsAsync(profile, stop.Token));
                running.Add(PollAsync(http, account.DirectorKeys[d], "sessions", profile.SessionListEvery, stop.Token));
                running.Add(CatalogAsync(http, account.DirectorKeys[d], profile.CatalogEvery, stop.Token));
                // A new account has factory agents off, so this is answered 404 - as it is for a real new account,
                // whose Director polls it all the same. The 404 is the traffic being measured, not a fault.
                running.Add(PollAsync(http, account.DirectorKeys[d], $"directors/{directorId}/triggers", profile.TriggersEvery,
                    stop.Token, notFoundExpected: true));
                running.Add(PollAsync(http, account.DirectorKeys[d], "account/status", profile.AccountStatusEvery, stop.Token));
            }
            if (i < phonesOpen)
                running.Add(PollAsync(http, account.PhoneKey, "sessions", profile.PhonePollEvery, stop.Token, useTags: true));
        }
        var connected = DateTime.UtcNow;
        Console.WriteLine($"[drive] all connected at {connected:o}");

        await Task.Delay(hold);
        var collected = await RequestLiveSampleAsync(runDir);
        stop.Cancel();
        var finished = Task.WhenAll(running);
        if (await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(60))) != finished)
            throw new TimeoutException("the simulated Directors and pollers did not stop within 60 s of the end of the hold");
        await finished;
        var ended = DateTime.UtcNow;

        await ReportTrafficAsync(http, accounts.Accounts.Take(count).ToList(), Path.Combine(runDir, TrafficFile));
        File.WriteAllText(Path.Combine(runDir, RunFile), JsonSerializer.Serialize(new DriveRun(profile.Name, count, phonesOpen,
            profile.PhoneOpenShare, begun, connected, ended, collected, Errors), new JsonSerializerOptions { WriteIndented = true }));
        foreach (var conn in connections) await conn.DisposeAsync();
        Console.WriteLine($"[drive] done at {ended:o}, {Errors} error(s)");
        return 0;
    }

    public const string TrafficFile = "traffic.json";
    public const string RunFile = "drive.json";

    /// <summary>Failed reads and pushes during the hold. Each is printed; a loop carries on after one, so the load
    /// stays what the profile says, and the count is saved with the run so the summary can refuse a lossy step.</summary>
    private static int _errors;
    private static int Errors => Volatile.Read(ref _errors);

    private static void Failed(string what, Exception ex)
    {
        Interlocked.Increment(ref _errors);
        Console.Error.WriteLine($"[drive] {what} FAILED: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Asks the host for one sample after a full collection, while every account is still connected, and
    /// waits for it. Returns whether it arrived; a host that does not answer within 60 s is a failed step.</summary>
    private static async Task<bool> RequestLiveSampleAsync(string runDir)
    {
        var request = Path.Combine(runDir, HostMode.CollectFile);
        File.WriteAllText(request, DateTime.UtcNow.ToString("o"));
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (File.Exists(request))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("the host did not take the live-memory sample within 60 s");
            await Task.Delay(500);
        }
        return File.Exists(Path.Combine(runDir, HostMode.CollectedFile));
    }

    private static async Task<HubConnection> ConnectDirectorAsync(string baseUrl, string key, string directorId, string machine)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(key))
            .AddMessagePackProtocol()
            .Build();
        // The Gateway may ask a Director to do something; a simulated one does nothing and says so.
        conn.On<DirectorCommand, DirectorCommandResult>("Command",
            cmd => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"load test Director: no verb {cmd.Verb}"));
        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new DirectorStreamHello
        {
            DirectorId = directorId, Version = "load", MachineName = machine, User = "load", Pid = 1, StartedAt = DateTime.UtcNow,
        });
        return conn;
    }

    /// <summary>A session row the size and shape of a real one (the scrubbed template), with its own identity.</summary>
    private static SessionDto NewSession(string template, string directorId, string machine)
    {
        var s = JsonSerializer.Deserialize<SessionDto>(template, Web) ?? throw new InvalidOperationException("bad session template");
        s.SessionId = Guid.NewGuid().ToString();
        s.DirectorId = directorId;
        s.MachineName = machine;
        s.ActivityState = "WaitingForInput";
        s.CreatedAt = DateTime.UtcNow;
        s.LastActivityAt = DateTime.UtcNow;
        return s;
    }

    /// <summary>A polled read, sent every <paramref name="every"/> with a little random spread so the accounts do not
    /// all arrive in the same millisecond. With <paramref name="useTags"/> it sends back the last tag, as a browser does.</summary>
    private static async Task PollAsync(HttpClient http, string key, string path, TimeSpan every, CancellationToken ct,
        bool useTags = false, bool notFoundExpected = false)
    {
        string? tag = null;
        await Delay(TimeSpan.FromMilliseconds(Random.Shared.Next((int)Math.Min(every.TotalMilliseconds, 30000))), ct);
        while (!ct.IsCancellationRequested)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (useTags && tag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", tag);
            try
            {
                using var response = await http.SendAsync(request, ct);
                await response.Content.LoadIntoBufferAsync(ct);
                if (useTags && response.StatusCode == HttpStatusCode.OK) tag = response.Headers.ETag?.ToString();
                if ((int)response.StatusCode >= 400 && !(notFoundExpected && response.StatusCode == HttpStatusCode.NotFound))
                    Failed($"GET /{path}", new HttpRequestException($"answered {(int)response.StatusCode}"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Failed($"GET /{path}", ex); }
            await Delay(every, ct);
        }
    }

    /// <summary>The Director's one-minute catalog cycle: the skill list twice (index and store), the workflow list and
    /// the injected text, each sending back the tag it holds (the behaviour of pull request 3531).</summary>
    private static Task CatalogAsync(HttpClient http, string key, TimeSpan every, CancellationToken ct) => Task.WhenAll(
        PollAsync(http, key, "gateway/skills", every, ct, useTags: true),
        PollAsync(http, key, "gateway/skills", every, ct, useTags: true),
        PollAsync(http, key, "gateway/workflows", every, ct, useTags: true),
        PollAsync(http, key, "gateway/injected-text", every, ct, useTags: true));

    private static async Task Delay(TimeSpan span, CancellationToken ct)
    {
        try { await Task.Delay(span, ct); }
        catch (OperationCanceledException) { /* the hold is over */ }
    }

    /// <summary>Each account's own traffic meter rows, read with its own key, saved for the summary.</summary>
    private static async Task ReportTrafficAsync(HttpClient http, List<LoadAccount> accounts, string file)
    {
        var all = new Dictionary<string, JsonElement>();
        foreach (var account in accounts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "diag/traffic");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.PhoneKey);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            all[account.Subject] = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
        File.WriteAllText(file, JsonSerializer.Serialize(all));
    }

    /// <summary>One simulated Director: its sessions, and the changes it pushes as they take turns.</summary>
    private sealed class SimulatedDirector(HubConnection conn, SessionDto[] sessions)
    {
        private long _sequence;

        public Task PushSnapshotAsync() => conn.InvokeAsync("PushSnapshot", ++_sequence, sessions);

        public Task RunTurnsAsync(LoadProfile profile, CancellationToken ct)
        {
            var working = (int)Math.Round(sessions.Length * profile.WorkingShare);
            return Task.WhenAll(sessions.Select((s, i) => i < working
                ? TakeTurnsAsync(s, profile.TurnEvery, ct)
                : IdleAsync(s, profile.IdleDeltaEvery, ct)));
        }

        /// <summary>Working for most of the interval, then waiting: one turn end per interval.</summary>
        private async Task TakeTurnsAsync(SessionDto s, TimeSpan every, CancellationToken ct)
        {
            await Delay(TimeSpan.FromMilliseconds(Random.Shared.Next((int)every.TotalMilliseconds)), ct);
            while (!ct.IsCancellationRequested)
            {
                await PushAsync(s, "Working", ct);
                await Delay(every * 0.8, ct);
                await PushAsync(s, "WaitingForInput", ct);
                await Delay(every * 0.2, ct);
            }
        }

        private async Task IdleAsync(SessionDto s, TimeSpan every, CancellationToken ct)
        {
            await Delay(TimeSpan.FromMilliseconds(Random.Shared.Next((int)every.TotalMilliseconds)), ct);
            while (!ct.IsCancellationRequested)
            {
                await PushAsync(s, "WaitingForInput", ct);
                await Delay(every, ct);
            }
        }

        private async Task PushAsync(SessionDto s, string state, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            s.ActivityState = state;
            s.LastActivityAt = DateTime.UtcNow;
            long sequence;
            lock (this) sequence = ++_sequence;
            // Not the hold's token: a push already begun is allowed to finish, so the Gateway never sees a
            // half-sent one. But a push the Gateway never answers must not hang the run, so it gets 30 s.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await conn.InvokeAsync("PushDelta", sequence, s, limit.Token); }
            catch (Exception ex)
            {
                // Any failure (a timeout, a refusal, a dropped connection) is counted and the session carries on;
                // the summary refuses a step with any failure, so nothing here is hidden.
                Failed($"PushDelta {s.SessionId}", ex);
            }
        }
    }
}

/// <summary>What the driver did in one step, for the summary: when it began connecting, when every account was
/// connected, when the hold ended, how many phones were open against the profile's share, whether the live-memory
/// sample was taken, and how many reads or pushes failed.</summary>
internal sealed record DriveRun(string Profile, int Accounts, int PhonesOpen, double PhoneOpenShare, DateTime BegunUtc,
    DateTime ConnectedUtc, DateTime EndedUtc, bool LiveSampleTaken, int Errors);
