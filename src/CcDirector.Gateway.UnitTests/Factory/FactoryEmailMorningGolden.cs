using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Email;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The 08:00 email of a fixed set of inputs as the composer made it BEFORE the 16:00 and midnight checks were removed
/// (one factory email a day, the owner's yes of 26 October 2026): composed on origin/main at b6d6acbc6 and hashed there,
/// so <see cref="FactoryEmailTests"/> proves the 08:00 email is byte-for-byte what it was. A change that MEANS to alter
/// the 08:00 email updates these hashes in the same pull request, and says why.
/// </summary>
internal static class FactoryEmailMorningGolden
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    /// <summary>Each case's name, then the SHA-256 of its subject, HTML and plain text, as composed at b6d6acbc6.</summary>
    public static readonly (string Case, string Subject, string Html, string Text)[] Hashes =
    {
        ("problems-linked", "1292E9F19654AD66BF4DE2982499EDC3B915BB1717A93B531B00555906F0708C", "6D4B1A027CDE859149A6549D1A7680735E11F729708C53055C37DB22E322E4B5", "C77CC7529F67D5C42CEF9D67B416F558807E0E9EE97CCCEC6A2196C67D51F4CE"),
        ("all-clear-unlinked", "71FE107675B97251430A45D488F606CFE7557BFC457CD77292F57BA9293F512D", "7BAA832D8723218F2565CB0D61B8AADEC3B282D4B6B4582DDE7AA65107F3D01C", "7D6FF1E8BF60AE80920922F62D9841FF0D06F6E8978A1B20ED2886616E637DD7"),
        ("needs-you-truncated", "01DDCD7B5B7C454EA83C83E503DC2BFCA71FF454966D246E50E4A09118BF0409", "135458140EA7131D5A7799AF63DD5981BD99DDDC4B71EEBC48427B3CC8273500", "15F3E5A6EFD3914CFD0771E096FBFCB1418EC39E7F6BA21A3184B7DE9F62BDD1"),
        ("daily-mockup", "09F0D12EE7813B4CAAC4749C3829D3FEFAEBC6FBBD1C18EB8EEFB0901E85B35A", "70B22D39155C9CE8D28B513BC28A1D05BA6180B908FBE906D40BA53D4AB8F0FF", "CAF045E22A549F56B14FE6442DFB8A347446288F2FF2463DB682F723B92FDF4A"),
    };

    public static string Sha256(string text)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));

    private static DateTime TorontoTime(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), Toronto);

    /// <summary>The fixed inputs, by case name. Pure: no clock, no store.</summary>
    public static IReadOnlyList<(string Case, FactoryEmailInput Input)> Cases()
    {
        var morning = FactoryEmailSlots.AtBoundary(TorontoTime(2026, 10, 11, 8), Toronto);
        var factories = new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase)
        {
            ["outreach"] = new("Outreach", new Dictionary<string, string> { ["sender"] = "Sender" }),
            ["research"] = new("Research", new Dictionary<string, string> { ["scout"] = "Scout" }),
            ["website"] = new("Website", new Dictionary<string, string> { ["boss"] = "Boss" }),
            ["old"] = new("Old", new Dictionary<string, string>(), Archived: true),
        };
        var problems = new[]
        {
            Problem("research", "scout", "Night scan", TorontoTime(2026, 10, 11, 2), CronRunProblems.DidNotRun, "its machine was offline", started: false),
            Problem("outreach", "sender", "Morning send", TorontoTime(2026, 10, 10, 9), CronRunProblems.DidNotReport, "the session ended"),
            Problem(null, "boss", "Loose job", TorontoTime(2026, 10, 9, 17, 5), CronRunProblems.Reported, "build <failed> - caf\u00e9"),
            Problem("website", "boss", "Mail check", TorontoTime(2026, 10, 11, 3), CronRunProblems.CheckFailed, "the output is not JSON"),
        };
        var days = new[]
        {
            new CronFactoryDayDto { Factory = "outreach", Done = 5, OpenProblems = 1, DoneRuns =
            {
                new CronFactoryDoneRunDto { Seat = "sender", JobName = "Morning send", FiredUtc = TorontoTime(2026, 10, 10, 9), Reported = "Sent 4 emails" },
                new CronFactoryDoneRunDto { Seat = "sender", JobName = "Morning send", FiredUtc = TorontoTime(2026, 10, 10, 13), Reported = null },
            } },
            new CronFactoryDayDto { Factory = "research", Done = 3, DidNotRun = 1, OpenProblems = 1 },
            new CronFactoryDayDto { Factory = "website", Done = 6, LeftOpen = 1, NothingToDo = 2, CheckFailed = 1, OpenProblems = 1 },
        };
        var waiting = new[]
        {
            new FactoryActivityDto
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Factory = "outreach", FactoryAgent = "sender",
                Subject = "a reply asks for a price", What = "a draft is ready.", Outcome = FactoryActivityOutcome.Asked,
                Actor = "session-1", OccurredUtc = TorontoTime(2026, 10, 11, 6), RecordedUtc = TorontoTime(2026, 10, 11, 6),
            },
            new FactoryActivityDto
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Factory = "research", FactoryAgent = "scout",
                Subject = null, What = "the source site changed its login.", Outcome = FactoryActivityOutcome.Escalated,
                Actor = "session-2", OccurredUtc = TorontoTime(2026, 10, 10, 22), RecordedUtc = TorontoTime(2026, 10, 10, 22),
            },
        };

        return new[]
        {
            ("problems-linked", new FactoryEmailInput(morning, Toronto, problems, days, factories, "https://gateway.example.test")),
            ("all-clear-unlinked", new FactoryEmailInput(morning, Toronto, Array.Empty<CronRunProblemDto>(),
                new[] { new CronFactoryDayDto { Factory = "outreach", Done = 4 } }, factories, null)),
            ("needs-you-truncated", new FactoryEmailInput(morning, Toronto, problems.Take(1).ToList(), days, factories,
                "https://gateway.example.test", NeedsYou: new FactoryEmailNeedsYou(waiting, Truncated: true))),
            ("daily-mockup", new FactoryEmailInput(FactoryEmailSlots.AtBoundary(TorontoTime(2026, 11, 1, 8), Toronto), Toronto,
                Array.Empty<CronRunProblemDto>(), days, factories, "https://gateway.example.test",
                NeedsYou: new FactoryEmailNeedsYou(waiting.Take(1).ToList(), Truncated: false))),
        };
    }

    private static CronRunProblemDto Problem(string? factory, string seat, string job, DateTime atUtc, string kind, string reason,
        bool started = true)
    {
        var raised = atUtc.AddMinutes(10);
        var asOf = TorontoTime(2026, 10, 11, 8, 5);
        var local = TimeZoneInfo.ConvertTimeFromUtc(atUtc, Toronto).ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new()
        {
            RunId = "run-" + job.Replace(' ', '-'),
            JobId = "cj_" + seat,
            JobName = job,
            Factory = factory,
            Seat = seat,
            ScheduledUtc = atUtc,
            StartedUtc = started ? atUtc : null,
            Shift = WorkShifts.ShiftOf(atUtc, Toronto).Name,
            Kind = kind,
            Reason = reason,
            Sentence = CronRunResultFold.SentenceOf(kind, reason),
            WhenText = (started ? "Started " : "Due ") + local,
            RaisedAtUtc = raised,
            OpenForText = CronRunResultFold.OpenFor(asOf - raised),
            State = CronRunProblemStates.Open,
        };
    }
}
