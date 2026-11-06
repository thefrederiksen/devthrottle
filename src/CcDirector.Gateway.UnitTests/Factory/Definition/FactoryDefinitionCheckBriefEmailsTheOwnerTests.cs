using CcDirector.Gateway.Factory.Definition;
using Xunit;
using static CcDirector.Gateway.Tests.Factory.Definition.DefinitionFixture;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// FACTORIES DO NOT EMAIL THE OWNER (the owner's decision, Factory Inbox mission, phase 5 "Email off"): what a seat has to
/// tell him goes into Your inbox. The check flags any seat's brief that tells it to email the owner, as a notice on the
/// brief's line, in the Gateway's words - and leaves email to anyone else, a prohibition, and the owner's own "send me an
/// email" alone.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionCheckBriefEmailsTheOwnerTests
{
    private static List<string> EmailNotices(IReadOnlyDictionary<string, string> content)
        => Check(content).Findings.Where(f => f.Message.StartsWith("This brief tells the seat to email the owner", StringComparison.Ordinal))
            .Select(f => $"{f.File}:{f.Line}:{f.Severity}").ToList();

    [Fact]
    public void Check_AWorkerBriefThatEmailsTheOwner_GetsTheNoticeOnTheBriefLine_AndNoError()
    {
        var content = Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts, then email the owner a summary.");

        var result = Check(content);

        Assert.Equal(new[] { "seats/writer.yaml:4:notice" }, EmailNotices(content));
        AssertNotice(result, "seats/writer.yaml", 4, "\"email the owner\"", "Factories do not email the owner",
            "cc-devthrottle inbox send", "send me an email");
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Check_ABossBriefThatRunsTheOwnerEmailCommand_GetsTheNotice()
    {
        var content = Edit("seats/boss.yaml", "  Run the factory.",
            "  Run the factory. When a run fails, run\n  cc-devthrottle email owner --subject \"failed\".");

        Assert.Equal(new[] { "seats/boss.yaml:4:notice" }, EmailNotices(content));
    }

    [Fact]
    public void Check_TheValidDefinition_HasNoEmailNotice()
    {
        Assert.Empty(EmailNotices(Valid()));
    }

    [Theory]
    [InlineData("Email me when it is done.", "Email me")]
    [InlineData("Then e-mail the owner the result.", "e-mail the owner")]
    [InlineData("Email the report to him every morning.", "Email the report to him")]
    [InlineData("Send the owner an email with the numbers.", "Send the owner an email")]
    [InlineData("Send me a short status email.", "Send me a short status email")]
    [InlineData("Send the summary to the owner by email.", "Send the summary to the owner by email")]
    [InlineData("Escalate with cc-devthrottle email owner.", "cc-devthrottle email owner")]
    [InlineData("POST it to /account/email on the Gateway.", "/account/email")]
    public void BriefEmailsTheOwnerPhrase_FindsEachWayOfEmailingTheOwner(string brief, string phrase)
    {
        Assert.Equal(phrase, FactoryDefinitionCheck.BriefEmailsTheOwnerPhrase(brief));
    }

    [Theory]
    [InlineData("Write the posts.")]
    [InlineData("Email each prospect on the list.")]
    [InlineData("Send the invoice to the customer by email.")]
    [InlineData("Answer every email in the support mailbox.")]
    [InlineData("Tell the owner in Your inbox with cc-devthrottle inbox send.")]
    public void BriefEmailsTheOwnerPhrase_LeavesEmailToOthersAndYourInboxAlone(string brief)
    {
        Assert.Null(FactoryDefinitionCheck.BriefEmailsTheOwnerPhrase(brief));
    }

    [Theory]
    [InlineData("Never email the owner.")]
    [InlineData("Do not email me; use Your inbox.")]
    [InlineData("You must not send the owner an email.")]
    public void BriefEmailsTheOwnerPhrase_AProhibition_IsNotFlagged(string brief)
    {
        Assert.Null(FactoryDefinitionCheck.BriefEmailsTheOwnerPhrase(brief));
    }

    [Fact]
    public void BriefEmailsTheOwnerPhrase_TheOwnersOwnSendMeAnEmail_IsNotFlagged()
    {
        // The owner's decision: a real email goes out when he says "send me an email". A brief holding his words asked
        // for it on purpose.
        Assert.Null(FactoryDefinitionCheck.BriefEmailsTheOwnerPhrase(
            "When the backup fails, send me an email with cc-devthrottle email owner."));
    }

    [Fact]
    public void BriefEmailsTheOwnerPhrase_AnOrderAfterAProhibitionInAnotherClause_IsStillFlagged()
    {
        Assert.Equal("email the owner",
            FactoryDefinitionCheck.BriefEmailsTheOwnerPhrase("Never email customers at night. Then email the owner."));
    }

    [Fact]
    public void BriefEmailsTheOwner_IsTheSentenceAPersonReads()
    {
        Assert.Equal(
            "This brief tells the seat to email the owner (\"email me\"). Factories do not email the owner: what he needs to "
            + "know goes into Your inbox with cc-devthrottle inbox send --subject \"...\" --body \"...\". Reword the brief to "
            + "tell him in Your inbox. A real email goes out only to someone else he names, or when he writes \"send me an email\".",
            FactoryDefinitionCheck.BriefEmailsTheOwner("email me"));
    }
}
