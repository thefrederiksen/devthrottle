using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE OFFER OF THE MAIL DESK TEMPLATE AS A DRAFT (Mail Desk mission, design 5.3), over the real store, the real
/// definition check and a real database. No factory: version 1, a draft. The same template again: nothing. A newer
/// template: a new draft, never published. An owner's edit: never overwritten. The switch off: nothing at all. It never
/// publishes in any case.
///
/// The offer's own tests run on the embedded template with a stand-in check text added when the folder does not hold
/// <c>checks/screen.py</c> yet, so they prove the offer whether or not the screening check has landed; the template's
/// own tests (<see cref="MailDeskTemplateTests"/>) prove the real folder.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskTemplateOfferTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryDefinitionStore _store;
    private readonly FactoryDefinitionEndpoints.Account _account;
    private readonly HashSet<TenantId> _switchedOn = new() { A };

    public MailDeskTemplateOfferTests()
    {
        _store = new FactoryDefinitionStore(_h.Open());
        _account = new FactoryDefinitionEndpoints.Account(_ => DefinitionFixture.FleetSkills, _ => new[] { "devlinux" },
            DefinitionFixture.AskNobody);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>The embedded template, with a stand-in check only while the real one has not landed.</summary>
    private static Dictionary<string, string> Template()
    {
        var files = new Dictionary<string, string>(MailDeskTemplate.Files, StringComparer.Ordinal);
        files.TryAdd(MailDeskTemplate.CheckPath, "print('{\"count\": 0}')\n");
        return files;
    }

    /// <summary>A later template: the same folder with the seat's prompt changed.</summary>
    private static Dictionary<string, string> NewerTemplate()
    {
        var files = Template();
        var seat = $"seats/{MailDeskTemplate.Seat}.yaml";
        files[seat] = files[seat].Replace("The Mail Desk screening handed you these messages.",
            "The Mail Desk screening handed you these messages, oldest first.");
        Assert.NotEqual(Template()[seat], files[seat]);
        return files;
    }

    private MailDeskTemplateOffer Offer(IReadOnlyDictionary<string, string>? template = null) =>
        new(_store, _account, tenant => _switchedOn.Contains(tenant), () => Now, template ?? Template());

    private static string Findings(MailDeskTemplateOfferResult result) =>
        string.Join(" | ", result.Findings.Select(f => $"{f.File}:{f.Line} {f.Message}"));

    // ---------- the cases of design 5.3 ----------

    [Fact]
    public void Offer_NoMailDeskFactory_StoresVersionOneDraftWrittenByTheGateway()
    {
        // Arrange
        var offer = Offer();

        // Act
        var result = offer.Offer(A);

        // Assert
        Assert.True(result.Outcome == MailDeskTemplateOfferOutcome.Offered, $"{result.Sentence} {Findings(result)}");
        Assert.Equal(1, result.Version);
        var standing = _store.Standing(A, MailDeskTemplate.Factory);
        Assert.Equal(1, standing.Newest!.Version);
        Assert.Equal(FactoryDefinitionStates.Draft, standing.Newest.State);
        Assert.Null(standing.Published);
        Assert.Equal(FactoryDefinitionStore.GatewayAuthorKind, standing.Newest.AuthorKind);
        Assert.Equal(MailDeskTemplateOffer.AuthorId, standing.Newest.AuthorId);
        Assert.Contains(MailDeskTemplate.Short(offer.TemplateVersion), standing.Newest.ChangeNote);
        Assert.Equal(Template(), _store.Find(A, MailDeskTemplate.Factory, 1)!.Files);
    }

    [Fact]
    public void Offer_SameTemplateVersionTwice_StoresNoSecondVersion()
    {
        // Arrange
        var offer = Offer();
        offer.Offer(A);

        // Act
        var second = offer.Offer(A);

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.AlreadyOffered, second.Outcome);
        Assert.Equal(1, second.Version);
        Assert.Equal(1, _store.Standing(A, MailDeskTemplate.Factory).Newest!.Version);
    }

    [Fact]
    public void Offer_NewerTemplate_StoresANewDraftAndDoesNotPublishIt()
    {
        // Arrange: the first template was offered and the owner published it.
        Offer().Offer(A);
        Assert.True(_store.Publish(A, MailDeskTemplate.Factory, 1, FactoryMemoryAuthorKinds.Person, "owner", Now).Ok);

        // Act
        var result = Offer(NewerTemplate()).Offer(A);

        // Assert
        Assert.True(result.Outcome == MailDeskTemplateOfferOutcome.Offered, $"{result.Sentence} {Findings(result)}");
        Assert.Equal(2, result.Version);
        var standing = _store.Standing(A, MailDeskTemplate.Factory);
        Assert.Equal(2, standing.Newest!.Version);
        Assert.Equal(FactoryDefinitionStates.Draft, standing.Newest.State);
        Assert.Equal(1, standing.Published!.Version);
        Assert.Equal(NewerTemplate(), _store.Find(A, MailDeskTemplate.Factory, 2)!.Files);
    }

    [Fact]
    public void Offer_OwnerEditedTheOfferedDraft_IsNotOverwrittenOrOfferedAgain()
    {
        // Arrange: offered, then the owner saved his own change on top.
        Offer().Offer(A);
        var edited = Template();
        edited["factory.yaml"] = edited["factory.yaml"].Replace("machine: devlinux", "machine: SOREN_NORTH");
        Assert.True(_store.SaveDraft(A, MailDeskTemplate.Factory, edited, 1, FactoryMemoryAuthorKinds.Person, "owner",
            "my machine", Now).Ok);

        // Act
        var result = Offer().Offer(A);

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.AlreadyOffered, result.Outcome);
        Assert.Equal(1, result.Version);
        var standing = _store.Standing(A, MailDeskTemplate.Factory);
        Assert.Equal(2, standing.Newest!.Version);
        Assert.Equal(edited, _store.Find(A, MailDeskTemplate.Factory, 2)!.Files);
    }

    [Fact]
    public void Offer_NewerTemplateAfterTheOwnersEdit_GoesAboveItAndKeepsIt()
    {
        // Arrange
        Offer().Offer(A);
        var edited = Template();
        edited["factory.yaml"] = edited["factory.yaml"].Replace("machine: devlinux", "machine: SOREN_NORTH");
        Assert.True(_store.SaveDraft(A, MailDeskTemplate.Factory, edited, 1, FactoryMemoryAuthorKinds.Person, "owner",
            "my machine", Now).Ok);

        // Act
        var result = Offer(NewerTemplate()).Offer(A);

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.Offered, result.Outcome);
        Assert.Equal(3, result.Version);
        Assert.Equal(edited, _store.Find(A, MailDeskTemplate.Factory, 2)!.Files);
        Assert.Null(_store.Standing(A, MailDeskTemplate.Factory).Published);
    }

    [Fact]
    public void Offer_SwitchOff_LooksAtNothingAndStoresNothing()
    {
        // Arrange: account B never had its Mail Desk switched on.
        var offer = Offer();

        // Act
        var result = offer.Offer(B);

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.SwitchOff, result.Outcome);
        Assert.Null(result.Version);
        Assert.Null(_store.Standing(B, MailDeskTemplate.Factory).Newest);
    }

    [Fact]
    public void Offer_EveryCase_NeverPublishes()
    {
        // Arrange
        var first = Offer();
        var newer = Offer(NewerTemplate());

        // Act: every outcome an offer can have, in turn.
        var results = new[] { first.Offer(A), first.Offer(A), newer.Offer(A), newer.Offer(A), first.Offer(B) };

        // Assert
        Assert.Equal(
            new[]
            {
                MailDeskTemplateOfferOutcome.Offered, MailDeskTemplateOfferOutcome.AlreadyOffered,
                MailDeskTemplateOfferOutcome.Offered, MailDeskTemplateOfferOutcome.AlreadyOffered,
                MailDeskTemplateOfferOutcome.SwitchOff,
            },
            results.Select(r => r.Outcome));
        Assert.Null(_store.Standing(A, MailDeskTemplate.Factory).Published);
        Assert.All(_store.History(A, MailDeskTemplate.Factory).Versions,
            v => Assert.Equal(FactoryDefinitionStates.Draft, v.State));
    }

    [Fact]
    public void Offer_OlderTemplateOfferedAgainAfterANewerOne_IsNotOfferedTwice()
    {
        // Arrange: a rollback of the Gateway brings back a template that was offered before.
        Offer().Offer(A);
        Offer(NewerTemplate()).Offer(A);

        // Act
        var result = Offer().Offer(A);

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.AlreadyOffered, result.Outcome);
        Assert.Equal(1, result.Version);
        Assert.Equal(2, _store.Standing(A, MailDeskTemplate.Factory).Newest!.Version);
    }

    // ---------- review notes A and B ----------

    [Fact]
    public void Offer_TemplateImportedByAPersonThenEdited_IsNotOfferedAgain()
    {
        // Arrange: the owner imported the exact template himself, then changed it; the Gateway never offered it.
        Assert.True(_store.SaveDraft(A, MailDeskTemplate.Factory, Template(), 0, FactoryMemoryAuthorKinds.Person, "owner",
            null, Now).Ok);
        var edited = Template();
        edited["factory.yaml"] = edited["factory.yaml"].Replace("machine: devlinux", "machine: SOREN_NORTH");
        Assert.True(_store.SaveDraft(A, MailDeskTemplate.Factory, edited, 1, FactoryMemoryAuthorKinds.Person, "owner",
            null, Now).Ok);

        // Act
        var result = Offer().Offer(A);

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.AlreadyOffered, result.Outcome);
        Assert.Equal(1, result.Version);
        Assert.Equal(2, _store.Standing(A, MailDeskTemplate.Factory).Newest!.Version);
    }

    [Fact]
    public void Offer_AnotherOfferStoresTheTemplateFirst_AnswersAlreadyOfferedNotRefused()
    {
        // Arrange: the clock is read just before the save, so a second offer run from it lands between this offer's
        // read and its save - the switch turned on while the Gateway's start offers.
        var rivalDone = false;
        var offer = new MailDeskTemplateOffer(_store, _account, _ => true, () =>
        {
            if (!rivalDone)
            {
                rivalDone = true;
                Assert.Equal(MailDeskTemplateOfferOutcome.Offered, Offer().Offer(A).Outcome);
            }
            return Now;
        }, Template());

        // Act
        var result = offer.Offer(A);

        // Assert
        Assert.True(rivalDone);
        Assert.Equal(MailDeskTemplateOfferOutcome.AlreadyOffered, result.Outcome);
        Assert.Equal(1, result.Version);
        Assert.Equal(1, _store.Standing(A, MailDeskTemplate.Factory).Newest!.Version);
    }

    [Fact]
    public void Offer_OwnerSavesDuringTheOffer_LostRaceAndStoresNothing()
    {
        // Arrange: the owner saves his own first version between the offer's read and its save.
        var owners = Template();
        owners["factory.yaml"] = owners["factory.yaml"].Replace("machine: devlinux", "machine: SOREN_NORTH");
        var ownerSaved = false;
        var offer = new MailDeskTemplateOffer(_store, _account, _ => true, () =>
        {
            if (!ownerSaved)
            {
                ownerSaved = true;
                Assert.True(_store.SaveDraft(A, MailDeskTemplate.Factory, owners, 0, FactoryMemoryAuthorKinds.Person,
                    "owner", null, Now).Ok);
            }
            return Now;
        }, Template());

        // Act
        var result = offer.Offer(A);

        // Assert
        Assert.True(ownerSaved);
        Assert.Equal(MailDeskTemplateOfferOutcome.LostRace, result.Outcome);
        Assert.Null(result.Version);
        var standing = _store.Standing(A, MailDeskTemplate.Factory);
        Assert.Equal(1, standing.Newest!.Version);
        Assert.Equal(owners, _store.Find(A, MailDeskTemplate.Factory, 1)!.Files);
    }

    // ---------- the check is the real one ----------

    [Fact]
    public void Offer_TemplateTheCheckRefuses_IsRefusedAndStoresNothing()
    {
        // Arrange: a template whose start names a check the folder does not hold.
        var broken = Template();
        broken.Remove(MailDeskTemplate.CheckPath);

        // Act
        var result = Offer(broken).Offer(A);

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.Refused, result.Outcome);
        Assert.Contains(result.Findings, f => f.IsError && f.Message.Contains(MailDeskTemplate.CheckPath));
        Assert.Null(_store.Standing(A, MailDeskTemplate.Factory).Newest);
    }

    [Fact]
    public void Offer_AccountWithoutDevlinux_IsOfferedWithTheMachineNotice()
    {
        // Arrange: the template names devlinux; this account's Directors are elsewhere.
        var account = new FactoryDefinitionEndpoints.Account(_ => DefinitionFixture.FleetSkills, _ => new[] { "SOREN_NORTH" },
            DefinitionFixture.AskNobody);
        var offer = new MailDeskTemplateOffer(_store, account, _ => true, () => Now, Template());

        // Act
        var result = offer.Offer(A);

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.Offered, result.Outcome);
        Assert.Contains(result.Findings, f => !f.IsError && f.Message.Contains("devlinux"));
    }

    // ---------- who wrote it ----------

    [Fact]
    public void Who_GatewayAuthor_IsSaidAsTheGateway()
    {
        // Arrange + Act
        var words = FactoryBriefPanelFold.Who(FactoryDefinitionStore.GatewayAuthorKind, MailDeskTemplateOffer.AuthorId);

        // Assert
        Assert.Equal("the Gateway (Mail Desk template)", words);
    }

    [Fact]
    public void Publish_GatewayAsPublisher_Throws()
    {
        // Arrange: the Gateway writes drafts only; a publish is a person's or a session's.
        Offer().Offer(A);

        // Act + Assert
        Assert.Throws<ArgumentException>(() =>
            _store.Publish(A, MailDeskTemplate.Factory, 1, FactoryDefinitionStore.GatewayAuthorKind, MailDeskTemplateOffer.AuthorId, Now));
    }

    // ---------- the two callers (phase 3, section 10; task F) ----------

    [Fact]
    public void OfferToEverySwitchedOnAccount_GatewayStart_OffersToAnOnAccountAndNotToAnOffOne()
    {
        // Arrange: A is switched on, B is not.
        var offer = Offer();

        // Act
        var results = offer.OfferToEverySwitchedOnAccount(new[] { A, B });

        // Assert
        var (tenant, result) = Assert.Single(results);
        Assert.Equal(A, tenant);
        Assert.Equal(MailDeskTemplateOfferOutcome.Offered, result!.Outcome);
        Assert.Equal(1, _store.Standing(A, MailDeskTemplate.Factory).Newest!.Version);
        Assert.Null(_store.Standing(B, MailDeskTemplate.Factory).Newest);
    }

    [Fact]
    public void OfferToEverySwitchedOnAccount_ANewerTemplateAfterADeploy_IsOfferedAtStartAsANewDraft()
    {
        // Arrange: the account was offered the template before this deploy.
        Offer().OfferToEverySwitchedOnAccount(new[] { A });

        // Act: the Gateway starts again with a newer template in its binary.
        var results = Offer(NewerTemplate()).OfferToEverySwitchedOnAccount(new[] { A });

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.Offered, Assert.Single(results).Result!.Outcome);
        var standing = _store.Standing(A, MailDeskTemplate.Factory);
        Assert.Equal(2, standing.Newest!.Version);
        Assert.Equal(FactoryDefinitionStates.Draft, standing.Newest.State);
    }

    [Fact]
    public void OfferToEverySwitchedOnAccount_TheSameTemplateAtTheNextStart_OffersNothingNew()
    {
        // Arrange
        Offer().OfferToEverySwitchedOnAccount(new[] { A });

        // Act
        var results = Offer().OfferToEverySwitchedOnAccount(new[] { A });

        // Assert
        Assert.Equal(MailDeskTemplateOfferOutcome.AlreadyOffered, Assert.Single(results).Result!.Outcome);
        Assert.Equal(1, _store.Standing(A, MailDeskTemplate.Factory).Newest!.Version);
    }
}
