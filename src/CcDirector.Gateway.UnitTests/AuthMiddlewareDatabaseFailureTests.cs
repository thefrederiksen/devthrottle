using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class AuthMiddlewareDatabaseFailureTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Run_AuthoritativeDatabaseUnavailable_ReturnsServiceUnavailableAndDoesNotContinue()
    {
        var database = _harness.Open();
        using var devices = new DeviceRegistry(database, _harness.LegacyPath("devices.json"));
        var key = devices.Register("device-unavailable", "WORKSTATION").DeviceKey;
        database.Dispose();
        var context = new DefaultHttpContext();
        context.Request.Path = "/sessions";
        context.Request.Headers.Authorization = $"Bearer {key}";
        context.Response.Body = new MemoryStream();
        var continued = false;

        await AuthMiddleware.Run(
            context,
            new AuthMiddleware.RequireToken { Hosted = false, Token = "shared-token", Devices = devices },
            () =>
            {
                continued = true;
                return Task.CompletedTask;
            });

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("device_registry_unavailable", body, StringComparison.Ordinal);
    }
}
