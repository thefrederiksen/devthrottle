namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE MACHINE QUESTION (Factory Definition, phase 5, design section 6): what the Gateway asks one machine about a
/// defined factory's seats there, and what that machine's Director answers. Sent as the director-level verb
/// <see cref="Verb"/> only to a Director whose Hello says <see cref="DirectorStreamHello.AnswersFactoryMachineQuestion"/>.
///
/// The answer is open to more fields: phase 5b adds the Python and uv answers beside the secrets and repositories,
/// so a reader ignores what it does not know and a writer never removes a field. Those two are asked only of a
/// Director whose Hello also says <see cref="DirectorStreamHello.AnswersFactoryCheckMachineQuestion"/>.
/// </summary>
public static class FactoryMachineQuestionContract
{
    /// <summary>The verb on the Director command channel, addressed to no session.</summary>
    public const string Verb = "factory-machine-question";
}

/// <summary>What the Gateway asks: the secret names and repository names of the seats that run on one machine.</summary>
public sealed class FactoryMachineQuestionRequest
{
    /// <summary>The cc-secrets entry names the seats list. Never a value.</summary>
    public List<string> Secrets { get; set; } = new();

    /// <summary>The repository names the seats work in.</summary>
    public List<string> Repositories { get; set; } = new();

    /// <summary>
    /// Phase 5b: a start on this machine has a check that declares no packages, so it runs under plain Python - say
    /// whether the Python the check runner would use is here. A Director too old to know the field ignores it.
    /// </summary>
    public bool AskPython { get; set; }

    /// <summary>Phase 5b: a start on this machine has a check that declares packages, so it runs under uv - say whether
    /// the uv the check runner would use is here.</summary>
    public bool AskUv { get; set; }
}

/// <summary>What one machine answers.</summary>
public sealed class FactoryMachineAnswer
{
    /// <summary>The machine that answered.</summary>
    public string Machine { get; set; } = "";

    /// <summary>
    /// Null when cc-secrets answered. Otherwise the sentence saying why it could not (not installed, failed, did not
    /// finish, answered in a shape that cannot be read) - and then <see cref="Secrets"/> is empty, because a cc-secrets
    /// that could not answer is never read as "not held".
    /// </summary>
    public string? SecretsUnavailable { get; set; }

    /// <summary>One answer per secret name asked, in the order asked. Empty when <see cref="SecretsUnavailable"/> is set.</summary>
    public List<FactorySecretAnswer> Secrets { get; set; } = new();

    /// <summary>One answer per repository name asked, in the order asked.</summary>
    public List<FactoryRepositoryAnswer> Repositories { get; set; } = new();

    /// <summary>Phase 5b: whether the check runner's Python is here. Null when it was not asked.</summary>
    public FactoryProgramAnswer? Python { get; set; }

    /// <summary>Phase 5b: whether the check runner's uv is here. Null when it was not asked.</summary>
    public FactoryProgramAnswer? Uv { get; set; }
}

/// <summary>Whether one program a check runs under is on this machine, found exactly as the check runner finds it.</summary>
public sealed class FactoryProgramAnswer
{
    /// <summary>The name looked for on PATH, as this machine's runner names it (<c>python.exe</c>, <c>python3</c>,
    /// <c>uv.exe</c>, <c>uv</c>).</summary>
    public string LookedFor { get; set; } = "";

    /// <summary>It was found.</summary>
    public bool Found { get; set; }

    /// <summary>Where it was found; null when it was not.</summary>
    public string? Path { get; set; }
}

/// <summary>Whether this machine holds one secret, and whether it may be used for <c>cc-secrets run</c>.</summary>
public sealed class FactorySecretAnswer
{
    /// <summary>The entry name asked about.</summary>
    public string Name { get; set; } = "";

    /// <summary>A cc-secrets entry of that name exists on this machine.</summary>
    public bool Held { get; set; }

    /// <summary>The entry is allowed for <c>run</c>. False when it is not held.</summary>
    public bool AllowedForRun { get; set; }

    /// <summary>When it is held but not allowed for run: cc-secrets' own words for that refusal. Null otherwise.</summary>
    public string? NotAllowedReason { get; set; }
}

/// <summary>Whether one repository name is registered on this machine.</summary>
public sealed class FactoryRepositoryAnswer
{
    /// <summary>The repository name asked about.</summary>
    public string Name { get; set; } = "";

    /// <summary>The name is in this Director's repository registry.</summary>
    public bool Registered { get; set; }

    /// <summary>The registered folder when it is registered; null otherwise.</summary>
    public string? Path { get; set; }
}
