// Issue #649 proof harness ONLY (not part of cc-director.sln). Hosts the real
// TelemetryConsentEndpoint plus a minimal /healthz on loopback:7878 over an isolated CC_DIRECTOR_ROOT,
// so the real Cockpit Telemetry page and curl can drive the live fleet-wide consent setting. The
// endpoint code under test (CcDirector.Gateway.Api.TelemetryConsentEndpoint) is invoked directly via
// reflection so the harness exercises the SAME delegates the Gateway maps, not a copy.

using System.Reflection;
using CcDirector.Gateway;
using Microsoft.AspNetCore.Routing;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
var app = builder.Build();
app.Urls.Add("http://127.0.0.1:17879");

// Map the REAL TelemetryConsentEndpoint.Map(IEndpointRouteBuilder) by reflection (it is internal).
var endpointType = typeof(GatewayHost).Assembly.GetType("CcDirector.Gateway.Api.TelemetryConsentEndpoint")
    ?? throw new InvalidOperationException("TelemetryConsentEndpoint type not found");
var mapMethod = endpointType.GetMethod("Map", BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidOperationException("TelemetryConsentEndpoint.Map not found");
mapMethod.Invoke(null, new object[] { app });

app.MapGet("/healthz", () => Results.Json(new { ok = true }));

Console.WriteLine($"[ConsentProofGateway] listening on http://127.0.0.1:17879 (CC_DIRECTOR_ROOT={Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT")})");
app.Run();
