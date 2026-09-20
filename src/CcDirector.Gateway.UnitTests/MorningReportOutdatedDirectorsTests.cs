using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The daily report's out-of-date Directors row (#3124). Every test pins one thing the email is allowed, or
/// not allowed, to tell a person about their machines:
///
///   * a Director provably older than the newest release is named, with its machine and its version;
///   * NOT KNOWING is never reported as either answer - an unread newest release, or a version that
///     cannot be parsed, produces no claim at all;
///   * a Director that was stopped, or not heard from in a day, is not nagged about;
///   * one account's row never names another account's machine.
/// </summary>
public sealed class MorningReportOutdatedDirectorsTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private static readonly DateTime Now = new(2026, 9, 20, 11, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private static DirectorDto Director(string machine, string version, double seenHoursAgo = 1, bool stopped = false) => new()
    {
        DirectorId = $"dir-{machine}",
        MachineName = machine,
        Version = version,
        LastSeen = Now.AddHours(-seenHoursAgo),
        StoppedAtUtc = stopped ? Now.AddHours(-seenHoursAgo) : null,
    };

    private List<MorningAttentionItemDto> Attention(
        string? newest, TenantId tenant, Dictionary<TenantId, DirectorDto[]> fleet)
    {
        var builder = new MorningReportBuilder(
            _h.Open(new AsyncLocalTenantContext()),
            utcNow: () => Now,
            directors: t => fleet.TryGetValue(t, out var mine) ? mine : Array.Empty<DirectorDto>(),
            newestRelease: () => newest);
        return builder.Build("someone@example.com", tenant, MorningReportWindow.Resolve("2026-09-19", "UTC")).Attention;
    }

    private static OutdatedDirectorsAttentionDto? RowOf(IEnumerable<MorningAttentionItemDto> attention) =>
        attention.OfType<OutdatedDirectorsAttentionDto>().SingleOrDefault();

    [Fact]
    public void A_Director_behind_the_newest_release_is_named_with_its_machine_and_version()
    {
        var row = RowOf(Attention("2.8.1", Alice, new()
        {
            [Alice] = new[] { Director("LAPTOP", "2.7.0"), Director("DESKTOP", "2.8.1"), Director("ATTIC", "2.6.3") },
        }));

        Assert.NotNull(row);
        Assert.Equal("outdated-directors", row!.Type);
        Assert.Equal("2.8.1", row.Newest);
        // One item for the account, every behind machine in it, in a stable order; the current one is absent.
        Assert.Equal(new[] { "ATTIC 2.6.3", "LAPTOP 2.7.0" }, row.Directors.Select(d => $"{d.Machine} {d.Version}"));
    }

    [Fact]
    public void Everything_current_means_no_row()
    {
        Assert.Null(RowOf(Attention("2.8.1", Alice, new()
        {
            [Alice] = new[] { Director("LAPTOP", "2.8.1"), Director("DEV", "2.9.0+local") },
        })));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_newest_release_that_has_not_been_read_yet_calls_nobody_behind(string? newest)
    {
        // Positive control: the same fleet DOES produce a row once the newest release is known, so the
        // absence asserted here is the unread release and not a fleet that had nothing to report.
        var fleet = new Dictionary<TenantId, DirectorDto[]> { [Alice] = new[] { Director("LAPTOP", "2.7.0") } };
        Assert.NotNull(RowOf(Attention("2.8.1", Alice, fleet)));

        Assert.Null(RowOf(Attention(newest, Alice, fleet)));
    }

    [Fact]
    public void A_version_that_cannot_be_read_is_never_called_behind()
    {
        var row = RowOf(Attention("2.8.1", Alice, new()
        {
            [Alice] = new[] { Director("ODD", "not-a-version"), Director("BLANK", ""), Director("LAPTOP", "2.7.0") },
        }));

        Assert.Equal(new[] { "LAPTOP" }, row!.Directors.Select(d => d.Machine));
    }

    [Fact]
    public void A_stopped_Director_and_one_not_heard_from_in_a_day_are_not_nagged_about()
    {
        var row = RowOf(Attention("2.8.1", Alice, new()
        {
            [Alice] = new[]
            {
                Director("STOPPED", "2.7.0", stopped: true),
                Director("LONG-GONE", "2.7.0", seenHoursAgo: 25),
                new DirectorDto { DirectorId = "dir-never", MachineName = "NEVER-SEEN", Version = "2.7.0" },
                // Shut overnight but worked on yesterday evening: this is who the row is for.
                Director("LAST-NIGHT", "2.7.0", seenHoursAgo: 10),
            },
        }));

        Assert.Equal(new[] { "LAST-NIGHT" }, row!.Directors.Select(d => d.Machine));
    }

    [Fact]
    public void One_accounts_row_never_names_another_accounts_machine()
    {
        var fleet = new Dictionary<TenantId, DirectorDto[]>
        {
            [Alice] = new[] { Director("ALICE-PC", "2.8.1") },
            [Bob] = new[] { Director("BOB-PC", "2.7.0") },
        };

        Assert.Null(RowOf(Attention("2.8.1", Alice, fleet)));
        Assert.Equal(new[] { "BOB-PC" }, RowOf(Attention("2.8.1", Bob, fleet))!.Directors.Select(d => d.Machine));
    }

    [Fact]
    public void A_builder_given_no_registry_says_nothing_about_Directors()
    {
        var builder = new MorningReportBuilder(_h.Open(new AsyncLocalTenantContext()), utcNow: () => Now);
        var attention = builder.Build("a@example.com", Alice, MorningReportWindow.Resolve("2026-09-19", "UTC")).Attention;

        Assert.Null(RowOf(attention));
    }

    [Fact]
    public void On_the_wire_the_row_is_camelCase_with_its_type_and_no_synthetic_discriminator()
    {
        var row = RowOf(Attention("2.8.1", Alice, new() { [Alice] = new[] { Director("LAPTOP", "2.7.0") } }));
        var json = System.Text.Json.JsonSerializer.Serialize<MorningAttentionItemDto>(row!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("{\"type\":\"outdated-directors\",\"newest\":\"2.8.1\",\"directors\":[{\"machine\":\"LAPTOP\",\"version\":\"2.7.0\"}]}", json);
    }
}
