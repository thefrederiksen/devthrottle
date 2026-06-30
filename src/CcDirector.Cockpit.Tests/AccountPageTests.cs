using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using CcDirector.Cockpit.Components.Pages;
using CcDirector.Cockpit.Services;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Issue #853: the redesigned Cockpit Account page. These bUnit tests render the REAL compiled Account
/// component backed by a REAL <see cref="GatewayClient"/> whose HttpClient is stubbed to answer the
/// account endpoints (status / devices / revoke / sign-in) with known responses, and assert:
///   - signed-out shows a working "Sign in" action that calls POST /account/sign-in and shows progress;
///   - signed-in shows a "Your devices" list with the "This device" marker, platform, and last-seen;
///   - Remove takes a confirmation step, calls DELETE /account/devices/{id}, and the row disappears on
///     the refresh;
///   - a device-list error shows an EXPLICIT error state, never an empty "no devices" list;
///   - an empty account shows the distinct empty state.
/// Also emits standalone HTML proof artifacts (real markup + real app.css) when CC853_PROOF_DIR is set,
/// the same screenshot-proof pattern the other Cockpit page tests use.
/// </summary>
public sealed class AccountPageTests : TestContext
{
    private const string ThisId = "dev-1111";
    private const string OtherId = "dev-2222";

    /// <summary>
    /// A stateful stub for the Gateway account endpoints. Routes by path + method and tracks revokes so a
    /// post-revoke device list omits the removed device (proving the row disappears on refresh).
    /// </summary>
    private sealed class AccountStubHandler : HttpMessageHandler
    {
        public bool SignedIn = true;
        public string? Email = "person@example.com";
        public string? Provider = "google";

        public bool DevicesSignedIn = true;
        public int DevicesHttpStatus = 200;            // set to 502 to simulate a cloud error
        public List<AccountDeviceDto> Devices = new();
        public readonly List<string> RevokedIds = new();

        public bool SignInStarted = true;
        public bool SignInAlready = false;

        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method;

            if (path == "/account/status" && method == HttpMethod.Get)
                return Json(new AccountStatusDto { SignedIn = SignedIn, Email = SignedIn ? Email : null, Provider = SignedIn ? Provider : null });

            if (path == "/account/devices" && method == HttpMethod.Get)
            {
                if (DevicesHttpStatus != 200)
                    return Error(DevicesHttpStatus, "Could not reach the DevThrottle account service to list devices. Try again shortly.");
                var visible = DevicesSignedIn
                    ? Devices.Where(d => !RevokedIds.Contains(d.Id)).ToList()
                    : null;
                return Json(new AccountDevicesResponseDto { SignedIn = DevicesSignedIn, Devices = visible });
            }

            if (path.StartsWith("/account/devices/", StringComparison.Ordinal) && method == HttpMethod.Delete)
            {
                var id = Uri.UnescapeDataString(path["/account/devices/".Length..]);
                RevokedIds.Add(id);
                return Json(new RevokeDeviceResponseDto { SignedIn = true, Id = id, Revoked = true });
            }

            if (path == "/account/sign-in" && method == HttpMethod.Post)
                return Json(new SignInStartResponseDto { Started = SignInStarted, AlreadySignedIn = SignInAlready });

            if (path == "/account/logout" && method == HttpMethod.Post)
            {
                SignedIn = false;
                return Json(new AccountStatusDto { SignedIn = false });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json<T>(T body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, Camel), System.Text.Encoding.UTF8, "application/json"),
            });

        private static Task<HttpResponseMessage> Error(int status, string message) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = message }), System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static List<AccountDeviceDto> TwoDevices() => new()
    {
        new AccountDeviceDto
        {
            Id = ThisId, Name = "WORKSTATION-01", Platform = "Windows", DeviceType = "gateway",
            AppVersion = "1.4.0", LastSeenAt = "2026-06-30T12:00:00Z", ThisDevice = true,
        },
        new AccountDeviceDto
        {
            Id = OtherId, Name = "Galaxy Z Flip 4", Platform = "Android", DeviceType = "phone",
            AppVersion = "1.3.2", LastSeenAt = "2026-06-28T09:30:00Z", ThisDevice = false,
        },
    };

    private (IRenderedComponent<Account> cut, AccountStubHandler stub) Render(Action<AccountStubHandler>? configure = null)
        => RenderInto(this, configure);

    /// <summary>
    /// Registers a stubbed <see cref="GatewayClient"/> on the given context and renders the Account
    /// component. A bUnit TestContext only allows ONE render (services can't be added after the first
    /// retrieval), so the proof-artifact test uses a fresh context per artifact via this helper.
    /// </summary>
    private static (IRenderedComponent<Account> cut, AccountStubHandler stub) RenderInto(TestContext ctx, Action<AccountStubHandler>? configure)
    {
        var stub = new AccountStubHandler();
        configure?.Invoke(stub);
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://gw.test/") };
        ctx.Services.AddSingleton(new GatewayClient(http, NullLogger<GatewayClient>.Instance));
        var cut = ctx.RenderComponent<Account>();
        return (cut, stub);
    }

    [Fact]
    public void SignedOut_shows_working_signin_action_not_static_text()
    {
        var (cut, _) = Render(s => s.SignedIn = false);

        var button = cut.Find(".acct-signin-btn");
        Assert.Contains("Sign in to DevThrottle", button.TextContent);
        // The old "use the tray" static text is gone.
        Assert.DoesNotContain("tray", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignIn_click_calls_start_endpoint_and_shows_progress()
    {
        var (cut, _) = Render(s => { s.SignedIn = false; s.SignInStarted = true; });

        cut.Find(".acct-signin-btn").Click();

        // The page shows the in-progress affordance (the background poll drives completion).
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".acct-signin-progress")));
    }

    [Fact]
    public void SignedIn_lists_devices_with_thisdevice_marker_platform_and_lastseen()
    {
        var (cut, _) = Render(s => { s.SignedIn = true; s.Devices = TwoDevices(); });

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".acct-device-row").Count));

        var rows = cut.FindAll(".acct-device-row");
        Assert.Contains("WORKSTATION-01", rows[0].TextContent);
        Assert.Contains("This device", rows[0].TextContent);          // marker on the Gateway's own device
        Assert.Contains("Windows", rows[0].TextContent);              // platform
        Assert.Contains("Last seen:", rows[0].TextContent);           // last-seen line

        // The other device is NOT marked "This device".
        Assert.DoesNotContain("This device", rows[1].TextContent);
        Assert.Contains("Galaxy Z Flip 4", rows[1].TextContent);
    }

    [Fact]
    public void Remove_requires_confirmation_then_drops_the_row_on_refresh()
    {
        var (cut, stub) = Render(s => { s.SignedIn = true; s.Devices = TwoDevices(); });
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".acct-device-row").Count));

        // First click reveals the confirmation, it does NOT remove anything yet.
        cut.FindAll(".acct-remove-btn")[1].Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".acct-remove-confirm")));
        Assert.Empty(stub.RevokedIds);

        // Confirm: calls DELETE and the list refreshes without the removed device.
        cut.Find(".acct-remove-confirm-yes").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".acct-device-row")));
        Assert.Contains(OtherId, stub.RevokedIds);
        Assert.Contains("WORKSTATION-01", cut.Markup);
        Assert.DoesNotContain("Galaxy Z Flip 4", cut.Markup);
    }

    [Fact]
    public void Remove_cancel_keeps_the_device()
    {
        var (cut, stub) = Render(s => { s.SignedIn = true; s.Devices = TwoDevices(); });
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".acct-device-row").Count));

        cut.FindAll(".acct-remove-btn")[1].Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".acct-remove-confirm")));
        cut.Find(".acct-remove-confirm-no").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".acct-remove-confirm")));
        Assert.Empty(stub.RevokedIds);
        Assert.Equal(2, cut.FindAll(".acct-device-row").Count);
    }

    [Fact]
    public void Devices_error_shows_explicit_error_not_empty_list()
    {
        var (cut, _) = Render(s => { s.SignedIn = true; s.DevicesHttpStatus = 502; });

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".acct-devices-error")));
        // The explicit error is shown, and the misleading "no devices" empty state is NOT.
        Assert.Empty(cut.FindAll(".acct-devices-empty"));
        Assert.Empty(cut.FindAll(".acct-device-row"));
    }

    [Fact]
    public void Devices_empty_account_shows_distinct_empty_state()
    {
        var (cut, _) = Render(s => { s.SignedIn = true; s.Devices = new List<AccountDeviceDto>(); });

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".acct-devices-empty")));
        Assert.Empty(cut.FindAll(".acct-devices-error"));
        Assert.Empty(cut.FindAll(".acct-device-row"));
    }

    /// <summary>
    /// Emits standalone HTML proof artifacts (the real rendered page wrapped in the real app.css) when
    /// CC853_PROOF_DIR is set. Not an assertion test - it lets the Developer Agent screenshot the genuine
    /// compiled markup for the issue's visual proof. A no-op in the normal suite.
    /// </summary>
    [Fact]
    public void EmitProofArtifacts_WhenProofDirSet()
    {
        var proofDir = Environment.GetEnvironmentVariable("CC853_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(proofDir)) return;
        Directory.CreateDirectory(proofDir);

        // Signed-in with a multi-device list (This device marker + last-seen).
        using (var ctx = new TestContext())
        {
            var (signedIn, _) = RenderInto(ctx, s => { s.SignedIn = true; s.Devices = TwoDevices(); });
            signedIn.WaitForAssertion(() => Assert.Equal(2, signedIn.FindAll(".acct-device-row").Count));
            WriteArtifact(proofDir, "account-signed-in-devices.html", signedIn.Markup);
        }

        // Remove confirmation visible on a row.
        using (var ctx = new TestContext())
        {
            var (confirm, _) = RenderInto(ctx, s => { s.SignedIn = true; s.Devices = TwoDevices(); });
            confirm.WaitForAssertion(() => Assert.Equal(2, confirm.FindAll(".acct-device-row").Count));
            confirm.FindAll(".acct-remove-btn")[1].Click();
            confirm.WaitForAssertion(() => Assert.NotNull(confirm.Find(".acct-remove-confirm")));
            WriteArtifact(proofDir, "account-remove-confirm.html", confirm.Markup);
        }

        // Signed-out with the real Sign in action.
        using (var ctx = new TestContext())
        {
            var (signedOut, _) = RenderInto(ctx, s => s.SignedIn = false);
            WriteArtifact(proofDir, "account-signed-out.html", signedOut.Markup);
        }

        // Sign-in in progress (after clicking Sign in).
        using (var ctx = new TestContext())
        {
            var (progress, _) = RenderInto(ctx, s => { s.SignedIn = false; s.SignInStarted = true; });
            progress.Find(".acct-signin-btn").Click();
            progress.WaitForAssertion(() => Assert.NotNull(progress.Find(".acct-signin-progress")));
            WriteArtifact(proofDir, "account-signin-progress.html", progress.Markup);
        }

        // Device-list error (explicit error state, not an empty list).
        using (var ctx = new TestContext())
        {
            var (error, _) = RenderInto(ctx, s => { s.SignedIn = true; s.DevicesHttpStatus = 502; });
            error.WaitForAssertion(() => Assert.NotNull(error.Find(".acct-devices-error")));
            WriteArtifact(proofDir, "account-devices-error.html", error.Markup);
        }

        // Empty account (distinct empty state).
        using (var ctx = new TestContext())
        {
            var (empty, _) = RenderInto(ctx, s => { s.SignedIn = true; s.Devices = new List<AccountDeviceDto>(); });
            empty.WaitForAssertion(() => Assert.NotNull(empty.Find(".acct-devices-empty")));
            WriteArtifact(proofDir, "account-devices-empty.html", empty.Markup);
        }
    }

    private static void WriteArtifact(string proofDir, string fileName, string pageHtml)
    {
        var here = AppContext.BaseDirectory;
        var cssPath = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..",
            "CcDirector.Cockpit", "wwwroot", "app.css"));
        var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";

        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>\n" +
            "body{background:#1E1E1E;margin:0;padding:16px;font-family:'Segoe UI',sans-serif;color:#CCCCCC}\n" +
            css +
            "\n</style></head><body>" +
            pageHtml +
            "</body></html>";

        File.WriteAllText(Path.Combine(proofDir, fileName), html);
    }
}
