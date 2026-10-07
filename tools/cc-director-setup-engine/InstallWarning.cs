namespace CcDirector.Setup.Engine;

/// <summary>
/// A component that did not install WITHOUT failing the install (<see cref="InstallCompletion.WarningStatus"/>),
/// with the facts the Complete screen's words depend on. Today that is the launcher, which only adds autostart.
/// </summary>
/// <param name="ComponentId">The component identifier (<c>cc-launcher</c>).</param>
/// <param name="Reason">Why it did not install, as the install runner recorded it for the row.</param>
/// <param name="Step">Which step failed: <see cref="PlaceStep"/> (the file never reached the disk) or <see cref="StartStep"/> (it did, and did not start). The same words the report carries.</param>
/// <param name="ReportAccepted">DevThrottle accepted the report of this failure.</param>
public sealed record InstallWarning(string ComponentId, string Reason, string Step, bool ReportAccepted)
{
    /// <summary>The step that downloads and places the file.</summary>
    public const string PlaceStep = "place";

    /// <summary>The step that registers and starts what was placed.</summary>
    public const string StartStep = "start";

    /// <summary>The line the screen shows: the display name and the reason.</summary>
    public string Line => $"{ComponentDisplayName.For(ComponentId)}: {Reason}";

    /// <summary>Is this the launcher?</summary>
    public bool IsLauncher => string.Equals(ComponentId, ComponentRegistry.Launcher.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file is on disk; only the start failed.</summary>
    public bool Placed => !string.Equals(Step, PlaceStep, StringComparison.OrdinalIgnoreCase);
}
