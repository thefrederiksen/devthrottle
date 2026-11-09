using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The rules for a sentence written for a team contributor: no machine of the account, no folder path, no email
/// address. Each rule has a sentence it refuses and sentences near it that it lets through, so a rule that grows
/// too eager fails here before a contributor's ordinary sentence is refused.
/// </summary>
public sealed class ContributorSentenceCheckTests
{
    private static readonly string[] Machines = { "SOREN_NORTH", "devlinux", "dev-machine-1" };

    // ---------------------------------------------------------------------------------------------------
    // A machine name.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("The site was rebuilt on SOREN_NORTH.", "SOREN_NORTH")]
    [InlineData("The site was rebuilt on soren_north tonight.", "SOREN_NORTH")]
    [InlineData("Devlinux sent the new page.", "devlinux")]
    [InlineData("Built on dev-machine-1, then published.", "dev-machine-1")]
    public void Refusal_SentenceNamesAMachine_RefusesNamingTheMachine(string sentence, string machine)
    {
        var refusal = ContributorSentenceCheck.Refusal(sentence, Machines);

        Assert.Equal($"The sentence for a contributor names the machine '{machine}'. A contributor never sees a machine: " +
                     "describe the step without naming it.", refusal);
    }

    [Theory]
    [InlineData("We checked your devlinuxish theme and fixed the colours.")]
    [InlineData("The north entrance photo is now on the home page.")]
    [InlineData("Your dev-machine-10 order page is live.")]
    public void Refusal_WordMerelyContainsAMachineName_Passes(string sentence)
    {
        Assert.Null(ContributorSentenceCheck.Refusal(sentence, Machines));
    }

    [Fact]
    public void Refusal_NoMachinesKnown_PassesAnOrdinarySentence()
    {
        Assert.Null(ContributorSentenceCheck.Refusal("We fixed your contact form.", new[] { "", "  " }));
    }

    // ---------------------------------------------------------------------------------------------------
    // A folder path.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"The photos are in C:\Sites\pine-valley now.")]
    [InlineData("The photos are in d:/sites/pine-valley now.")]
    [InlineData(@"Copied to \\fileserver\share\photos.")]
    [InlineData("Saved under /home/soren/sites.")]
    [InlineData("Saved under /Users/soren/sites.")]
    [InlineData("Saved under /mnt/data/sites.")]
    [InlineData("Saved under ~/sites.")]
    public void Refusal_SentenceHoldsAFolderPath_RefusesWithThePathRule(string sentence)
    {
        var refusal = ContributorSentenceCheck.Refusal(sentence, Machines);

        Assert.Equal("The sentence for a contributor holds a folder path. A contributor never sees a machine: " +
                     "describe the step without the path.", refusal);
    }

    [Theory]
    [InlineData("Your opening hours and/or holiday dates are now on the page.")]
    [InlineData("The new menu goes live at 5:30 tomorrow.")]
    [InlineData("Delivery is free within 10 km/h of the shop, about 1/2 an hour.")]
    [InlineData("The home page and the Users guide were both updated.")]
    [InlineData(@"Prices now show as 5\10 on the sale page.")]
    public void Refusal_SlashOrColonThatIsNotAPath_Passes(string sentence)
    {
        Assert.Null(ContributorSentenceCheck.Refusal(sentence, Machines));
    }

    // ---------------------------------------------------------------------------------------------------
    // An email address.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Replies now go to orders@pinevalley.example.")]
    [InlineData("We wrote to j.smith+shop@mail.example.co.uk for you.")]
    public void Refusal_SentenceHoldsAnEmailAddress_RefusesWithTheEmailRule(string sentence)
    {
        var refusal = ContributorSentenceCheck.Refusal(sentence, Machines);

        Assert.Equal("The sentence for a contributor holds an email address. Describe the step without the address.", refusal);
    }

    [Theory]
    [InlineData("Your contact form now emails you when a customer writes.")]
    [InlineData("Meet us @ the market on Saturday is now on the banner.")]
    [InlineData("Thanks to @pinevalley for the photos.")]
    public void Refusal_AtSignThatIsNotAnAddress_Passes(string sentence)
    {
        Assert.Null(ContributorSentenceCheck.Refusal(sentence, Machines));
    }
}
