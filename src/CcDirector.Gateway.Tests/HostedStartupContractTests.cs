using System.Reflection;
using System.Reflection.Emit;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Pins the fail-CLOSED hosted identity (production-readiness item MH-3 / TOP-ISSUES #8): the hosted Gateway
/// image must prove its full contract at startup or refuse to run, and self-host must be entirely unaffected.
///
/// The pieces proven here:
///  - The immutable hosted identity is read from the compiled-in <see cref="HostedGatewayImageAttribute"/>,
///    not from the droppable CC_GATEWAY_HOSTED toggle.
///  - The hosted contract holds only when hosted mode is on, auth is enabled (the disable flags are
///    rejected), a public https URL is set, and PostgreSQL is configured. Any missing piece is a violation.
///  - A hosted-image boot with an incomplete contract THROWS (the entry point turns that into a non-zero
///    exit); a self-host boot never runs the contract at all.
/// </summary>
public sealed class HostedStartupContractTests
{
    // A complete, valid hosted environment - the baseline each test perturbs one field of.
    private static Dictionary<string, string?> ValidEnv() => new()
    {
        [GatewayHostedMode.HostedEnvVar] = "1",
        [GatewayHost.AuthDisabledEnvVar] = null,
        [GatewayHost.AuthEnabledEnvVar] = null,
        [GatewayPublicUrl.PublicBaseUrlEnvVar] = "https://gateway.devthrottle.com",
        [CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar] = "Host=db;Database=gateway;Username=u;Password=p",
    };

    private static IReadOnlyList<string> Check(Dictionary<string, string?> env)
        => HostedStartupContract.CheckHostedContract(
            env[GatewayHostedMode.HostedEnvVar],
            env[GatewayHost.AuthDisabledEnvVar],
            env[GatewayHost.AuthEnabledEnvVar],
            env[GatewayPublicUrl.PublicBaseUrlEnvVar],
            env[CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar]);

    [Fact]
    public void Full_contract_has_no_violations()
    {
        Assert.Empty(Check(ValidEnv()));
    }

    [Fact]
    public void Missing_hosted_toggle_is_a_violation()
    {
        var env = ValidEnv();
        env[GatewayHostedMode.HostedEnvVar] = null;
        var violations = Check(env);
        Assert.Contains(violations, v => v.Contains(GatewayHostedMode.HostedEnvVar));
    }

    [Fact]
    public void Hosted_toggle_other_than_one_is_a_violation()
    {
        var env = ValidEnv();
        env[GatewayHostedMode.HostedEnvVar] = "0";
        Assert.Contains(Check(env), v => v.Contains(GatewayHostedMode.HostedEnvVar));
    }

    [Fact]
    public void No_auth_disable_flag_is_rejected()
    {
        var env = ValidEnv();
        env[GatewayHost.AuthDisabledEnvVar] = "1";
        Assert.Contains(Check(env), v => v.Contains(GatewayHost.AuthDisabledEnvVar));
    }

    [Fact]
    public void Auth_zero_disable_flag_is_rejected()
    {
        var env = ValidEnv();
        env[GatewayHost.AuthEnabledEnvVar] = "0";
        Assert.Contains(Check(env), v => v.Contains(GatewayHost.AuthEnabledEnvVar));
    }

    [Fact]
    public void Missing_public_url_is_a_violation()
    {
        var env = ValidEnv();
        env[GatewayPublicUrl.PublicBaseUrlEnvVar] = null;
        Assert.Contains(Check(env), v => v.Contains(GatewayPublicUrl.PublicBaseUrlEnvVar));
    }

    [Fact]
    public void Non_https_public_url_is_a_violation()
    {
        var env = ValidEnv();
        env[GatewayPublicUrl.PublicBaseUrlEnvVar] = "http://gateway.devthrottle.com";
        Assert.Contains(Check(env), v => v.Contains(GatewayPublicUrl.PublicBaseUrlEnvVar));
    }

    [Theory]
    // A bare "https://" prefix is NOT a valid absolute URL: these all cleared the old StartsWith("https://")
    // check yet have no usable host, so GatewayPublicUrl would have served a broken base with no later guard.
    [InlineData("https://")]        // scheme only, no host
    [InlineData("https:///path")]   // empty host, path only
    [InlineData("https://:443")]    // empty host, port only
    [InlineData("https:// ")]       // scheme with a blank host
    [InlineData("https://gateway.devthrottle.com?x=1")] // base URL must carry no query
    [InlineData("https://gateway.devthrottle.com#frag")] // base URL must carry no fragment
    [InlineData("not-a-url")]       // not absolute at all
    [InlineData("ftp://gateway.devthrottle.com")] // wrong scheme
    public void Malformed_https_public_url_is_a_violation(string value)
    {
        var env = ValidEnv();
        env[GatewayPublicUrl.PublicBaseUrlEnvVar] = value;
        Assert.Contains(Check(env), v => v.Contains(GatewayPublicUrl.PublicBaseUrlEnvVar));
    }

    [Theory]
    // Well-formed public base URLs must pass: a plain host, a host with a port, and a host with a path
    // prefix are all valid base URLs the contract must NOT reject (over-refusal would be a hosted outage).
    [InlineData("https://gateway.devthrottle.com")]
    [InlineData("https://gateway.devthrottle.com/")]
    [InlineData("https://host:8443")]
    [InlineData("https://gateway.devthrottle.com/base")]
    [InlineData("  https://gateway.devthrottle.com  ")] // surrounding whitespace is trimmed, still valid
    public void Well_formed_https_public_url_is_not_a_violation(string value)
    {
        var env = ValidEnv();
        env[GatewayPublicUrl.PublicBaseUrlEnvVar] = value;
        Assert.DoesNotContain(Check(env), v => v.Contains(GatewayPublicUrl.PublicBaseUrlEnvVar));
    }

    [Fact]
    public void Missing_postgres_connection_is_a_violation()
    {
        var env = ValidEnv();
        env[CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar] = null;
        Assert.Contains(Check(env), v => v.Contains(CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar));
    }

    [Fact]
    public void Blank_postgres_connection_is_a_violation()
    {
        var env = ValidEnv();
        env[CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar] = "   ";
        Assert.Contains(Check(env), v => v.Contains(CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar));
    }

    [Fact]
    public void Every_missing_piece_is_reported_at_once()
    {
        // A bare hosted image with nothing configured names every failing piece, so an operator fixes them
        // in one pass rather than discovering them one restart at a time.
        var violations = HostedStartupContract.CheckHostedContract(
            hosted: null, noAuth: "1", authToggle: "0", publicUrl: null, dbConnection: null);

        Assert.Contains(violations, v => v.Contains(GatewayHostedMode.HostedEnvVar));
        Assert.Contains(violations, v => v.Contains(GatewayHost.AuthDisabledEnvVar));
        Assert.Contains(violations, v => v.Contains(GatewayHost.AuthEnabledEnvVar));
        Assert.Contains(violations, v => v.Contains(GatewayPublicUrl.PublicBaseUrlEnvVar));
        Assert.Contains(violations, v => v.Contains(CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar));
    }

    [Fact]
    public void A_violation_message_never_echoes_the_connection_string()
    {
        // The connection string carries credentials; a violation about it must describe the value, never
        // print it. (Here it is blank, but the redaction path is the one that matters.)
        var violations = HostedStartupContract.CheckHostedContract(
            hosted: "1", noAuth: null, authToggle: null,
            publicUrl: "https://gateway.devthrottle.com", dbConnection: "   ");
        Assert.All(violations, v => Assert.DoesNotContain("Password", v, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Self_host_never_runs_the_contract()
    {
        // Not the hosted image: Assert is a no-op even with a completely broken environment. This is what
        // keeps self-host behavior byte-identical.
        HostedStartupContract.Assert(isHostedImage: false, readEnv: _ => null);
    }

    [Fact]
    public void Hosted_image_with_full_contract_starts()
    {
        var env = ValidEnv();
        // Does not throw.
        HostedStartupContract.Assert(isHostedImage: true, readEnv: name => env[name]);
    }

    [Fact]
    public void Hosted_image_with_incomplete_contract_refuses()
    {
        var env = ValidEnv();
        env[CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar] = null;
        var ex = Assert.Throws<InvalidOperationException>(
            () => HostedStartupContract.Assert(isHostedImage: true, readEnv: name => env[name]));
        Assert.Contains("REFUSING TO START", ex.Message);
        Assert.Contains(CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar, ex.Message);
    }

    [Fact]
    public void Hosted_image_marker_is_read_from_the_assembly_not_the_environment()
    {
        // Null entry assembly and unmarked assemblies are NOT the hosted image.
        Assert.False(GatewayHostedMode.IsHostedImageAssembly(null));
        Assert.False(GatewayHostedMode.IsHostedImageAssembly(typeof(GatewayHostedMode).Assembly));
        Assert.False(GatewayHostedMode.IsHostedImageAssembly(GetType().Assembly));

        // An assembly that carries the marker attribute IS the hosted image.
        var marked = new AssemblyName("HostedImageMarkerProbe");
        var builder = AssemblyBuilder.DefineDynamicAssembly(marked, AssemblyBuilderAccess.RunAndCollect);
        var ctor = typeof(HostedGatewayImageAttribute).GetConstructor(Type.EmptyTypes)!;
        builder.SetCustomAttribute(new CustomAttributeBuilder(ctor, Array.Empty<object>()));
        Assert.True(GatewayHostedMode.IsHostedImageAssembly(builder));
    }

    [Fact]
    public void Image_wide_marker_file_makes_an_unmarked_deployment_the_hosted_image()
    {
        // This is the fix for the entrypoint-swap bypass: publishing the hosted host also ships a runnable,
        // UNMARKED CcDirector.Gateway.dll. When the image-wide marker file sits in the deployment directory,
        // even that unmarked entry is the hosted image, so the fail-closed contract fires whichever executable
        // the container runs.
        var dir = Path.Combine(Path.GetTempPath(), "cc-hosted-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // No marker yet: an unmarked entry assembly is self-host.
            Assert.False(GatewayHostedMode.HostedImageMarkerPresent(dir));
            Assert.False(GatewayHostedMode.IsHostedImageDeployment(entryAssembly: null, dir));

            // Drop the marker the hosted host bakes into its published output.
            File.WriteAllText(Path.Combine(dir, GatewayHostedMode.HostedImageMarkerFileName), "marker");

            // Now the SAME unmarked deployment is the hosted image.
            Assert.True(GatewayHostedMode.HostedImageMarkerPresent(dir));
            Assert.True(GatewayHostedMode.IsHostedImageDeployment(entryAssembly: null, dir));
            Assert.True(GatewayHostedMode.IsHostedImageDeployment(typeof(GatewayHostedMode).Assembly, dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void No_marker_and_no_attribute_is_self_host()
    {
        // A directory with no marker, and no marked entry assembly, is self-host - the byte-identical dev /
        // desktop path. A null or blank base directory never fabricates a hosted verdict.
        var dir = Path.Combine(Path.GetTempPath(), "cc-hosted-nomarker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(GatewayHostedMode.HostedImageMarkerPresent(dir));
            Assert.False(GatewayHostedMode.HostedImageMarkerPresent(null));
            Assert.False(GatewayHostedMode.HostedImageMarkerPresent(""));
            Assert.False(GatewayHostedMode.IsHostedImageDeployment(typeof(GatewayHostedMode).Assembly, dir));
            Assert.False(GatewayHostedMode.IsHostedImageDeployment(GetType().Assembly, null));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
