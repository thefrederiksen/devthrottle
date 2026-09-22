using System.Text.Json;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The installer failure channel (issue #3311): a public route, so its bounds ARE its security. These pin
/// what is refused, what is scrubbed, what the durable line carries, and the hourly limits.
/// </summary>
public sealed class InstallReportEndpointsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 22, 20, 0, 0, DateTimeKind.Utc);
    private const string InstallId = "3f6d0e4a-0b8e-4c55-9a53-1f0b8f0e2a11";

    public InstallReportEndpointsTests() => InstallReportEndpoints.ResetForTests();

    public void Dispose() => InstallReportEndpoints.ResetForTests();

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static InstallReportEndpoints.InstallReportPost Post(
        string? installId = InstallId, string? message = "launcher did not stay running", string? diagnostics = null) =>
        new(installId, "launcher", "start", message, diagnostics, "macos", "15.6.0", "arm64", "2.9.0", "setup-wizard");

    [Fact]
    public void Handle_AValidReport_IsAcceptedAndRetrievable()
    {
        var result = InstallReportEndpoints.Handle(Post(diagnostics: "last exit code = 134: Abort trap"), Now);

        Assert.Equal(StatusCodes.Status202Accepted, Status(result));
        var recent = Assert.Single(InstallReportEndpoints.Recent(10));
        Assert.Equal("launcher", recent.Component);
        Assert.Equal("macos", recent.Os);
        Assert.Contains("134: Abort trap", recent.Diagnostics);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("has spaces in it ok")]
    [InlineData("../../etc/passwd")]
    public void Handle_ABadInstallId_IsRefused(string? installId)
    {
        Assert.Equal(StatusCodes.Status400BadRequest, Status(InstallReportEndpoints.Handle(Post(installId: installId), Now)));
        Assert.Empty(InstallReportEndpoints.Recent(10));
    }

    [Fact]
    public void Handle_NoMessage_IsRefused()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, Status(InstallReportEndpoints.Handle(Post(message: "  "), Now)));
    }

    [Fact]
    public void Handle_NoBody_IsRefused()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, Status(InstallReportEndpoints.Handle(null, Now)));
    }

    [Fact]
    public void Handle_HomeFoldersAreScrubbedEvenWhenTheInstallerDidNot()
    {
        InstallReportEndpoints.Handle(Post(
            message: "Check /Users/robertziegler/Library/Application Support/cc-director/logs",
            diagnostics: "path = /Users/robertziegler/Library/LaunchAgents/x.plist\nC:\\Users\\Bob\\AppData\\Local"), Now);

        var r = Assert.Single(InstallReportEndpoints.Recent(10));
        Assert.DoesNotContain("robertziegler", r.Message + r.Diagnostics);
        Assert.DoesNotContain("Bob", r.Diagnostics);
        Assert.Contains("~/Library/Application Support/cc-director/logs", r.Message);
    }

    [Fact]
    public void Handle_FieldsAreCappedAndControlCharactersRemoved()
    {
        var huge = new string('x', InstallReportEndpoints.MaxDiagnostics + 5000);
        InstallReportEndpoints.Handle(Post(message: "bad\u0007bell\u001b[31m", diagnostics: huge), Now);

        var r = Assert.Single(InstallReportEndpoints.Recent(10));
        Assert.Equal(InstallReportEndpoints.MaxDiagnostics, r.Diagnostics.Length);
        Assert.Equal("badbell[31m", r.Message);
    }

    [Fact]
    public void DurableLine_IsOneLineOfJsonThatCarriesTheContent()
    {
        InstallReportEndpoints.Handle(Post(diagnostics: "line one\nline two\n[InstallReport] forged"), Now);
        var r = Assert.Single(InstallReportEndpoints.Recent(10));

        var line = InstallReportEndpoints.DurableLine(r);

        Assert.StartsWith("[InstallReport] {", line);
        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line["[InstallReport] ".Length..]);
        Assert.Equal("line one\nline two\n[InstallReport] forged", doc.RootElement.GetProperty("diagnostics").GetString());
    }

    [Fact]
    public void Handle_OneInstallIsLimitedPerHour()
    {
        for (var i = 0; i < InstallReportEndpoints.MaxReportsPerInstallPerHour; i++)
            Assert.Equal(StatusCodes.Status202Accepted, Status(InstallReportEndpoints.Handle(Post(), Now.AddSeconds(i))));

        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(InstallReportEndpoints.Handle(Post(), Now.AddMinutes(1))));
        // A different machine is not held back by this one.
        Assert.Equal(StatusCodes.Status202Accepted, Status(InstallReportEndpoints.Handle(Post(installId: "aaaaaaaa-0000-0000-0000-000000000001"), Now.AddMinutes(1))));
        // An hour later the first machine may report again.
        Assert.Equal(StatusCodes.Status202Accepted, Status(InstallReportEndpoints.Handle(Post(), Now.AddHours(1).AddMinutes(1))));
    }

    [Fact]
    public void Handle_TheWholeRouteIsLimitedPerHour()
    {
        for (var i = 0; i < InstallReportEndpoints.MaxReportsPerHour; i++)
        {
            var id = $"route-limit-{i:D8}";
            Assert.Equal(StatusCodes.Status202Accepted, Status(InstallReportEndpoints.Handle(Post(installId: id), Now)));
        }

        Assert.Equal(StatusCodes.Status429TooManyRequests,
            Status(InstallReportEndpoints.Handle(Post(installId: "route-limit-overflow"), Now)));
    }
}
