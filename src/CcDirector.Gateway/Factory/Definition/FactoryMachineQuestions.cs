using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>What a publish asks one machine: the secret names and repository names of the seats that run there.</summary>
public sealed record MachineQuestion(string Machine, IReadOnlyList<string> Secrets, IReadOnlyList<string> Repositories);

/// <summary>
/// What a publish learned from one machine: its <see cref="Answer"/>, or the sentence saying why it could not be asked
/// (<see cref="NotAsked"/>) - no connected Director there, a Director too old to be asked, no answer in time. Exactly
/// one of the two is set.
/// </summary>
public sealed record MachineReply(FactoryMachineAnswer? Answer, string? NotAsked)
{
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
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _send;

    /// <param name="connectedDirectorsOf">The account's running Directors whose tunnel is connected now.</param>
    /// <param name="answers">Whether a Director said on Hello that it answers the question.</param>
    /// <param name="send">Sends a command down a Director's tunnel.</param>
    public FactoryMachineQuestionAsker(Func<TenantId, IReadOnlyList<DirectorDto>> connectedDirectorsOf,
        Func<TenantId, string, bool> answers, DirectorCommandRouter.SendDirectorCommandAsync send)
    {
        _connectedDirectorsOf = connectedDirectorsOf ?? throw new ArgumentNullException(nameof(connectedDirectorsOf));
        _answers = answers ?? throw new ArgumentNullException(nameof(answers));
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    /// <summary>
    /// The questions a definition raises: for each machine a seat runs on (its own, else the factory default), the
    /// seats' secret names (seat and start secrets) and repository names, each once, in the order first named. A
    /// machine with nothing to ask is not asked. Null (a definition that does not parse) raises none.
    /// </summary>
    public static IReadOnlyList<MachineQuestion> QuestionsFor(FactoryDefinition? definition)
    {
        if (definition is null) return Array.Empty<MachineQuestion>();
        var byMachine = new Dictionary<string, (string Machine, List<string> Secrets, List<string> Repositories)>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in definition.Seats)
        {
            var machine = seat.Machine ?? definition.Defaults.Machine;
            if (!byMachine.TryGetValue(machine, out var question))
                byMachine[machine] = question = (machine, new List<string>(), new List<string>());
            foreach (var secret in seat.Secrets.Concat(seat.Starts.SelectMany(s => s.Secrets)))
                if (!question.Secrets.Contains(secret, StringComparer.Ordinal)) question.Secrets.Add(secret);
            if (seat.Repository is { } repository && !question.Repositories.Contains(repository, StringComparer.Ordinal))
                question.Repositories.Add(repository);
        }
        return byMachine.Values
            .Where(q => q.Secrets.Count > 0 || q.Repositories.Count > 0)
            .Select(q => new MachineQuestion(q.Machine, q.Secrets, q.Repositories))
            .ToList();
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

        var director = onMachine.FirstOrDefault(d => _answers(tenant, d.DirectorId));
        if (director is null)
        {
            var version = onMachine[0].Version;
            return NotAsked(machine, string.IsNullOrWhiteSpace(version)
                ? $"the Director on {machine} is too old to be asked. Update it."
                : $"the Director on {machine} is version {version}, which is too old to be asked. Update it.");
        }

        var result = await DirectorCommandRouter.TrySendAsync(_send, director.DirectorId, FactoryMachineQuestionContract.Verb, "",
            new FactoryMachineQuestionRequest { Secrets = question.Secrets.ToList(), Repositories = question.Repositories.ToList() },
            ct, Timeout, machine);
        if (result is null)
            return NotAsked(machine, $"no Director on {machine} is connected now.");
        if (result.Status == DirectorCommandStatus.Timeout)
            return NotAsked(machine, $"the Director on {machine} did not answer within {Timeout.TotalSeconds:0} seconds.");
        if (result.Status != DirectorCommandStatus.Ok)
            return NotAsked(machine, $"the Director on {machine} did not answer the question ({result.Error}).");

        var answer = Read(result.BodyJson);
        if (answer is null)
            return NotAsked(machine, $"the Director on {machine} answered in a shape the Gateway does not read.");
        FileLog.Write($"[FactoryMachineQuestionAsker] {machine}: answered by director={director.DirectorId}");
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
