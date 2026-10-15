using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Which connected Directors answer the <c>factory-machine-question</c> verb (Factory Definition, phase 5), from what
/// each said about itself on Hello (<see cref="Contracts.DirectorStreamHello.AnswersFactoryMachineQuestion"/>). A
/// stated capability and not a version number, for the reason <see cref="FleetManagerHomeCapabilityRegistry"/> gives;
/// keyed by account AND Director, because a Director id is written by the client.
/// </summary>
public sealed class FactoryMachineQuestionCapabilityRegistry
{
    private readonly ConcurrentDictionary<(TenantId Tenant, string DirectorId), bool> _answers = new();

    /// <summary>Record what a Director said about itself on Hello, under the account its connection is bound to.</summary>
    public void Record(TenantId tenant, string directorId, bool answersFactoryMachineQuestion)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        _answers[(tenant, directorId)] = answersFactoryMachineQuestion;
    }

    /// <summary>Whether this account's Director said it answers the question. False for one that never said so - an
    /// older build, or one this Gateway has not heard from since it started.</summary>
    public bool Answers(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _answers.TryGetValue((tenant, directorId), out var answers) && answers;
}
