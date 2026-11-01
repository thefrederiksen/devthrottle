namespace CcDirector.Gateway.Contracts;

/// <summary>
/// <c>GET /gateway/factory/name-check</c>: every seat of the account's factories that brings back "CEO" or a name
/// that is not a role word (the owner's ruling: "A factory has a boss, not a CEO ... there are no names").
/// </summary>
public sealed class FactoryNameCheckDto
{
    /// <summary>How many findings; 0 when every factory passes.</summary>
    public int Count { get; set; }

    /// <summary>How many factories were checked.</summary>
    public int FactoriesChecked { get; set; }

    /// <summary>The check's heading: "Name check: 2 seats flagged", or "Name check: nothing flagged".</summary>
    public string HeadingText { get; set; } = "";

    /// <summary>The rule, in words.</summary>
    public string RuleText { get; set; } = "";

    /// <summary>"Every factory passes." when there is nothing to report; null otherwise.</summary>
    public string? PassText { get; set; }

    public List<FactoryNameFindingDto> Findings { get; set; } = new();
}

/// <summary>One seat the name check flags.</summary>
public sealed class FactoryNameFindingDto
{
    public string Factory { get; set; } = "";
    public string FactoryTitle { get; set; } = "";
    public string SeatId { get; set; } = "";

    /// <summary>"ceo" or "not-a-role-word".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The finding in one sentence, for example "WarmForward: the seat 'nora-hale' is named 'Nora Hale', which is
    /// not a role word of this factory (its role is Boss)."</summary>
    public string Text { get; set; } = "";
}
