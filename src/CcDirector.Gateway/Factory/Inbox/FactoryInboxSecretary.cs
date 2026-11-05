using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Talk;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// THE SECRETARY'S SEAT AND THE WAKE'S WORDS (Factory Inbox mission, phase 2).
///
/// A SEAT NOBODY DEFINES. Every factory with a Boss has a Secretary, and no definition names it - the seat name
/// <see cref="FactoryInboxSeats.Secretary"/> is reserved, so no definition can. The Gateway makes the seat up from the
/// published definition at the moment it is needed (<see cref="SeatOf"/>): on the Boss's computer, with the Boss's agent,
/// model and rule sets - the factory's rules - and a brief that is the Secretary's job and the inbox commands. Nothing
/// else: no grants, no files, no secrets, no factory skills, no fleet skills and no repository (it works in its kit's
/// home). The seat start and the kit are then built exactly as every seat's are (<see cref="FactorySeatStart.Build"/>,
/// <c>Api.FactoryKitEndpoints</c>), so the Director needs nothing new, and the session's history row carries the mark the
/// inbox routes key on: the factory, the seat <c>secretary</c> and a seat kit.
///
/// Only a DEFINED factory can have one: a kit is built from a published version, and a Director stamps a seat only on a
/// create that names one.
/// </summary>
public static class FactoryInboxSecretary
{
    /// <summary>The Secretary's name and role, as its seat carries them.</summary>
    public const string SeatName = "Secretary";

    /// <summary>The Secretary's role word.</summary>
    public const string Role = "Secretary";

    /// <summary>
    /// The Secretary's seat for <paramref name="definition"/>, made up from its Boss seat, or null when the definition
    /// names no Boss seat it holds.
    /// </summary>
    public static SeatDefinition? SeatOf(FactoryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var boss = definition.Seats.FirstOrDefault(s => string.Equals(s.Seat, definition.BossSeat?.Trim(), StringComparison.Ordinal));
        if (boss is null) return null;
        return new SeatDefinition(
            Seat: FactoryInboxSeats.Secretary,
            Name: SeatName,
            Role: Role,
            Line: null,
            Machine: boss.MachineOrDefault(definition.Defaults),
            Agent: boss.AgentOrDefault(definition.Defaults),
            Model: boss.ModelOrDefault(definition.Defaults),
            Repository: null,
            Brief: Brief(definition.Title, definition.Factory),
            Starts: [],
            Grants: [],
            Rules: boss.Rules,
            Secrets: [],
            FactorySkills: [],
            FleetSkills: [],
            Files: [],
            Steps: []);
    }

    /// <summary>The definition with its Secretary seat added, or null when it names no Boss seat it holds. The kit route
    /// composes a Secretary's kit from this.</summary>
    public static FactoryDefinition? WithSecretary(FactoryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (SeatOf(definition) is not { } secretary) return null;
        return definition with { Seats = definition.Seats.Where(s => s.Seat != secretary.Seat).Append(secretary).ToList() };
    }

    /// <summary>
    /// The Secretary's job, in full: its brief, and the first thing it reads. It never judges, never decides and never
    /// starts a session; it files, merges repeats, attaches what each message is about, writes the owner's Good or Bad
    /// note into the factory's memory for the Boss, hands ONE bundle, and closes itself when the inbox is empty.
    /// </summary>
    public static string Brief(string factoryTitle, string factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        return
            $"You are the Secretary of {factoryTitle} (factory id: {factory}). You file this factory's inbox: the messages the " +
            "owner leaves for its Boss, typed or spoken, from the phone or the Cockpit. You never judge a message, never decide " +
            "one and never start a session or a seat - the Boss decides every message, and you hand them to it.\n\n" +
            "1. See what waits to be filed: cc-devthrottle factory inbox new\n" +
            "2. Receive it: cc-devthrottle factory inbox receive. When a message repeats another, merge it into the one it " +
            "repeats (--merge <id>:<the id it repeats>). When a message is about a run, a report or a record and names it, " +
            "attach it (--link <id>:run|report|record:<its id>).\n" +
            "3. When a message carries the owner's Good or Bad note, write one short note into the factory's memory saying what " +
            "the owner judged good or bad and why, as a judgement for the Boss: cc-devthrottle factory memory set <name> \"<text>\"\n" +
            "4. Hand everything you received to the Boss as ONE bundle: cc-devthrottle factory inbox bundle --hand\n" +
            "5. Run cc-devthrottle factory inbox new again. When it is empty and your bundle is handed, close this session: " +
            "cc-devthrottle session done. If you are told of new messages while you are still working, stay open and file them " +
            "the same way.";
    }

    /// <summary>The Secretary's first prompt: its job, and how many messages wait now.</summary>
    public static string Opening(RegisteredFactoryDto factory, int waiting)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Brief(factory.Title, factory.Factory) + "\n\n" + Count(waiting, "message", "messages") + " wait to be filed now.";
    }

    /// <summary>The Secretary session's name: &lt;Factory&gt; - Secretary - file the factory inbox.</summary>
    public static string SessionName(RegisteredFactoryDto factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return $"{factory.Title} - Secretary - file the factory inbox";
    }

    /// <summary>The one line a running Secretary is rung with.</summary>
    public static string RingLine(int waiting)
        => $"{Count(waiting, "new factory inbox message", "new factory inbox messages")} - run cc-devthrottle factory inbox new";

    /// <summary>
    /// The Boss's first prompt when the wake starts it for a bundle: who it is and its brief (the night-start opening's
    /// words, <see cref="FactorySeatOpening"/>), where its kit is, and what to do with the bundle. The seat start adds the
    /// Boss's standing paragraphs (<see cref="FactorySeatStart.Build"/>).
    /// </summary>
    /// <param name="inAKit">A defined factory's Boss starts with a seat kit, and is told where it is.</param>
    public static string BossOpening(RegisteredFactoryDto factory, RegisteredFactorySeatDto boss, FactoryGivenBrief brief, int bundles,
        bool inAKit)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(boss);
        ArgumentNullException.ThrowIfNull(brief);
        return FactorySeatOpening.WhoYouAre(factory, boss) + "\n\n"
               + $"Your brief is given to you here in full, as the Gateway holds it ({brief.VersionWords}).\n\n"
               + FactorySeatOpening.BriefBlock(brief, "\n") + "\n\n"
               + (inAKit ? FactorySeatOpening.KitSentence + "\n\n" : "")
               + BossBundleWork(bundles);
    }

    /// <summary>The Boss's first prompt in an UNDEFINED factory whose brief is still a file: told the file's path, as its
    /// talk is, then what to do with the bundle.</summary>
    public static string BossOpeningFromAFile(RegisteredFactoryDto factory, RegisteredFactorySeatDto boss, int bundles)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(boss);
        return FactorySeatOpening.WhoYouAre(factory, boss) + "\n\n"
               + $"Read your brief first: {FactoryTalkSeed.InFolder(factory.Folder, boss.BriefFile)}\n\n"
               + BossBundleWork(bundles);
    }

    /// <summary>What the Boss does with a bundle.</summary>
    public static string BossBundleWork(int bundles)
        => $"You were started because your Secretary has handed you {Count(bundles, "bundle", "bundles")} of messages the owner " +
           "left in this factory's inbox. Read them: cc-devthrottle factory inbox bundle. Decide every message, one decision " +
           "each, with cc-devthrottle factory inbox decide <id>: delegated --session <the session you started to do it>, todo, " +
           "noted, declined --why \"...\", cannot --why \"...\", or pushback --why \"...\". Judge each one: when the owner asks " +
           "for something that should not be done, push back and say why. When every message is decided and the sessions you " +
           "started have come back, close this session: cc-devthrottle session done.";

    /// <summary>The Boss session's name when the wake starts it.</summary>
    public static string BossSessionName(RegisteredFactoryDto factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return $"{factory.Title} - Boss - decide the factory inbox";
    }

    /// <summary>The one line a running Boss is rung with.</summary>
    public static string BossRingLine(int bundles)
        => $"{Count(bundles, "new factory inbox bundle", "new factory inbox bundles")} from your Secretary - run cc-devthrottle factory inbox bundle";

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
