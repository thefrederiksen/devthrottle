using System.Reflection;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The word the owner is shown when a correction is refused, and the word that refusal is recorded under, are ONE
/// word (the Wingman-on-every-turn mission, slice G).
///
/// WHY THIS IS A TEST AND NOT A CONVENTION. The feedback route takes the refusal cause straight off the outcome
/// the service returned, so a rule added to the service reaches the activity ledger without the route learning
/// about it. That is only safe while every refusal code is also a legal ledger cause spelled identically: a code
/// with no cause behind it would be written into the ledger as a word its own validated vocabulary does not hold,
/// and a code spelled differently would put the sentence the owner read and the line in the record under two
/// names for one event. Both are the kind of drift that is invisible until somebody queries the ledger and finds
/// a gap where the refusals should be.
///
/// The codes are read by REFLECTION rather than listed here, so a code added next year is covered on the day it
/// is added rather than on the day somebody remembers this file.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
/// </summary>
public sealed class FeedbackCodesAreLedgerCausesTests
{
    private static IReadOnlyList<(string Name, string Value)> Codes() =>
        typeof(TurnVerdictFeedbackCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .ToList();

    [Fact]
    public void Every_refusal_code_is_a_legal_ledger_cause_spelled_the_same_way()
    {
        var codes = Codes();
        Assert.NotEmpty(codes);

        foreach (var (name, value) in codes)
        {
            if (value == TurnVerdictFeedbackCodes.Recorded) continue;
            Assert.True(ActivityCauses.All.Contains(value),
                $"TurnVerdictFeedbackCodes.{name} is \"{value}\", which is not one of ActivityCauses.All - the "
                + "route writes that word into the ledger on a refusal, so the ledger would be written under a "
                + "cause its own vocabulary does not hold.");
        }
    }

    /// <summary>
    /// The one code with no cause behind it, asserted BY NAME so that it is a decision rather than an omission:
    /// an accepted correction writes a durable row carrying its own moment, word and note, and that row is the
    /// record. If a cause is ever added for it, this goes red and somebody has to say why the row is not enough.
    /// </summary>
    [Fact]
    public void The_accepted_code_is_the_one_with_no_ledger_cause()
    {
        Assert.DoesNotContain(TurnVerdictFeedbackCodes.Recorded, ActivityCauses.All);
        Assert.Contains(ActivityEventTypes.TurnVerdictFeedbackRefused, ActivityEventTypes.All);
    }
}
