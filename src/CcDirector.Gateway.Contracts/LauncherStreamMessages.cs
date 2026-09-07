namespace CcDirector.Gateway.Contracts;

/// <summary>
/// launcher-persistent-join: the first message a cc-launcher sends after opening its persistent stream to
/// the Gateway, declaring which machine this connection speaks for. The <see cref="LauncherHub"/> binds the
/// connection to this identity; from then on every command the Gateway pushes DOWN this connection reaches
/// only this machine's launcher, so one connection can never drive another machine's launcher.
///
/// This is the launcher twin of <see cref="DirectorStreamHello"/> (the Director's UP-channel Hello). A
/// launcher only ever RECEIVES commands after Hello - it pushes no session state - so this is the only
/// message it sends up the stream.
///
/// Remove-the-network-port mission, phase 6: Hello used to carry the launcher's loopback REST port for
/// cross-referencing the registry entry. The launcher listens on nothing now, so there is no port to
/// declare - this connection IS the only way a command reaches the launcher.
/// </summary>
public sealed class LauncherStreamHello
{
    /// <summary>The machine name (the same key the launcher registers under via POST /launchers/register).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>Launcher build version, for diagnostics.</summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// What this launcher says it can honour, and the two local facts only it can see - see
    /// <see cref="LauncherCapabilityDeclaration"/>.
    ///
    /// IT RIDES ON HELLO BECAUSE HELLO IS THE ONE MESSAGE A LAUNCHER SENDS. A launcher receives commands
    /// and pushes no state, so there is no second moment to ask it anything; and a capability question
    /// asked over the stream later would be unanswerable in exactly the case that matters, when the
    /// launcher holds no stream. Declared at join time, the answer is already at the Gateway before any
    /// caller needs it, and it is discarded with the connection when that launcher goes.
    ///
    /// NULL IS A REAL AND DIFFERENT STATE. A launcher built before this field sends a Hello without it,
    /// and that is REACHABLE-BUT-SILENT, not incapable. It must never be folded together with a launcher
    /// that sent no Hello at all - see <see cref="LauncherDeclarationState"/>.
    /// </summary>
    public LauncherCapabilityDeclaration? Capability { get; set; }
}
