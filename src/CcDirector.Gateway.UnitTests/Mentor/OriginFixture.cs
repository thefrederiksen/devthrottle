using CcDirector.Gateway.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The origin tests mutate one static table (<see cref="Origin.Strips"/>) to watch the no-path property go
/// red, so every test that classifies runs in this one collection and never beside another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OriginCollection
{
    public const string Name = "mentor-origin";
}

/// <summary>
/// The port of the reference's test fixtures (tools/mentor/tests/conftest.py): invented filler text, a
/// prompt record, a turn-submitted event, and local Toronto minutes. The reference builds a data root on
/// disk and loads it through metrics.load_world; the port classifies the same records directly, which is
/// what the fifth inspector's own method did and what origin.classify sees either way.
/// </summary>
internal static class OriginFixture
{
    public static readonly string[] Filler =
    {
        "alpha", "beta", "gamma", "delta", "kappa", "lambda", "sigma", "omega",
        "zulu", "tango", "foxtrot", "bravo", "quebec", "yankee", "juliet", "romeo",
    };

    public static readonly LocalZone Zone = LocalZone.FromIana("America/Toronto");

    private static int _pids;

    /// <summary>Invented prompt text of exactly <paramref name="words"/> words.</summary>
    public static string Words(int words) => string.Join(" ", Enumerable.Range(0, words).Select(i => Filler[i % Filler.Length]));

    /// <summary>'2026-08-25 10:30' in the account's zone as a UTC instant.</summary>
    public static DateTime Local(string text) => Zone.ToUtc(DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", null));

    /// <summary>The UTC instant <paramref name="seconds"/> after the local time <paramref name="baseLocal"/>.</summary>
    public static DateTime At(string baseLocal, int seconds) => Local(baseLocal).AddSeconds(seconds);

    public static MentorRecord Prompt(DateTime ts, string session, string? text = null, int words = 8, string role = "user",
        string? modality = null, string? surface = null, string? context = "c1", string? agent = "ClaudeCode")
    {
        text ??= Words(words);
        return new MentorRecord
        {
            Ts = ts,
            Session = session,
            Context = context,
            Role = role,
            Modality = modality,
            Surface = surface,
            Words = PyTextForTests.CountWords(text),
            Text = text,
            Agent = agent,
            Name = "alpha beta session",
            Pid = "conversation-20260825.jsonl:" + Interlocked.Increment(ref _pids),
        };
    }

    private static long _seq;

    public static MentorEvent Turn(DateTime ts, string session, string? sendSource = null, string? inputOrigin = null)
    {
        var seq = Interlocked.Increment(ref _seq);
        return new MentorEvent
        {
            Ts = ts,
            Seq = seq,
            Session = session,
            Type = "turn-submitted",
            SendSource = sendSource,
            InputOrigin = inputOrigin,
            Where = "activity_events.jsonl:" + seq,
        };
    }

    /// <summary>Classify the records against the events; answer the user records in time order.</summary>
    public static List<MentorRecord> Classify(IReadOnlyList<MentorRecord> prompts, IEnumerable<MentorEvent>? events = null)
    {
        Origin.Classify(prompts, events ?? Array.Empty<MentorEvent>());
        return prompts.Where(p => p.Role == "user").OrderBy(p => p.Ts).ToList();
    }

    public static MentorRecord Only(IReadOnlyList<MentorRecord> prompts, IEnumerable<MentorEvent>? events = null)
    {
        var users = Classify(prompts, events);
        Assert.Single(users);
        return users[0];
    }
}

/// <summary>The one text rule the fixtures need from the product: the product's own word count.</summary>
internal static class PyTextForTests
{
    public static int CountWords(string text) => Origin.CountWords(text);
}
