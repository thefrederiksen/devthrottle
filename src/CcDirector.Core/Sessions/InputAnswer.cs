namespace CcDirector.Core.Sessions;

/// <summary>The three things that can happen to input sent to a session (issue #4380).</summary>
public enum InputAnswerKind
{
    /// <summary>It reached the terminal.</summary>
    Written,

    /// <summary>It is kept and will reach the terminal after the send in front of it, in order - as a person's keys are
    /// held while a send is being typed.</summary>
    Held,

    /// <summary>Nothing of it reached the terminal, and the reason says why.</summary>
    Refused,
}

/// <summary>
/// EVERY INPUT ROUTE ANSWERS (issue #4380): written, held with the reason, or refused with the reason - never silence.
/// Input used to be dropped with only a log line to show for it: the usage-limit guard discarded remote keystrokes and a
/// terminal-socket caller was told nothing at all. A route now hands one of these back to whoever sent the input, and
/// that caller passes it on to the person.
/// </summary>
public sealed record InputAnswer(InputAnswerKind Kind, string? Reason)
{
    /// <summary>It reached the terminal.</summary>
    public static InputAnswer Written { get; } = new(InputAnswerKind.Written, null);

    /// <summary>It is kept and written after the send in front of it.</summary>
    public static InputAnswer Held(string reason) => new(InputAnswerKind.Held, reason);

    /// <summary>Nothing of it reached the terminal.</summary>
    public static InputAnswer Refused(string reason) => new(InputAnswerKind.Refused, reason);

    /// <summary>The one wire word for the kind: "written", "held" or "refused".</summary>
    public string Word => Kind switch
    {
        InputAnswerKind.Written => "written",
        InputAnswerKind.Held => "held",
        _ => "refused",
    };

    public override string ToString() => Reason is null ? Word : $"{Word} ({Reason})";
}
