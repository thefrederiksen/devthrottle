namespace CcDirector.Gateway.Factory.Definition;

/// <summary>The two severities a finding has.</summary>
public static class DefinitionSeverity
{
    /// <summary>Refuses a save, a publish and a restore.</summary>
    public const string Error = "error";

    /// <summary>Never refuses; returned with the success.</summary>
    public const string Notice = "notice";
}

/// <summary>
/// One thing the whole-definition check found. <see cref="File"/> is a path in the definition (or the place a missing
/// file belongs, such as <c>seats/</c>; empty only for a finding about the version as a whole). <see cref="Line"/> is
/// 1-based, and 0 when the finding is about the file as a whole. <see cref="Message"/> is one plain sentence saying
/// what is wrong and what to do.
/// </summary>
public sealed record DefinitionFinding(string Severity, string File, int Line, string Message)
{
    /// <summary>True when the finding refuses a save.</summary>
    public bool IsError => Severity == DefinitionSeverity.Error;
}

/// <summary>
/// What the check returns: the findings (errors first, then by file and line, at most
/// <see cref="FactoryDefinitionCheck.MaxFindings"/> and one more saying how many were not shown), and the parsed
/// definition when there is no error.
/// </summary>
public sealed record FactoryDefinitionCheckResult(IReadOnlyList<DefinitionFinding> Findings, FactoryDefinition? Definition)
{
    /// <summary>True when any finding is an error, so the content may not be saved, published or restored.</summary>
    public bool HasErrors => Findings.Any(f => f.IsError);
}
