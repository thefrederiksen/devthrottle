using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The server-side network monitor (Network Diagnostics mission, Phase 1). On a timer it collects the
/// per-device Tailscale picture and threads each device through the approved <see cref="NetDiagDrift"/>
/// Decide-machine, logging drift state QUIETLY (no channels yet - P5 bolts the doorbell + owner email onto
/// the same state). It honors the monitor contracts the Architect set:
///   - tick cadence &lt;= 90s so K=3 lands near the 5-min duration floor (not far past it);
///   - OFFLINE peers are gated to Unknown BEFORE Decide (Decide takes CurrentDirect and does not check Online);
///   - direct-vs-relay comes from the ping-path parse (PeerDiag.Direct), NEVER the tailscale Relay field;
///   - HomeLanPresent for a relaying device = an ARP probe of its cached LAN IP resolving to the SAME cached
///     MAC (refreshed on every LAN-direct sighting), smoothed by a short positive-presence cache so a single
///     Android power-save ARP miss does not whipsaw a genuine active-use drift out of accrual.
///
/// <see cref="Tick"/> is the pure, dependency-injected core (collector + MAC resolver + clock) so the whole
/// per-device logic is unit-testable with no timer, no CLI, and no P/Invoke.
/// </summary>
public sealed class NetDiagMonitor : IDisposable
{
    /// <summary>Tick cadence. &lt;=90s so three consecutive bad ticks land near the 5-minute drift floor.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(75);
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a positive ARP+MAC presence result is trusted to smooth over a single probe blip.
    ///
    /// SAFETY INVARIANT (do NOT break): this MUST stay comfortably below <see cref="NetDiagDrift.MinDriftDuration"/>
    /// (the 5-minute drift floor). That gap is what makes "the user left the house" watertight: a departed
    /// phone ages out of this presence cache (~120s) well before drift could ever fire (K=3 over 5 min), so it
    /// can reach at most a brief Suspect and NEVER Drifted+alert. Bumping this past the drift floor would
    /// silently reopen the false-alert hole. Guarded by NetDiagMonitorTests.PresenceCacheWindow_StaysBelowDriftFloor.
    /// </summary>
    public static readonly TimeSpan PresenceCacheWindow = TimeSpan.FromSeconds(120);

    /// <summary>Baseline samples retained per device (bounded).</summary>
    public const int MaxSamplesPerDevice = 50;

    private sealed class DeviceState
    {
        public NetDiagDrift.MachineState Drift = new();
        public readonly List<NetDiagDrift.GoodSample> Samples = new();
        public string? CachedLanIp;
        public string? CachedMac;
        public DateTime? LastPresentUtc;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);

    private readonly Func<TailscaleDiagnostics.NetworkDiag> _collect;
    private readonly Func<string, string?> _resolveMac;
    private readonly NetDiagDeviceStore? _deviceStore;
    private readonly NetDiagRollupStore? _rollup;
    private readonly Action<string>? _onDrift;
    private readonly Action<string>? _onResolve;
    private Timer? _timer;

    /// <param name="collect">Gathers the current per-device picture (production: <see cref="TailscaleDiagnostics.Collect"/>).</param>
    /// <param name="resolveMac">Resolves a LAN IP to a MAC (production: <see cref="LanPresenceProbe.TryResolveMac"/>).</param>
    /// <param name="deviceStore">Optional durable store: seeds baselines + presence identity on startup (drift starts fresh).</param>
    /// <param name="rollup">Optional hourly quality rollup: each judged observation folds into it (home/away path split).</param>
    /// <param name="onDrift">P5: invoked with the device NAME on the rising edge into Drifted (persistent home relay).</param>
    /// <param name="onResolve">P5: invoked with the device NAME on observed recovery (Drifted -> Ok).</param>
    public NetDiagMonitor(
        Func<TailscaleDiagnostics.NetworkDiag> collect,
        Func<string, string?> resolveMac,
        NetDiagDeviceStore? deviceStore = null,
        NetDiagRollupStore? rollup = null,
        Action<string>? onDrift = null,
        Action<string>? onResolve = null)
    {
        _collect = collect ?? throw new ArgumentNullException(nameof(collect));
        _resolveMac = resolveMac ?? throw new ArgumentNullException(nameof(resolveMac));
        _deviceStore = deviceStore;
        _rollup = rollup;
        _onDrift = onDrift;
        _onResolve = onResolve;

        // Seed baselines + presence identity from the store so a restart does not re-warmup; the drift
        // MachineState is DELIBERATELY not restored - every device starts fresh at Unknown and re-accrues.
        if (deviceStore is not null)
            foreach (var (key, pd) in deviceStore.LoadAll())
            {
                var ds = new DeviceState { CachedLanIp = pd.LanIp, CachedMac = pd.Mac };
                ds.Samples.AddRange(pd.Samples);
                _devices[key] = ds;
            }
    }

    /// <summary>Start the periodic monitor. Safe to call once.</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => RunOnce(), null, StartupDelay, TickInterval);
    }

    private void RunOnce()
    {
        try
        {
            var diag = _collect();
            var decisions = Tick(diag, _resolveMac, DateTime.UtcNow);
            var nameByIp = diag.Peers
                .Where(p => !string.IsNullOrEmpty(p.TailscaleIp))
                .GroupBy(p => p.TailscaleIp!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
            foreach (var (device, d) in decisions)
            {
                var name = nameByIp.TryGetValue(device, out var n) && !string.IsNullOrEmpty(n) ? n : device;
                if (d.ShouldAlert)
                {
                    FileLog.Write($"[NetDiagMonitor] DRIFT device={name} (home relaying persistently) - firing alert channels");
                    try { _onDrift?.Invoke(name); } catch (Exception ex) { FileLog.Write($"[NetDiagMonitor] onDrift failed for {name}: {ex.Message}"); }
                }
                else if (d.ShouldResolve)
                {
                    FileLog.Write($"[NetDiagMonitor] RESOLVED device={name} - direct path restored");
                    try { _onResolve?.Invoke(name); } catch (Exception ex) { FileLog.Write($"[NetDiagMonitor] onResolve failed for {name}: {ex.Message}"); }
                }
                else if (d.Status is "suspect" or "drifted")
                {
                    FileLog.Write($"[NetDiagMonitor] device={name} status={d.Status}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NetDiagMonitor] tick failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Process one collected picture: update per-device baseline + presence cache and run each device
    /// through the Decide-machine. Pure w.r.t. the injected clock/resolver; mutates only the internal
    /// per-device state. Returns each device's decision (for logging).
    /// </summary>
    public IReadOnlyList<(string device, NetDiagDrift.Decision decision)> Tick(
        TailscaleDiagnostics.NetworkDiag diag, Func<string, string?> resolveMac, DateTime nowUtc)
    {
        var results = new List<(string, NetDiagDrift.Decision)>();
        bool tailscaleUp = diag.TailscaleAvailable
            && string.Equals(diag.BackendState, "Running", StringComparison.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var peer in diag.Peers)
            {
                var key = peer.TailscaleIp;
                if (string.IsNullOrEmpty(key)) continue;
                if (!_devices.TryGetValue(key, out var ds)) { ds = new DeviceState(); _devices[key] = ds; }

                // Contract: gate OFFLINE peers to Unknown BEFORE Decide (an offline phone must not accrue as
                // "bad"). A device we cannot currently see says nothing about home network quality.
                if (!peer.Online)
                {
                    ds.Drift = new NetDiagDrift.MachineState { State = NetDiagDrift.State.Unknown };
                    results.Add((key, new NetDiagDrift.Decision { Next = ds.Drift, Status = "unknown" }));
                    continue;
                }

                // Only judge peers with a CONFIRMED ping verdict. An online-but-not-pinged peer
                // (Direct==null: past the ping cap, or a ping that did not answer) has no authoritative
                // direct-vs-relay result, so gate it to Unknown rather than judging on the status-fallback
                // path (which could read DERP without a real ping confirming a relay).
                if (peer.Direct is null)
                {
                    ds.Drift = new NetDiagDrift.MachineState { State = NetDiagDrift.State.Unknown };
                    results.Add((key, new NetDiagDrift.Decision { Next = ds.Drift, Status = "unknown" }));
                    continue;
                }

                bool currentDirect = peer.Direct == true;
                bool isLanPath = IsLanPath(peer.Path);

                // On a LAN-direct sighting: refresh the baseline sample AND the (IP, MAC) cache - this is
                // exactly when CurAddr gives the current 192.168.x and we can (re)capture the MAC, so the
                // cache never goes stale while the device is home.
                if (currentDirect && isLanPath)
                {
                    var lanIp = ExtractIp(peer.Path!);
                    if (lanIp is not null)
                    {
                        ds.CachedLanIp = lanIp;
                        var mac = resolveMac(lanIp);
                        if (mac is not null) ds.CachedMac = mac;
                        ds.LastPresentUtc = nowUtc;
                    }
                    if (peer.LatencyMs is { } lat)
                    {
                        ds.Samples.Add(new NetDiagDrift.GoodSample(true, true, true, lat));
                        if (ds.Samples.Count > MaxSamplesPerDevice) ds.Samples.RemoveAt(0);
                    }
                    // Persist the refreshed baseline + presence identity so both survive a restart (drift
                    // state is never persisted). Best-effort: a store failure must not break the tick.
                    try { _deviceStore?.Save(key, ds.Samples, ds.CachedLanIp, ds.CachedMac); }
                    catch (Exception ex) { FileLog.Write($"[NetDiagMonitor] device-store save failed for {key}: {ex.Message}"); }
                }

                bool homePresent = ResolveHomePresence(ds, currentDirect && isLanPath, resolveMac, nowUtc);

                var obs = new NetDiagDrift.Observation
                {
                    TailscaleUp = tailscaleUp,
                    Baseline = NetDiagDrift.ComputeBaseline(ds.Samples),
                    CurrentDirect = peer.Direct,
                    CurrentIsLanPath = isLanPath,
                    CurrentLatencyMs = peer.LatencyMs,
                    HomeLanPresent = homePresent,
                    NowUtc = nowUtc,
                };

                var decision = NetDiagDrift.Decide(obs, ds.Drift);
                ds.Drift = decision.Next;

                // Fold this judged observation into the hourly quality rollup (home/away split on the
                // MEASURED path). The monitor has no throughput numbers, so down/up are null. Best-effort.
                //
                // TENANT: explicitly Local, and that is correct HERE and only here. This monitor is
                // constructed by GatewayHost inside `if (!Hosted)` (the mode read once into its options) - it shells out to the
                // tailscale command-line tool, which the hosted container image does not carry, and a hosted
                // Gateway has no tailnet to diagnose. On self-host, Local is the only tenant there is. If this
                // monitor is ever constructed on hosted, this line becomes wrong and the owning tenant must be
                // passed in rather than assumed; it is written literally, at the fold, so that change is
                // visible here instead of hidden behind a defaulted parameter.
                try { _rollup?.Fold(Core.Tenancy.TenantId.Local, nowUtc, peer.LatencyMs, peer.Direct, isLanPath, null, null); }
                catch (Exception ex) { FileLog.Write($"[NetDiagMonitor] rollup fold failed for {key}: {ex.Message}"); }

                results.Add((key, decision));
            }
        }

        return results;
    }

    // Positive physical-presence with MAC-identity match + short smoothing cache.
    private bool ResolveHomePresence(DeviceState ds, bool currentlyLanDirect, Func<string, string?> resolveMac, DateTime nowUtc)
    {
        if (currentlyLanDirect) return true; // trivially home; also refreshed LastPresentUtc above
        if (ds.CachedLanIp is null || ds.CachedMac is null) return false;

        var mac = resolveMac(ds.CachedLanIp);
        if (mac is not null && string.Equals(mac, ds.CachedMac, StringComparison.OrdinalIgnoreCase))
        {
            ds.LastPresentUtc = nowUtc;
            return true;
        }
        // Smooth over a single ARP miss (Android power-save) with a short positive-presence cache: if we
        // confirmed presence very recently, trust it so a genuine active-use drift is not whipsawed out of
        // accrual. A device that truly left stops refreshing this and ages out within the window.
        return ds.LastPresentUtc is { } last && (nowUtc - last) <= PresenceCacheWindow;
    }

    /// <summary>A path is LAN-direct when it is an "a.b.c.d:port" in a private range; "DERP(...)" / null are not.</summary>
    public static bool IsLanPath(string? path)
    {
        var ip = ExtractIp(path);
        if (ip is null) return false;
        var b = ip.Split('.');
        if (b.Length != 4 || !int.TryParse(b[0], out var o0) || !int.TryParse(b[1], out var o1)) return false;
        if (o0 == 10) return true;
        if (o0 == 172 && o1 >= 16 && o1 <= 31) return true;
        if (o0 == 192 && o1 == 168) return true;
        if (o0 == 169 && o1 == 254) return true;
        return false;
    }

    /// <summary>Strip the ":port" from a "192.168.1.15:52091" path; null for "DERP(...)" / empty.</summary>
    public static string? ExtractIp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("DERP", StringComparison.OrdinalIgnoreCase)) return null;
        var colon = path.LastIndexOf(':');
        var ip = colon > 0 ? path[..colon] : path;
        return ip.Count(c => c == '.') == 3 ? ip : null;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
