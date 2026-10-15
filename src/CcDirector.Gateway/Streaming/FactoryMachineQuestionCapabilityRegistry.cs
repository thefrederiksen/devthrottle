using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Which connected Directors answer the <c>factory-machine-question</c> verb (Factory Definition, phase 5), from what
/// each said about itself on Hello (<see cref="Contracts.DirectorStreamHello.AnswersFactoryMachineQuestion"/>). A
/// stated capability and not a version number, for the reason <see cref="FleetManagerHomeCapabilityRegistry"/> gives;
/// keyed by account AND Director, because a Director id is written by the client.
///
/// Phase 5b adds a second statement, <see cref="Contracts.DirectorStreamHello.AnswersFactoryCheckMachineQuestion"/>:
/// the Director also answers whether the Python and uv its check runner would use are on its machine.
/// </summary>
public sealed class FactoryMachineQuestionCapabilityRegistry
{
    private readonly ConcurrentDictionary<(TenantId Tenant, string DirectorId), (bool Answers, bool AnswersChecks)> _answers = new();

    /// <summary>Record what a Director said about itself on Hello, under the account its connection is bound to.</summary>
    public void Record(TenantId tenant, string directorId, bool answersFactoryMachineQuestion, bool answersFactoryCheckMachineQuestion)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        _answers[(tenant, directorId)] = (answersFactoryMachineQuestion, answersFactoryMachineQuestion && answersFactoryCheckMachineQuestion);
    }

    /// <summary>Whether this account's Director said it answers the question. False for one that never said so - an
    /// older build, or one this Gateway has not heard from since it started.</summary>
    public bool Answers(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _answers.TryGetValue((tenant, directorId), out var said) && said.Answers;

    /// <summary>Whether this account's Director said it also answers the question's Python and uv parts (phase 5b).
    /// Never true for a Director that does not answer the question at all.</summary>
    public bool AnswersChecks(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _answers.TryGetValue((tenant, directorId), out var said) && said.AnswersChecks;
}
