using System.Linq;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The setup wizard's hosted gateway card makes no offer of its own (the Pricing Change mission, item B5).
///
/// The card used to end "Included with Pro - and with the free 14-day Pro trial that new accounts get when
/// they first connect here. No credit card." That sentence was true when it was written and is false now:
/// no new account is granted a trial. A wizard is the worst place for a promise like that, because it ships
/// inside the Director and goes on being read for as long as that build is installed, while what each plan
/// includes is decided on the Gateway and can change the same afternoon.
///
/// So the line is plan-neutral: no trial, no amount, no number of days, no named plan - and the pricing
/// page, which is always current, is where it points. Both places the sentence lives are checked: the text
/// on the card, and the name a screen reader is given for the card, which had drifted before.
/// </summary>
public class GatewayHostedCardWordingTests
{
    private static readonly string[] PromisesTheCardMayNotMake =
    {
        "trial", "credit card", "no card", "free", "included with", "Pro",
    };

    [AvaloniaFact]
    public void ThePlanLineMakesNoOfferAndPointsAtThePricingPage()
    {
        var wizard = new FirstRunWizardDialog(new AgentOptions());

        var text = wizard.GatewayHostedPlanText.Text ?? "";

        AssertPlanNeutral(text);
        Assert.Contains("devthrottle.com/pricing", text);
    }

    [AvaloniaFact]
    public void TheNameAScreenReaderIsGivenMakesNoOfferEither()
    {
        var wizard = new FirstRunWizardDialog(new AgentOptions());

        var name = AutomationProperties.GetName(wizard.GatewayHostedCard) ?? "";

        AssertPlanNeutral(name);
        Assert.Contains("devthrottle.com/pricing", name);
    }

    /// <summary>
    /// The card's opening line promises only what every plan gets. It used to add "phone access, voice,
    /// and the morning report work immediately", which a plan without them would make false.
    /// </summary>
    [AvaloniaFact]
    public void TheOpeningLinePromisesOnlyWhatEveryPlanGets()
    {
        var wizard = new FirstRunWizardDialog(new AgentOptions());

        var text = wizard.GatewayHostedSummaryText.Text ?? "";

        Assert.Equal("We run it for you. Sign in and this machine is enrolled.", text);
        var name = AutomationProperties.GetName(wizard.GatewayHostedCard) ?? "";
        foreach (var feature in new[] { "phone", "voice", "morning report", "immediately" })
        {
            Assert.DoesNotContain(feature, text, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(feature, name, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertPlanNeutral(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), "the card must still say where the plans are");
        foreach (var promise in PromisesTheCardMayNotMake)
        {
            // "Pro" is matched as a whole word in its own casing: it is the plan's name, and a plain
            // substring match would trip over every ordinary word that happens to contain it.
            var found = promise == "Pro"
                ? text.Split(' ', ',', '.', '-', ';', ':').Contains("Pro")
                : text.Contains(promise, System.StringComparison.OrdinalIgnoreCase);
            Assert.False(found, $"the hosted gateway card must not say \"{promise}\": {text}");
        }
        // No amount and no number of days: the card carries no digit at all.
        Assert.False(text.Any(char.IsDigit), $"the hosted gateway card must carry no number: {text}");
        Assert.DoesNotContain("$", text);
    }
}
