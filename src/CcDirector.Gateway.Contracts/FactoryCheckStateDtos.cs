namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One checked start of a defined factory and the newest state its check left (Factory Definition, phase 5b, design
/// 1.4), as <c>GET /factory-definitions/{factory}/check-state</c> serves it.
/// </summary>
public sealed class FactoryCheckStateDto
{
    /// <summary>Whose state this is: <c>trigger:&lt;trigger id&gt;</c> or <c>cron:&lt;schedule id&gt;</c>.</summary>
    public string OwnerKey { get; set; } = "";

    /// <summary>The seat the start belongs to.</summary>
    public string Seat { get; set; } = "";

    /// <summary>The start's number: its place in the seat's <c>starts:</c> list, counting every start from 1 - the number
    /// in its trigger's name, <c>&lt;factory&gt;-&lt;seat&gt;-start-&lt;N&gt;</c>.</summary>
    public int Start { get; set; }

    /// <summary>The trigger's or the schedule's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The check's path in the definition.</summary>
    public string CheckPath { get; set; } = "";

    /// <summary>The newest state, or the empty string when the check has stored none (or it was reset).</summary>
    public string Content { get; set; } = "";

    /// <summary>When the newest state was written (UTC), or null when none has been.</summary>
    public DateTime? WrittenUtc { get; set; }

    /// <summary>Who wrote it: a Director id, or <c>person &lt;who&gt;</c> for a reset. Null when none has been.</summary>
    public string? WrittenBy { get; set; }

    /// <summary>The trigger run or scheduled run that wrote it, or null (a reset, or none written).</summary>
    public string? RunRef { get; set; }

    /// <summary>True when the newest row is a person's reset.</summary>
    public bool Reset { get; set; }
}

/// <summary>The answer to <c>GET /factory-definitions/{factory}/check-state</c>: every checked start, by seat and start.</summary>
public sealed class FactoryCheckStateListResponse
{
    public string Factory { get; set; } = "";
    public int Count { get; set; }
    public List<FactoryCheckStateDto> States { get; set; } = new();
}

/// <summary>The body of <c>POST /factory-definitions/{factory}/check-state/reset</c>.</summary>
public sealed class ResetFactoryCheckStateRequest
{
    /// <summary>The start whose state is reset: <c>trigger:&lt;id&gt;</c> or <c>cron:&lt;id&gt;</c>.</summary>
    public string? OwnerKey { get; set; }
}
