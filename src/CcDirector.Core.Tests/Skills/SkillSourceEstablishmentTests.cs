using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// WHERE a Director's source comes from, run through the REAL store refresh against a hermetic Gateway and then
/// the real installer reading what the refresh recorded (devthrottle_internal#2311, review findings SK-F2 and
/// SK-F3). The ownership tests pin what one source may do to another's folders; these pin that the source a
/// Director installs as is the one the Gateway named for the key that fetched the library - never the address it
/// used, and never a local record that can be missing.
/// </summary>
public sealed class SkillSourceEstablishmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "skill-source-establish-" + Guid.NewGuid().ToString("N"));

    private string Shared => Path.Combine(_root, "home", ".agents", "skills");
    private string LinkRoot => Path.Combine(_root, "home", ".claude", "skills");
    private string StoreOf(string director) => Path.Combine(_root, director, "skills", "installed");

    private static readonly DirectorTeam? NoTeamFile = null;
    private static readonly DirectorTeam TeamAFile = new("team-a", "Team A");

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(directory, recursive: false);
        }
        Directory.Delete(_root, recursive: true);
    }

    // ---- SK-F3: the account owns its skills whatever address its Gateway is reached at ------------------------

    [Theory]
    [InlineData("http://gateway.example.com", "https://gateway.example.com")]          // a move to TLS
    [InlineData("https://devthrottle.mycompany.com", "https://devthrottle.com")]       // a custom domain to the default
    [InlineData("http://192.168.1.20:7878", "http://gateway-box.tailnet:7878")]        // a moved / re-addressed Gateway
    public async Task The_same_account_keeps_owning_its_skills_when_its_Gateway_address_changes(string before, string after)
    {
        var gateway = new FakeGateway();
        gateway.Account("person-key", gatewayId: "gw-1", tenantId: "tenant-person", teamId: null);
        gateway.Serve("person-key", "keeper", "v1");
        gateway.Serve("person-key", "withdrawn", "v1");

        await Refresh("director", gateway, before, "person-key");
        Assert.True(Install("director", NoTeamFile).IsComplete);

        // The Gateway changes the content of one skill and withdraws another, and is now reached at a new address.
        gateway.Serve("person-key", "keeper", "v2");
        gateway.Withdraw("person-key", "withdrawn");
        await Refresh("director", gateway, after, "person-key");
        var placement = Install("director", NoTeamFile);

        Assert.Empty(placement.Problems);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(LinkRoot, "keeper", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(Shared, "withdrawn")));
        Assert.False(File.Exists(Path.Combine(LinkRoot, "withdrawn", "SKILL.md")));   // its link is left in place and reads as nothing (SK-F12)
    }

    [Fact]
    public async Task Two_Directors_of_one_account_at_two_front_doors_share_its_skills_and_another_account_does_not()
    {
        var gateway = new FakeGateway();
        gateway.Account("person-key", "gw-1", "tenant-person", null);
        gateway.Account("other-key", "gw-1", "tenant-other", null);
        gateway.Serve("person-key", "shared-skill", "v1");
        gateway.Serve("other-key", "shared-skill", "OTHER");

        await Refresh("laptop", gateway, "https://devthrottle.com", "person-key");
        Install("laptop", NoTeamFile);

        // The same account through another front door, after the skill moved on: it is the same library, so
        // this Director refreshes the folder rather than yielding it.
        gateway.Serve("person-key", "shared-skill", "v2");
        await Refresh("desktop", gateway, "https://gw.devthrottle.com", "person-key");
        Assert.Empty(Install("desktop", NoTeamFile).Problems);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(LinkRoot, "shared-skill", "SKILL.md")));

        // A DIFFERENT personal account on the same Gateway and address does not own it.
        await Refresh("someone-else", gateway, "https://devthrottle.com", "other-key");
        var other = Install("someone-else", NoTeamFile);
        Assert.Contains(other.Problems, p => p.SkillId == "shared-skill" && p.Fault == SkillPlacementFault.HeldByAnotherSource);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(LinkRoot, "shared-skill", "SKILL.md")));
    }

    // ---- SK-F2: the source is the one that fetched the library, and missing means touch nothing ----------------

    [Fact]
    public async Task The_refresh_records_the_source_the_Gateway_named_for_the_key()
    {
        var gateway = new FakeGateway();
        gateway.Account("team-key", "gw-1", "team-a", "team-a");
        gateway.Serve("team-key", "team-skill", "v1");

        await Refresh("director", gateway, "https://devthrottle.com", "team-key");

        var recorded = SkillSource.Establish(StoreOf("director"), TeamAFile).Source;
        Assert.Equal(new SkillSource("gw-1", "team-a", "team-a"), recorded);
    }

    [Fact]
    public async Task A_team_key_saved_without_its_team_record_removes_and_overwrites_nothing()
    {
        // THE PARTIAL ENROLLMENT (review finding SK-F2): the team-bound key and the Gateway address were saved, the
        // team file was not. The store holds the TEAM's library. Read as personal, it would remove the person's
        // own skills and overwrite the names both serve - F7 again with the team wearing personal ownership.
        var gateway = new FakeGateway();
        gateway.Account("person-key", "gw-1", "tenant-person", null);
        gateway.Account("team-key", "gw-1", "team-a", "team-a");
        gateway.Serve("person-key", "mine-one", "PERSONAL");
        gateway.Serve("person-key", "both", "PERSONAL");
        gateway.Serve("team-key", "both", "TEAM");
        gateway.Serve("team-key", "team-one", "TEAM");

        await Refresh("personal-director", gateway, "https://devthrottle.com", "person-key");
        Install("personal-director", NoTeamFile);

        await Refresh("team-director", gateway, "https://devthrottle.com", "team-key");
        var placement = Install("team-director", NoTeamFile);   // no team file: the partial enrollment

        Assert.All(placement.Problems, p => Assert.Equal(SkillPlacementFault.SourceMismatch, p.Fault));
        Assert.Equal(2, placement.Problems.Count);
        Assert.Contains("PERSONAL", File.ReadAllText(Path.Combine(LinkRoot, "mine-one", "SKILL.md")));
        Assert.Contains("PERSONAL", File.ReadAllText(Path.Combine(LinkRoot, "both", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(Shared, "team-one")));
        Assert.Contains("check the Director's team in Settings", placement.Describe());

        // With its team recorded, the same Director places its own and yields the shared name.
        var whole = Install("team-director", TeamAFile);
        Assert.True(File.Exists(Path.Combine(LinkRoot, "team-one", "SKILL.md")));
        Assert.Contains(whole.Problems, p => p.SkillId == "both" && p.Fault == SkillPlacementFault.HeldByAnotherSource);
    }

    [Fact]
    public async Task A_Gateway_that_does_not_name_the_source_gets_nothing_placed_and_nothing_removed()
    {
        var gateway = new FakeGateway();
        gateway.Account("person-key", "gw-1", "tenant-person", null);
        gateway.Serve("person-key", "mine-one", "PERSONAL");
        await Refresh("personal-director", gateway, "https://devthrottle.com", "person-key");
        Install("personal-director", NoTeamFile);

        // A Gateway older than the rule: the register carries no source.
        var old = new FakeGateway { NamesTheSource = false };
        old.Account("old-key", "", "", null);
        old.Serve("old-key", "old-one", "OLD");
        await Refresh("old-director", old, "http://old-gateway:7878", "old-key");
        var placement = Install("old-director", NoTeamFile);

        Assert.Equal(SkillPlacementFault.SourceUnknown, Assert.Single(placement.Problems).Fault);
        Assert.Contains("the Gateway must be updated", placement.Describe());
        Assert.False(Directory.Exists(Path.Combine(Shared, "old-one")));
        Assert.Contains("PERSONAL", File.ReadAllText(Path.Combine(LinkRoot, "mine-one", "SKILL.md")));
    }

    // ---- SK-F6: the source travels with each skill's bytes ------------------------------------------------------

    [Fact]
    public async Task A_Director_moved_from_a_team_to_the_personal_account_never_places_the_team_bytes_as_personal()
    {
        // THE RULING'S CASE (review finding SK-F6). The Director served team A, then its key became the person's.
        // Both libraries hold a skill called "both". The personal version cannot be read this cycle, so the store
        // still holds the TEAM's bytes under that name. They must never be placed, or stamped, as the person's.
        var gateway = new FakeGateway();
        gateway.Account("team-key", "gw-1", "team-a", "team-a");
        gateway.Account("person-key", "gw-1", "tenant-person", null);
        gateway.Serve("team-key", "both", "TEAM");
        gateway.Serve("person-key", "both", "PERSONAL");

        await Refresh("director", gateway, "https://devthrottle.com", "team-key");
        Assert.Empty(Install("director", TeamAFile).Problems);
        Assert.Equal("team:team-a", AccountStampedOn(Path.Combine(Shared, "both")));

        gateway.FailDetail("person-key", "both");
        await Refresh("director", gateway, "https://devthrottle.com", "person-key");
        Install("director", NoTeamFile);

        var body = File.ReadAllText(Path.Combine(LinkRoot, "both", "SKILL.md"));
        var account = AccountStampedOn(Path.Combine(Shared, "both"));
        Assert.False(body.Contains("TEAM") && account == "personal", "the team's bytes are stamped as the person's own");
        Assert.Contains("TEAM", body);                 // still the team's copy...
        Assert.Equal("team:team-a", account);         // ...and still labelled as the team's

        // Once the personal version can be read, the person's own skill takes the name, as it always does.
        gateway.Recover("person-key", "both");
        await Refresh("director", gateway, "https://devthrottle.com", "person-key");
        Assert.Empty(Install("director", NoTeamFile).Problems);
        Assert.Contains("PERSONAL", File.ReadAllText(Path.Combine(LinkRoot, "both", "SKILL.md")));
        Assert.Equal("personal", AccountStampedOn(Path.Combine(Shared, "both")));
    }

    [Fact]
    public async Task A_kept_skill_fetched_for_another_library_leaves_the_store_and_one_fetched_for_this_library_stays()
    {
        var gateway = new FakeGateway();
        gateway.Account("team-key", "gw-1", "team-a", "team-a");
        gateway.Account("person-key", "gw-1", "tenant-person", null);
        gateway.Serve("team-key", "both", "TEAM");
        gateway.Serve("person-key", "both", "PERSONAL");
        gateway.Serve("person-key", "mine", "PERSONAL");

        await Refresh("director", gateway, "https://devthrottle.com", "team-key");
        gateway.FailDetail("person-key", "both");
        await Refresh("director", gateway, "https://devthrottle.com", "person-key");

        // The team's bytes are gone from the store - the person's skill arrives when it can be read.
        Assert.False(Directory.Exists(Path.Combine(StoreOf("director"), "both")));

        // A failed read of a skill this library already holds keeps it, exactly as before.
        gateway.Serve("person-key", "mine", "PERSONAL-v2");
        gateway.FailDetail("person-key", "mine");
        await Refresh("director", gateway, "https://devthrottle.com", "person-key");
        Assert.Contains("PERSONAL", File.ReadAllText(Path.Combine(StoreOf("director"), "mine", "SKILL.md")));
    }

    [Fact]
    public async Task The_same_version_of_a_skill_served_by_two_libraries_is_fetched_again_for_the_second()
    {
        // The SAME bytes at the SAME version under both accounts: "already have it" must also mean "fetched for this
        // library", or the store keeps the team's record and the person's own skill is refused for ever.
        var gateway = new FakeGateway();
        gateway.Account("team-key", "gw-1", "team-a", "team-a");
        gateway.Account("person-key", "gw-1", "tenant-person", null);
        gateway.Serve("team-key", "both", "SAME");
        gateway.Serve("person-key", "both", "SAME");

        await Refresh("director", gateway, "https://devthrottle.com", "team-key");
        Install("director", TeamAFile);
        await Refresh("director", gateway, "https://devthrottle.com", "person-key");

        Assert.Empty(Install("director", NoTeamFile).Problems);
        Assert.Equal("personal", AccountStampedOn(Path.Combine(Shared, "both")));
    }

    [Fact]
    public void A_skill_in_the_store_recorded_for_another_library_is_never_placed()
    {
        // Where the bytes hit the disk: placement checks each skill's own recorded source as well, so a store
        // that holds another library's bytes for any reason cannot put them out under this library's name.
        var store = StoreOf("director");
        Directory.CreateDirectory(store);
        SkillDirectoryInstaller.Materialize(store, Bundle("both", "TEAM"), new SkillSource("gw-1", "team-a", "team-a"));
        SkillDirectoryInstaller.Materialize(store, Bundle("unrecorded", "OLD"), null);
        SkillDirectoryInstaller.Materialize(store, Bundle("mine", "PERSONAL"), PersonalSource);

        var placement = SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, store, new SkillInstallPaths(Shared, LinkRoot),
            Path.Combine(_root, "director", "reclaimed.txt"), PersonalSource);

        Assert.Equal(new[] { "both", "unrecorded" },
            placement.Problems.Where(p => p.Fault == SkillPlacementFault.SourceMismatch).Select(p => p.SkillId).OrderBy(n => n));
        Assert.False(Directory.Exists(Path.Combine(Shared, "both")));
        Assert.False(Directory.Exists(Path.Combine(Shared, "unrecorded")));
        Assert.Equal("personal", AccountStampedOn(Path.Combine(Shared, "mine")));
    }

    // ---- SK-F7: refresh and placement never interleave over the store -------------------------------------------

    [Fact]
    public async Task A_refresh_that_lands_while_placement_is_copying_never_gets_its_bytes_stamped_with_the_old_source()
    {
        // THE REVIEW'S INTERLEAVING, forced on every round (review finding SK-F7). Placement has read a skill's
        // recorded source and not yet copied its bytes; at exactly that point a refresh after an account move
        // rebuilds the store with the other library's bytes. Placement must never copy those bytes under the
        // source it already read. The refresh is given 300 ms to land inside the window - long enough that it
        // always lands when nothing stops it.
        var gateway = new FakeGateway();
        gateway.Account("person-key", "gw-1", "tenant-person", null);
        gateway.Account("team-key", "gw-1", "team-a", "team-a");
        gateway.Serve("person-key", "both", "PERSONAL");
        gateway.Serve("team-key", "both", "TEAM");
        await Refresh("director", gateway, "https://devthrottle.com", "person-key");

        for (var round = 0; round < 20; round++)
        {
            var key = round % 2 == 0 ? "team-key" : "person-key";
            var source = round % 2 == 0 ? PersonalSource : new SkillSource("gw-1", "team-a", "team-a");
            Task? landing = null;
            SkillDirectoryInstaller.SwapStepForTests.Value = (step, _) =>
            {
                if (step != "source-read" || landing is not null)
                    return;
                landing = Task.Run(() => Refresh("director", gateway, "https://devthrottle.com", key));
                landing.Wait(TimeSpan.FromMilliseconds(300));
            };
            try
            {
                PlaceAs(source);
            }
            finally
            {
                SkillDirectoryInstaller.SwapStepForTests.Value = null;
            }
            Assert.NotNull(landing);
            await landing!;

            AssertNeverRelabelled(round);
        }
    }

    [Fact]
    public async Task Refresh_and_placement_racing_freely_for_many_rounds_never_relabel_a_librarys_bytes()
    {
        var gateway = new FakeGateway();
        gateway.Account("person-key", "gw-1", "tenant-person", null);
        gateway.Account("team-key", "gw-1", "team-a", "team-a");
        foreach (var id in new[] { "both", "second", "third" })
        {
            gateway.Serve("person-key", id, "PERSONAL");
            gateway.Serve("team-key", id, "TEAM");
        }
        await Refresh("director", gateway, "https://devthrottle.com", "person-key");

        for (var round = 0; round < 60; round++)
        {
            var key = round % 2 == 0 ? "team-key" : "person-key";
            var refresh = Task.Run(() => Refresh("director", gateway, "https://devthrottle.com", key));
            var place = Task.Run(() => PlaceAs(round % 3 == 0 ? new SkillSource("gw-1", "team-a", "team-a") : PersonalSource));
            await Task.WhenAll(refresh, place);
            AssertNeverRelabelled(round);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Place this Director's store into the shared folder only (no links, so a round is fast), as
    /// <paramref name="source"/>.</summary>
    private SkillPlacement PlaceAs(SkillSource source) =>
        SkillDirectoryInstaller.InstallFor(
            AgentKind.Codex, StoreOf("director"), new SkillInstallPaths(Shared, null),
            Path.Combine(_root, "director", "reclaimed.txt"), source);

    /// <summary>Every placed copy carries the stamp of the library its bytes came from.</summary>
    private void AssertNeverRelabelled(int round)
    {
        if (!Directory.Exists(Shared))
            return;
        foreach (var folder in Directory.GetDirectories(Shared))
        {
            var body = File.ReadAllText(Path.Combine(folder, "SKILL.md"));
            var account = AccountStampedOn(folder);
            if (body.Contains("TEAM"))
                Assert.True(account == "team:team-a", $"round {round}: the team's bytes in '{folder}' are stamped '{account}'");
            else
                Assert.True(account == "personal", $"round {round}: the person's bytes in '{folder}' are stamped '{account}'");
        }
    }

    private static readonly SkillSource PersonalSource = new("gw-1", "tenant-person", null);

    private static SkillBundle Bundle(string id, string body) =>
        new(id, 1, "hash-" + body, "A skill.", new[] { id }, $"# {id}\n\n{body}\n", Array.Empty<SkillFileBytes>());

    /// <summary>The account line of the source stamp on a placed skill folder.</summary>
    private static string? AccountStampedOn(string folder) =>
        File.ReadAllLines(Path.Combine(folder, SkillDirectoryInstaller.MarkerFileName))
            .Where(l => l.StartsWith(SkillSource.AccountKey, StringComparison.Ordinal))
            .Select(l => l[SkillSource.AccountKey.Length..])
            .SingleOrDefault();

    private async Task Refresh(string director, FakeGateway gateway, string url, string key)
    {
        var refresh = new SkillStoreRefresh(StoreOf(director), gateway.Client, url, key, new HeldGatewayAnswers());
        Assert.True(await refresh.RefreshAsync() >= 0);
    }

    private SkillPlacement Install(string director, DirectorTeam? teamFile) =>
        SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, StoreOf(director), new SkillInstallPaths(Shared, LinkRoot),
            Path.Combine(_root, director, "reclaimed.txt"), sourceOverride: null, configuredTeam: () => teamFile);

    /// <summary>A Gateway serving the skills register and versions per bearer key, answering at ANY address.</summary>
    private sealed class FakeGateway
    {
        private readonly Dictionary<string, (string GatewayId, string TenantId, string? TeamId)> _accounts = new();
        private readonly Dictionary<string, Dictionary<string, string>> _skills = new();
        private readonly HashSet<(string Key, string Id)> _unreadable = new();
        public bool NamesTheSource { get; init; } = true;
        public HttpClient Client { get; }

        public FakeGateway() => Client = new HttpClient(new Handler(this));

        public void Account(string key, string gatewayId, string tenantId, string? teamId)
        {
            _accounts[key] = (gatewayId, tenantId, teamId);
            _skills.TryAdd(key, new Dictionary<string, string>());
        }

        public void Serve(string key, string id, string body) => _skills[key][id] = body;
        public void Withdraw(string key, string id) => _skills[key].Remove(id);

        /// <summary>The register still lists the skill, but reading its version fails - a request that failed,
        /// not a skill that was withdrawn.</summary>
        public void FailDetail(string key, string id) => _unreadable.Add((key, id));
        public void Recover(string key, string id) => _unreadable.Remove((key, id));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var key = request.Headers.Authorization?.Parameter ?? "";
            if (!_accounts.TryGetValue(key, out var account))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var library = _skills[key];
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/gateway/skills")
            {
                var rows = library.Select(kv => new { id = kv.Key, version = Version(kv.Value), enabled = true, contentHash = kv.Value });
                return NamesTheSource
                    ? Json(new { skills = rows, source = new { gatewayId = account.GatewayId, tenantId = account.TenantId, teamId = account.TeamId } })
                    : Json(new { skills = rows });
            }
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 5 && _unreadable.Contains((key, Uri.UnescapeDataString(parts[2]))))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (parts.Length == 5 && library.TryGetValue(Uri.UnescapeDataString(parts[2]), out var body))
            {
                return Json(new
                {
                    version = Version(body), summary = "A skill.", triggers = new[] { parts[2] },
                    bodyMarkdown = $"# {parts[2]}\n\n{body}\n", files = Array.Empty<object>(), contentHash = body,
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        /// <summary>A body's version: a new body is a new version, as a publish makes it.</summary>
        private static int Version(string body) => Math.Abs(body.GetHashCode() % 100000) + 1;

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

        private sealed class Handler(FakeGateway owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(owner.Respond(request));
        }
    }
}
