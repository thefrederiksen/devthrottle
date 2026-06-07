using System.Runtime.CompilerServices;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>Outcome of one rung of the troubleshooting ladder (issue #223).</summary>
public enum RungStatus
{
    Pass,
    Fail,
    /// <summary>Informational - never the root cause verdict (the firewall rung, versions).</summary>
    Info,
    /// <summary>Not run because an earlier rung already failed - the ladder stops at the first
    /// failing rung; that rung IS the diagnosis.</summary>
    Skipped,
}

/// <summary>One rung's result: what was checked, what was found, and the exact fix.</summary>
public sealed class LadderRung
{
    public string Title { get; init; } = "";
    public RungStatus Status { get; init; }
    /// <summary>What was checked and what was found, one or two short lines.</summary>
    public string Found { get; init; } = "";
    /// <summary>The exact command or action that fixes this rung. Null when passing/info.</summary>
    public string? Fix { get; init; }

    /// <summary>True when the Director can apply <see cref="Fix"/> itself (the serve-mapping
    /// rung: the self-provisioner's own EnsureMapping). The dialog offers "Fix it now";
    /// nothing is ever auto-applied without that click.</summary>
    public bool CanAutoFix { get; init; }
}

/// <summary>
/// The Gateway-connectivity troubleshooting ladder (issue #223): the exact diagnostic
/// sequence a human had to run by hand during the SORENLAPTOP incident, encoded in the
/// CORRECT order - firewall LAST, because with Tailscale Serve active a TCP timeout means
/// "no serve mapping", never "firewall" (Serve answers inside tailscaled before the
/// Windows firewall is consulted). The ladder stops at the first failing rung: that rung
/// is the root cause; everything after it is noise.
///
/// Rungs:
///   1. Is Tailscale up?            (BackendState == Running)
///   2. Is the serve mapping there? (serve status carries --https=&lt;port&gt; -&gt; loopback)
///   3. Does the local listener answer? (GET 127.0.0.1:&lt;port&gt;/healthz)
///   4. Does the advertised URL answer AS THIS DIRECTOR? (outside-in healthz + id match)
///   5. Versions                    (info: this build vs the Gateway's)
///   6. Firewall                    (info: why it is almost never the cause)
///
/// Runs on demand from the troubleshooting dialog; each rung is yielded as it completes
/// so the dialog fills in live (responsive-UI rule). All checks are read-only except
/// nothing - fixes are offered, never auto-applied.
/// </summary>
public sealed class GatewayConnectivitySelfTest
{
    private readonly int _port;
    private readonly string _directorId;
    private readonly string? _advertisedEndpoint;
    private readonly string? _gatewayUrl;
    private readonly string? _provisionerLastError;

    /// <summary>Test seam: read-only tailscale CLI calls. Production: the real CLI.</summary>
    internal Func<string, (bool ok, string stdout, string message)> Runner { get; set; } = TailscaleCli.Run;

    /// <summary>Test seam: CLI presence.</summary>
    internal Func<bool> CliAvailable { get; set; } = () => TailscaleCli.IsAvailable;

    /// <summary>Test seam: HTTP GET returning (2xx ok, body-or-error). Production: real HTTP.</summary>
    internal Func<string, CancellationToken, Task<(bool ok, string detail)>> HttpProbe { get; set; } = ProbeHttpAsync;

    /// <param name="advertisedEndpoint">The URL the Gateway dials back - prefer the one from the
    /// last handshake verdict (what was ACTUALLY dialed) over a recomputed value.</param>
    /// <param name="provisionerLastError">TailscaleServeSelfProvisioner.LastError, surfaced on
    /// the serve-mapping rung: it explains WHY the mapping is missing when the provisioner
    /// already tried and failed.</param>
    public GatewayConnectivitySelfTest(int port, string directorId, string? advertisedEndpoint, string? gatewayUrl, string? provisionerLastError)
    {
        if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port), port, "port must be positive");
        _port = port;
        _directorId = directorId ?? throw new ArgumentNullException(nameof(directorId));
        _advertisedEndpoint = advertisedEndpoint;
        _gatewayUrl = gatewayUrl;
        _provisionerLastError = provisionerLastError;
    }

    /// <summary>Run the ladder, yielding each rung as it completes.</summary>
    public async IAsyncEnumerable<LadderRung> RunAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        FileLog.Write($"[GatewayConnectivitySelfTest] RunAsync: port={_port}, endpoint={_advertisedEndpoint ?? "(none)"}");
        var failed = false;

        // ----- Rung 1: Is Tailscale up? -----
        LadderRung rung;
        if (!CliAvailable())
        {
            rung = new LadderRung
            {
                Title = "Is Tailscale up?",
                Status = RungStatus.Fail,
                Found = "The tailscale CLI was not found on this machine.",
                Fix = "Install Tailscale (winget install Tailscale.Tailscale), log into the tailnet, then re-test.",
            };
        }
        else
        {
            var (ok, stdout, message) = await Task.Run(() => Runner("status --json"), ct);
            var backendState = ok ? TailscaleIdentity.ParseBackendState(stdout) : null;
            rung = string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase)
                ? new LadderRung
                {
                    Title = "Is Tailscale up?",
                    Status = RungStatus.Pass,
                    Found = "tailscale status: BackendState=Running.",
                }
                : new LadderRung
                {
                    Title = "Is Tailscale up?",
                    Status = RungStatus.Fail,
                    Found = ok
                        ? $"tailscale status: BackendState={backendState ?? "(missing)"} - the tailnet is not connected."
                        : $"tailscale status failed: {message}",
                    Fix = "Start Tailscale and connect: open the Tailscale app, or run: tailscale up",
                };
        }
        failed = rung.Status == RungStatus.Fail;
        yield return rung;

        // ----- Rung 2: Is the serve mapping present? -----
        if (failed)
        {
            yield return Skip("Is the Serve mapping present?");
        }
        else
        {
            var (ok, stdout, message) = await Task.Run(() => Runner("serve status --json"), ct);
            var hasMapping = ok && TailscaleServeSelfProvisioner.StatusHasMapping(stdout, _port);
            if (hasMapping)
            {
                yield return new LadderRung
                {
                    Title = "Is the Serve mapping present?",
                    Status = RungStatus.Pass,
                    Found = $"tailscale serve maps https://<this-machine>:{_port} -> http://localhost:{_port}.",
                };
            }
            else
            {
                var why = ok
                    ? $"The serve table has NO mapping for port {_port} - the Gateway's probes hit nothing and time out. This was the SORENLAPTOP root cause."
                    : $"tailscale serve status failed: {message}";
                if (_provisionerLastError is not null)
                    why += $" Self-provisioner last error: {_provisionerLastError}";
                yield return new LadderRung
                {
                    Title = "Is the Serve mapping present?",
                    Status = RungStatus.Fail,
                    Found = why,
                    Fix = $"tailscale serve --bg --https={_port} http://localhost:{_port}",
                    CanAutoFix = true,
                };
                failed = true;
            }
        }

        // ----- Rung 3: Does the local listener answer? -----
        if (failed)
        {
            yield return Skip("Does the local listener answer?");
        }
        else
        {
            var (ok, detail) = await HttpProbe($"http://127.0.0.1:{_port}/healthz", ct);
            yield return ok
                ? new LadderRung
                {
                    Title = "Does the local listener answer?",
                    Status = RungStatus.Pass,
                    Found = $"GET http://127.0.0.1:{_port}/healthz answered 2xx.",
                }
                : new LadderRung
                {
                    Title = "Does the local listener answer?",
                    Status = RungStatus.Fail,
                    Found = $"GET http://127.0.0.1:{_port}/healthz failed: {detail}. The Director's own Kestrel is not answering - this is a Director problem, not a network one.",
                    Fix = "Check the Director log at %LOCALAPPDATA%\\cc-director\\logs\\director\\ for Kestrel startup errors, then restart this Director.",
                };
            failed = failed || !ok;
        }

        // ----- Rung 4: Does the advertised URL answer as THIS Director? -----
        if (failed)
        {
            yield return Skip("Does the advertised URL reach this Director?");
        }
        else if (string.IsNullOrEmpty(_advertisedEndpoint))
        {
            yield return new LadderRung
            {
                Title = "Does the advertised URL reach this Director?",
                Status = RungStatus.Fail,
                Found = "This Director has no advertised tailnet endpoint - it never registered a callable address.",
                Fix = "Confirm Tailscale is logged in (rung 1) so the MagicDNS name resolves; or set gateway.tailnetEndpoint in config.json for a non-Tailscale front door.",
            };
            failed = true;
        }
        else
        {
            var url = $"{_advertisedEndpoint.TrimEnd('/')}/healthz";
            var (ok, detail) = await HttpProbe(url, ct);
            if (!ok)
            {
                yield return new LadderRung
                {
                    Title = "Does the advertised URL reach this Director?",
                    Status = RungStatus.Fail,
                    Found = $"GET {url} failed from this machine: {detail}. Rungs 1-3 passed, so the URL itself is wrong: stale tailnet name, wrong port, or a certificate still being issued.",
                    Fix = "Compare the advertised URL against 'tailscale status' (this machine's MagicDNS name) and this Director's port. First-ever Serve on a node can take ~30s to get its TLS certificate - re-test once before digging.",
                };
                failed = true;
            }
            else if (detail.Contains(_directorId, StringComparison.OrdinalIgnoreCase))
            {
                yield return new LadderRung
                {
                    Title = "Does the advertised URL reach this Director?",
                    Status = RungStatus.Pass,
                    Found = $"GET {url} answered as this Director ({_directorId[..Math.Min(8, _directorId.Length)]}...).",
                };
            }
            else
            {
                yield return new LadderRung
                {
                    Title = "Does the advertised URL reach this Director?",
                    Status = RungStatus.Fail,
                    Found = $"GET {url} answered 2xx but as a DIFFERENT Director - the advertised URL reaches the wrong process (port collision or stale serve mapping).",
                    Fix = $"Run 'tailscale serve status' and check what port {_port} proxies to; remove stale mappings with: tailscale serve --https={_port} off",
                };
                failed = true;
            }
        }

        // ----- Rung 5: Versions (info) -----
        {
            var found = $"This Director: {AppVersion.Display}.";
            if (!string.IsNullOrEmpty(_gatewayUrl))
            {
                var (ok, detail) = await HttpProbe($"{_gatewayUrl.TrimEnd('/')}/healthz", ct);
                found += ok
                    ? " Gateway /healthz answered (see dialog header for its version)."
                    : $" Gateway /healthz did not answer from here: {detail}.";
            }
            yield return new LadderRung
            {
                Title = "Build versions",
                Status = RungStatus.Info,
                Found = found + " A Director build that predates the self-serve provisioner (#197) never asserts its own Serve mapping - if rung 2 keeps failing after a manual fix, update this Director.",
            };
        }

        // ----- Rung 6: Firewall (info, deliberately LAST) -----
        yield return new LadderRung
        {
            Title = "Windows Firewall",
            Status = RungStatus.Info,
            Found = "Checked last on purpose: with Tailscale Serve active, the mapping answers inside tailscaled BEFORE the Windows firewall is consulted - a TCP timeout means a missing mapping (rung 2), not a blocked port. The firewall only matters for non-Serve setups (a hand-run reverse proxy).",
        };

        FileLog.Write($"[GatewayConnectivitySelfTest] RunAsync complete: rootCauseFound={failed}");
    }

    private static LadderRung Skip(string title) => new()
    {
        Title = title,
        Status = RungStatus.Skipped,
        Found = "Skipped - an earlier rung already failed; fix that one first.",
    };

    /// <summary>One short HTTP GET: (2xx?, body-snippet-or-error). 5s budget per probe.</summary>
    private static async Task<(bool ok, string detail)> ProbeHttpAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode
                ? (true, body.Length > 500 ? body[..500] : body)
                : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "timeout after 5s");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
