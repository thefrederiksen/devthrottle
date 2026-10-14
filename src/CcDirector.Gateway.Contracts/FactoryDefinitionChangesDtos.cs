namespace CcDirector.Gateway.Contracts;

/// <summary>
/// What changes between two versions of a factory's definition (Factory Definition, phase 1; mission section 5.3).
/// Every sentence is composed on the Gateway; the command line and the Cockpit render it verbatim and decide nothing
/// (rule 7). Lists are in a fixed order: by seat id, then by path or id.
/// </summary>
public sealed class FactoryDefinitionChangesDto
{
    /// <summary>One line at the top: "2 seats changed, 1 start moved, rules tightened on outreach", or "Nothing changes.".</summary>
    public string Summary { get; set; } = "";

    /// <summary>True when the two versions define the same factory and hold the same text.</summary>
    public bool NothingChanges { get; set; }

    /// <summary>True when nothing is published yet, so everything is listed as added.</summary>
    public bool FirstPublish { get; set; }

    /// <summary>factory.yaml fields that change.</summary>
    public List<FactoryDefinitionFieldChangeDto> Factory { get; set; } = new();

    /// <summary>Seats added, removed and changed, by seat id.</summary>
    public List<FactoryDefinitionSeatChangeDto> Seats { get; set; } = new();

    /// <summary>Starts created, moved, changed and switched off, by seat id.</summary>
    public List<FactoryDefinitionStartChangeDto> Starts { get; set; } = new();

    /// <summary>Rules tightened or loosened: per rule set, then per seat taking or dropping one.</summary>
    public List<FactoryDefinitionRuleChangeDto> Rules { get; set; } = new();

    /// <summary>Grants added, removed and changed, by id.</summary>
    public List<FactoryDefinitionItemChangeDto> Grants { get; set; } = new();

    /// <summary>Files, checks and factory skills added, removed and changed, by path, each with its line difference.</summary>
    public List<FactoryDefinitionFileChangeDto> Files { get; set; } = new();
}

/// <summary>One field of factory.yaml that changes.</summary>
public sealed class FactoryDefinitionFieldChangeDto
{
    /// <summary>The field, as the file spells it: <c>title</c>, <c>defaults.machine</c>, <c>repositories</c>.</summary>
    public string Field { get; set; } = "";

    /// <summary>The change in words: "title DevThrottle -> DevThrottle Works".</summary>
    public string Sentence { get; set; } = "";

    /// <summary>The line difference, for a field holding many lines (the goal text); otherwise null.</summary>
    public FactoryDefinitionTextDiffDto? Diff { get; set; }
}

/// <summary>A seat added, removed or changed.</summary>
public sealed class FactoryDefinitionSeatChangeDto
{
    /// <summary>The seat id.</summary>
    public string Seat { get; set; } = "";

    /// <summary><c>added</c>, <c>removed</c> or <c>changed</c>.</summary>
    public string Change { get; set; } = "";

    /// <summary>The change in words: "Seat Outreach (outreach) changed.".</summary>
    public string Sentence { get; set; } = "";

    /// <summary>Each field that changes, in words: "machine SOREN_NORTH -> devlinux".</summary>
    public List<string> Details { get; set; } = new();

    /// <summary>The brief's line difference, or null when the brief does not change.</summary>
    public FactoryDefinitionTextDiffDto? BriefDiff { get; set; }
}

/// <summary>One start of a seat created, moved, changed or switched off.</summary>
public sealed class FactoryDefinitionStartChangeDto
{
    /// <summary>The seat id.</summary>
    public string Seat { get; set; } = "";

    /// <summary><c>created</c>, <c>moved</c>, <c>changed</c> or <c>switched off</c>.</summary>
    public string Change { get; set; } = "";

    /// <summary>The change in words: "Outreach: start moved, Weekdays 08:00 Europe/Copenhagen -> Weekdays 09:00 Europe/Copenhagen.".</summary>
    public string Sentence { get; set; } = "";

    /// <summary>Each other part of the start that changes, in words: "check checks/a.py -> checks/b.py".</summary>
    public List<string> Details { get; set; } = new();
}

/// <summary>Rules tightened or loosened on one rule set or one seat.</summary>
public sealed class FactoryDefinitionRuleChangeDto
{
    /// <summary>The rule set's name when the change is inside a rule set; otherwise null.</summary>
    public string? RuleSet { get; set; }

    /// <summary>The seat id when the change is a seat taking or dropping a rule set; otherwise null.</summary>
    public string? Seat { get; set; }

    /// <summary><c>tightened</c> or <c>loosened</c>.</summary>
    public string Direction { get; set; } = "";

    /// <summary>The change in words: "Rule set outreach tightened: deny Bash(rm:*) added.".</summary>
    public string Sentence { get; set; } = "";
}

/// <summary>A grant added, removed or changed.</summary>
public sealed class FactoryDefinitionItemChangeDto
{
    /// <summary>The grant id.</summary>
    public string Id { get; set; } = "";

    /// <summary><c>added</c>, <c>removed</c> or <c>changed</c>.</summary>
    public string Change { get; set; } = "";

    /// <summary>The change in words.</summary>
    public string Sentence { get; set; } = "";

    /// <summary>Each field that changes, in words.</summary>
    public List<string> Details { get; set; } = new();
}

/// <summary>A file, check or factory skill file added, removed or changed, with its full line difference.</summary>
public sealed class FactoryDefinitionFileChangeDto
{
    /// <summary>The path in the definition.</summary>
    public string Path { get; set; } = "";

    /// <summary><c>file</c>, <c>check</c>, <c>skill</c>, or <c>definition</c> for a definition file (factory.yaml, a seat, grants.yaml, a rule set) reported by its text because nothing it defines changes: its layout or comments change, or a rule set holding no rules is added or removed.</summary>
    public string Kind { get; set; } = "";

    /// <summary><c>added</c>, <c>removed</c> or <c>changed</c>.</summary>
    public string Change { get; set; } = "";

    /// <summary>The change in words: "Check checks/new_mail.py changed: 2 lines added, 1 removed.".</summary>
    public string Sentence { get; set; } = "";

    /// <summary>The line difference.</summary>
    public FactoryDefinitionTextDiffDto Diff { get; set; } = new();
}

/// <summary>
/// A line difference between two texts: the changed lines with a few lines of context around them, at most a capped
/// number of lines, and how many were left out.
/// </summary>
public sealed class FactoryDefinitionTextDiffDto
{
    /// <summary>How many lines are added in all (shown or not).</summary>
    public int Added { get; set; }

    /// <summary>How many lines are removed in all (shown or not).</summary>
    public int Removed { get; set; }

    /// <summary>The lines to show, in order.</summary>
    public List<FactoryDefinitionDiffLineDto> Lines { get; set; } = new();

    /// <summary>How many lines of the difference are not shown because of the cap.</summary>
    public int LinesNotShown { get; set; }

    /// <summary>A sentence to show after the lines, or null: how many were left out, or why the lines are not matched one by one.</summary>
    public string? Note { get; set; }
}

/// <summary>One line of a line difference.</summary>
public sealed class FactoryDefinitionDiffLineDto
{
    /// <summary><c>context</c>, <c>added</c>, <c>removed</c>, or <c>skipped</c> for a run of unchanged lines not shown.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The mark to show before the text: "+", "-", " ", or "" for a skipped run.</summary>
    public string Marker { get; set; } = "";

    /// <summary>The line's text, or for a skipped run the sentence saying how many lines it holds.</summary>
    public string Text { get; set; } = "";

    /// <summary>The 1-based line number in the published text, or null for an added line or a skipped run.</summary>
    public int? OldLine { get; set; }

    /// <summary>The 1-based line number in the compared text, or null for a removed line or a skipped run.</summary>
    public int? NewLine { get; set; }
}
