using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Factory Definition, phase 5: the MACHINE QUESTION area of the Director's tunnel command surface. One verb,
/// <c>factory-machine-question</c>, addressed to no session: the Gateway asks, while a person publishes or restores a
/// defined factory's definition, whether this machine holds the seats' secrets and has their repositories registered.
/// <see cref="FactoryMachineQuestion"/> answers; this class reads the payload and hands the answer back.
///
/// A payload it cannot read is <see cref="DirectorCommandStatus.BadRequest"/>. Every answer, including "cc-secrets
/// could not answer", is a SUCCESSFUL result carrying a <see cref="FactoryMachineAnswer"/>: the question was answered,
/// and the answer says what it could not find out. It types nothing into any session.
/// </summary>
internal sealed class FactoryMachineQuestionExecutor : ISessionCommandArea
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FactoryMachineQuestion _question;
    private readonly string _machine;

    public FactoryMachineQuestionExecutor() : this(FactoryMachineQuestion.ForThisMachine(), Environment.MachineName)
    {
    }

    /// <param name="question">Test seam: the question with a fake cc-secrets.</param>
    /// <param name="machine">The machine name the answer is given for, as this Director says it on Hello.</param>
    internal FactoryMachineQuestionExecutor(FactoryMachineQuestion question, string machine)
    {
        _question = question ?? throw new ArgumentNullException(nameof(question));
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
    }

    public IReadOnlyCollection<string> Verbs { get; } = new[] { FactoryMachineQuestionContract.Verb };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        if (command.Verb != FactoryMachineQuestionContract.Verb)
            return Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"verb '{command.Verb}' is not handled by the factory machine question area"));

        // The registry is always wired on a real Director (repos-list reads the same one); a missing one is a wiring
        // defect, and answering "not registered" for every repository would hide it.
        var registry = context.Services?.Repositories
            ?? throw new InvalidOperationException("the repository registry is not wired on this Director's tunnel");
        return AnswerAsync(command, registry.Repositories, cancellationToken);
    }

    internal async Task<DirectorCommandResult> AnswerAsync(DirectorCommand command, IReadOnlyList<RepositoryConfig> registered,
        CancellationToken cancellationToken)
    {
        FileLog.Write($"[FactoryMachineQuestionExecutor] {command.Verb}: cmdId={command.CommandId}");

        var request = ReadRequest(command.PayloadJson);
        if (request is null)
        {
            FileLog.Write($"[FactoryMachineQuestionExecutor] {command.Verb}: cmdId={command.CommandId} refused: unreadable payload");
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                "the payload must be a factory machine question: secrets and repositories, each a list of names");
        }

        var answer = await _question.AnswerAsync(_machine, request.Secrets, request.Repositories, registered, cancellationToken);
        FileLog.Write($"[FactoryMachineQuestionExecutor] {command.Verb}: cmdId={command.CommandId}, secrets={answer.Secrets.Count}, " +
                      $"secretsUnavailable={answer.SecretsUnavailable is not null}, repositories={answer.Repositories.Count}");
        return DirectorCommandResult.Success(JsonSerializer.Serialize(answer, JsonOptions));
    }

    /// <summary>The request when the payload reads as one, with no empty or missing name; null otherwise.</summary>
    internal static FactoryMachineQuestionRequest? ReadRequest(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var request = JsonSerializer.Deserialize<FactoryMachineQuestionRequest>(payloadJson, JsonOptions);
            if (request?.Secrets is null || request.Repositories is null) return null;
            if (request.Secrets.Any(string.IsNullOrWhiteSpace) || request.Repositories.Any(string.IsNullOrWhiteSpace)) return null;
            return request;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
