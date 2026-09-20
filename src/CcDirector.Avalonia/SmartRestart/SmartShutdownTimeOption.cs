namespace CcDirector.Avalonia.SmartRestart;

/// <summary>One entry in the "time allowed" dropdown: the minutes, and the words the owner reads.</summary>
public sealed record SmartShutdownTimeOption(int Minutes, string Label)
{
    public TimeSpan TimeAllowed => TimeSpan.FromMinutes(Minutes);

    public override string ToString() => Label;
}
