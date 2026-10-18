using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The pure parts of the 80% no-room alarm: which account has room again first, and when
/// (<see cref="LicenseDecisionEngine.FirstBackWithRoom"/>), and the alarm's words when the account emails are too long for
/// an alarm's sentence (<see cref="LicenseReadinessFold.NoRoomAlarmConditions"/>). The alarm opening, staying and closing
/// over the real store is held down in <see cref="LicenseReadinessServiceTests"/>.
/// </summary>
public sealed class LicenseNoRoomAlarmTests
{
    private static LicenseAccountReading Reading(string email, double five, DateTime? fiveResets, double week, DateTime? weekResets,
        DateTime? readAt = null)
        => new(email, new LicenseWindowReading(five, fiveResets), new LicenseWindowReading(week, weekResets), readAt ?? T0, false);

    private static LicenseSnapshot Snapshot(params LicenseAccountReading[] accounts)
        => Snapshot(Array.Empty<LicenseBar>(), accounts);

    private static LicenseSnapshot Snapshot(IReadOnlyList<LicenseBar> bars, params LicenseAccountReading[] accounts)
        => new(T0, accounts, Array.Empty<LicenseMachineState>(), bars, Array.Empty<LicenseExhaustion>());

    // Of two full accounts, the one whose full windows have all reset first is named - not the one whose weekly limit
    // resets first while its 5-hour window is still full.
    [Fact]
    public void FirstBackWithRoom_TwoFullAccounts_NamesTheOneWhoseFullWindowsAllResetFirst()
    {
        var snapshot = Snapshot(
            Reading(A, 50, T0.AddHours(1), 85, T0.AddDays(3)),
            Reading(B, 98, T0.AddHours(30), 99, T0.AddHours(20)),
            Reading(C, 10, T0.AddHours(1), 99, T0.AddHours(40)));

        var back = LicenseDecisionEngine.FirstBackWithRoom(snapshot, A);

        Assert.Equal(new LicenseAccountReturn(B, T0.AddHours(30)), back);
    }

    // A bar on an account that ends after its reset puts its return at the end of the bar.
    [Fact]
    public void FirstBackWithRoom_ABarEndingAfterTheReset_IsWhenItComesBack()
    {
        var snapshot = Snapshot(new[] { new LicenseBar(B, LicenseWindowNames.SevenDay, T0.AddHours(50)) },
            Reading(A, 50, T0.AddHours(1), 85, T0.AddDays(3)),
            Reading(B, 10, T0.AddHours(1), 99, T0.AddHours(20)));

        Assert.Equal(new LicenseAccountReturn(B, T0.AddHours(50)), LicenseDecisionEngine.FirstBackWithRoom(snapshot, A));
    }

    // A full window with no reset time, or one whose reset has already passed, says nothing about when the account has
    // room again: that account is left out, and with no other the answer is that no return can be told.
    [Fact]
    public void FirstBackWithRoom_NoResetTimeAhead_IsLeftOut()
    {
        var snapshot = Snapshot(
            Reading(A, 50, T0.AddHours(1), 85, T0.AddDays(3)),
            Reading(B, 10, T0.AddHours(1), 99, null),
            Reading(C, 98, T0.AddMinutes(-5), 20, T0.AddDays(2)));

        Assert.Null(LicenseDecisionEngine.FirstBackWithRoom(snapshot, A));
    }

    // Two account emails of the longest kind do not fit one alarm sentence: the sentence then calls the account "the
    // account in use", keeps the time, and stays within an alarm's limit, so the check is never refused.
    [Fact]
    public void NoRoomAlarmConditions_LongAccountEmails_StillFitAnAlarmSentence_AndKeepTheTime()
    {
        var longA = new string('a', 300) + "@example.com";
        var longB = new string('b', 300) + "@example.com";
        var snapshot = Snapshot(
            Reading(longA, 50, T0.AddHours(1), 85, T0.AddDays(3)),
            Reading(longB, 10, T0.AddHours(1), 99, T0.AddHours(20)));
        var judged = new[]
        {
            new LicenseMachineReadiness(Guid.NewGuid(), "machine-a", "user", "machine-a", longA, null, LicenseReadinessStates.NoRoom,
                null, null, null, null, null, LicenseReadinessFold.NoRoomText, LicenseTones.Neutral, null),
        };

        var condition = Assert.Single(LicenseReadinessFold.NoRoomAlarmConditions(snapshot, judged, Array.Empty<LicenseAlarmEntity>()));

        Assert.Equal("The account in use is at 85% of its weekly limit and no other account has room to switch its machines to; "
                     + "the first has room again at 2026-10-11 23:12 UTC.", condition.Problem);
        Assert.Equal((null, null, longA), (condition.MachineId, condition.MachineName, condition.AccountEmail));
        Assert.True(condition.Problem.Length <= LicenseAlarmService.MaxSentenceLength);
    }
}
