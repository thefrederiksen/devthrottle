using System.Text.Json;
using CcDirector.Core.Factory;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// What a publish asks one machine: the secret names and repository names of the seats that run there, and (phase 5b)
/// whether the Python or the uv a start's check runs under is there - <paramref name="AskPython"/> when a check there
/// declares no packages, <paramref name="AskUv"/> when one does, exactly as the Director's check runner chooses.
/// </summary>
public sealed record MachineQuestion(string Machine, IReadOnlyList<string> Secrets, IReadOnlyList<string> Repositories,
    bool AskPython = false, bool AskUv = false);

/// <summary>What a start's check runs under, as the Director's check runner chooses it.</summary>
public enum CheckProgram
{
    /// <summary>No program to ask about: no check, a missing check, or a header that cannot be read.</summary>
    None,

    /// <summary>The check declares no packages and runs under plain Python.</summary>
    Python,

    /// <summary>The check declares packages and runs under <c>uv run --script</c>.</summary>
    Uv,
}

/// <summary>
/// What a publish learned from one machine: its <see cref="Answer"/>, or the sentence saying why it could not be asked
/// (<see cref="NotAsked"/>) - no connected Director there, a Director too old to be asked, no answer in time. Exactly
/// one of the two is set.
/// </summary>
public sealed record MachineReply(FactoryMachineAnswer? Answer, string? NotAsked)
{
    /// <summary>
    /// Phase 5b: set when the machine was asked and answered, but its Director is too old to answer about Python or
    /// uv - the sentence saying so. The secrets and repositories in <see cref="Answer"/> still stand.
    /// </summary>
    public string? ChecksNotAnswered { get; init; }

    public static MachineReply Answered(FactoryMachineAnswer answer) => new(answer ?? throw new ArgumentNullException(nameof(answer)), null);

    public static MachineReply CouldNotAsk(string why) => new(null, why ?? throw new ArgumentNullException(nameof(why)));
}

/// <summary>
/// THE PUBLISH ASKS THE MACHINES (Factory Definition, phase 5, design section 6). Before the whole-definition check runs
/// for a publish or a restore, this asks each seat's machine - one question per machine, with that machine's seats'
/// secret names and repository names - and the answers are handed INTO the check, which stays a pure function that
/// reads no network (<see cref="FactoryDefinitionCheck.Check"/>).
///
/// The question goes only to a running, connected Director on that machine whose Hello said it answers it; machines
/// are asked at once, each with <see cref="Timeout"/>, so a publish waits at most that long for all of them.
/// </summary>
internal sealed class FactoryMachineQuestionAsker
{
    /// <summary>
    /// How long a publish waits for one machine's answer: 15 seconds. A person is waiting on the publish, and the
    /// answer is one local read of the cc-secrets list (about a second) and of the repository registry, so a machine
    /// that has not answered in 15 seconds is not going to help that person; the publish goes on with a notice saying
    /// so. It is longer than the Director's own 10 second limit on cc-secrets
    /// (<see cref="Core.Sessions.FactoryMachineQuestion.DefaultListTimeout"/>), so that one fires first and the answer
    /// names cc-secrets rather than the machine.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<TenantId, IReadOnlyList<DirectorDto>> _connectedDirectorsOf;
    private readonly Func<TenantId, string, bool> _answers;
    private readonly Func<TenantId, string, bool> _answersChecks;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _send;

    /// <param name="connectedDirectorsOf">The account's running Directors whose tunnel is connected now.</param>
    /// <param name="answers">Whether a Director said on Hello that it answers the question.</param>
    /// <param name="answersChecks">Whether a Director said on Hello that it also answers the Python and uv parts.</param>
    /// <param name="send">Sends a command down a Director's tunnel.</param>
    public FactoryMachineQuestionAsker(Func<TenantId, IReadOnlyList<DirectorDto>> connectedDirectorsOf,
        Func<TenantId, string, bool> answers, Func<TenantId, string, bool> answersChecks, DirectorCommandRouter.SendDirectorCommandAsync send)
    {
        _connectedDirectorsOf = connectedDirectorsOf ?? throw new ArgumentNullException(nameof(connectedDirectorsOf));
        _answers = answers ?? throw new ArgumentNullException(nameof(answers));
        _answersChecks = answersChecks ?? throw new ArgumentNullException(nameof(answersChecks));
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    /// <summary>
    /// The questions a definition raises: for each machine a seat runs on (its own, else the factory default), the
    /// seats' secret names (seat and start secrets) and repository names, each once, in the order first named; and
    /// (phase 5b) for each start with a check, Python or uv as <see cref="ProgramFor"/> says. A machine with nothing to
    /// ask is not asked. Null (a definition that does not parse) raises none.
    /// </summary>
    /// <param name="content">The definition's files, for the text of each check.</param>
    public static IReadOnlyList<MachineQuestion> QuestionsFor(FactoryDefinition? definition, IReadOnlyDictionary<string, string> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (definition is null) return Array.Empty<MachineQuestion>();
        var byMachine = new Dictionary<string, Asked>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in definition.Seats)
        {
            var machine = seat.Machine ?? definition.Defaults.Machine;
            if (!byMachine.TryGetValue(machine, out var question))
                byMachine[machine] = question = new Asked(machine);
            foreach (var secret in seat.Secrets.Concat(seat.Starts.SelectMany(s => s.Secrets)))
                if (!question.Secrets.Contains(secret, StringComparer.Ordinal)) question.Secrets.Add(secret);
            if (seat.Repository is { } repository && !question.Repositories.Contains(repository, StringComparer.Ordinal))
                question.Repositories.Add(repository);
            foreach (var start in seat.Starts)
            {
                switch (ProgramFor(start.Check, content))
                {
                    case CheckProgram.Python: question.AskPython = true; break;
                    case CheckProgram.Uv: question.AskUv = true; break;
                }
            }
        }
        return byMachine.Values
            .Where(q => q.Secrets.Count > 0 || q.Repositories.Count > 0 || q.AskPython || q.AskUv)
            .Select(q => new MachineQuestion(q.Machine, q.Secrets, q.Repositories, q.AskPython, q.AskUv))
            .ToList();
    }

    private sealed class Asked(string machine)
    {
        public string Machine { get; } = machine;
        public List<string> Secrets { get; } = new();
        public List<string> Repositories { get; } = new();
        public bool AskPython { get; set; }
        public bool AskUv { get; set; }
    }

    /// <summary>
    /// What a start's check runs under, read from its script header with the Director's own reader
    /// (<see cref="CheckScriptHeader"/>), so the question asked is the program the run will look for: uv when it
    /// declares packages, else Python. <see cref="CheckProgram.None"/> for a start with no check, a path the definition
    /// does not place as a check (any other file, or none: its own error), or a header that cannot be read (the run
    /// fails on the header, not on a program). The path is classified the way the definition check classifies it, so
    /// what is asked and what is worded read the same set of checks.
    /// </summary>
    public static CheckProgram ProgramFor(string? checkPath, IReadOnlyDictionary<string, string> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (checkPath is null || DefinitionPaths.Classify(checkPath).Info?.Place != DefinitionPlace.Check
            || !content.TryGetValue(checkPath, out var text))
            return CheckProgram.None;
        var header = CheckScriptHeader.Read(text);
        if (header.Error is not null) return CheckProgram.None;
        return header.DeclaresPackages ? CheckProgram.Uv : CheckProgram.Python;
    }

    /// <summary>Ask every machine at once. The answer has one reply per question, keyed by machine name (any case).</summary>
    public async Task<IReadOnlyDictionary<string, MachineReply>> AskAsync(TenantId tenant, IReadOnlyList<MachineQuestion> questions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(questions);
        FileLog.Write($"[FactoryMachineQuestionAsker] AskAsync: machines={questions.Count}");
        var directors = questions.Count == 0 ? Array.Empty<DirectorDto>() : _connectedDirectorsOf(tenant);
        var replies = await Task.WhenAll(questions.Select(q => AskOneAsync(tenant, q, directors, ct)));
        var byMachine = new Dictionary<string, MachineReply>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < questions.Count; i++) byMachine[questions[i].Machine] = replies[i];
        FileLog.Write($"[FactoryMachineQuestionAsker] AskAsync: answered={replies.Count(r => r.Answer is not null)}, notAsked={replies.Count(r => r.NotAsked is not null)}");
        return byMachine;
    }

    private async Task<MachineReply> AskOneAsync(TenantId tenant, MachineQuestion question, IReadOnlyList<DirectorDto> directors,
        CancellationToken ct)
    {
        var machine = question.Machine;
        var onMachine = directors
            .Where(d => string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (onMachine.Count == 0)
            return NotAsked(machine, $"no Director on {machine} is connected now.");

        // A machine with a check to answer for prefers a Director that can answer about Python and uv.
        var asksChecks = question.AskPython || question.AskUv;
        var director = (asksChecks ? onMachine.FirstOrDefault(d => _answersChecks(tenant, d.DirectorId)) : null)
            ?? onMachine.FirstOrDefault(d => _answers(tenant, d.DirectorId));
        if (director is null)
        {
            var version = onMachine[0].Version;
            return NotAsked(machine, string.IsNullOrWhiteSpace(version)
                ? $"the Director on {machine} is too old to be asked. Update it."
                : $"the Director on {machine} is version {version}, which is too old to be asked. Update it.");
        }

        var checksAnswered = asksChecks && _answersChecks(tenant, director.DirectorId);
        var result = await DirectorCommandRouter.TrySendAsync(_send, director.DirectorId, FactoryMachineQuestionContract.Verb, "",
            new FactoryMachineQuestionRequest
            {
                Secrets = question.Secrets.ToList(),
                Repositories = question.Repositories.ToList(),
                AskPython = checksAnswered && question.AskPython,
                AskUv = checksAnswered && question.AskUv,
            },
            ct, Timeout, machine);
        if (result is null)
            return NotAsked(machine, $"no Director on {machine} is connected now.");
        if (result.Status == DirectorCommandStatus.Timeout)
            return NotAsked(machine, $"the Director on {machine} did not answer within {Timeout.TotalSeconds:0} seconds.");
        if (result.Status != DirectorCommandStatus.Ok)
            return NotAsked(machine, $"the Director on {machine} did not answer the question ({result.Error}).");

        var answer = Read(result.BodyJson);
        // A Director that said it answers the check parts and then left one out answered in a shape not read here.
        if (answer is null || (checksAnswered && ((question.AskPython && answer.Python is null) || (question.AskUv && answer.Uv is null))))
            return NotAsked(machine, $"the Director on {machine} answered in a shape the Gateway does not read.");
        FileLog.Write($"[FactoryMachineQuestionAsker] {machine}: answered by director={director.DirectorId}, checksAnswered={checksAnswered}");
        if (asksChecks && !checksAnswered)
        {
            var why = string.IsNullOrWhiteSpace(director.Version)
                ? $"the Director on {machine} is too old to answer about Python or uv. Update it."
                : $"the Director on {machine} is version {director.Version}, which cannot answer about Python or uv yet. Update it.";
            FileLog.Write($"[FactoryMachineQuestionAsker] {machine}: check parts not answered - {why}");
            return MachineReply.Answered(answer) with { ChecksNotAnswered = why };
        }
        return MachineReply.Answered(answer);
    }

    private static MachineReply NotAsked(string machine, string why)
    {
        FileLog.Write($"[FactoryMachineQuestionAsker] {machine}: not asked - {why}");
        return MachineReply.CouldNotAsk(why);
    }

    private static FactoryMachineAnswer? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var answer = JsonSerializer.Deserialize<FactoryMachineAnswer>(json, JsonOptions);
            return answer?.Secrets is null || answer.Repositories is null ? null : answer;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
