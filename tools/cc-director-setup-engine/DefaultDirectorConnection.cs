using System.Runtime.InteropServices;
using CcDirector.Core.Configuration;
using CcDirector.Core.Instances;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The default Director of an install, as the setup command line must see it to connect it before it
/// has ever started: the folder it will read its connection from, and the device id it will present.
///
/// WHY THIS EXISTS (issue #3506). Every Director - the default one included - runs in its own home,
/// <c>&lt;machine root&gt;\instances\&lt;slug&gt;</c>, and reads its gateway connection only from there. The
/// installed product (the app, the tools, the launcher) stays at the machine root. <c>enroll</c> runs from
/// the machine root, and used to write the connection there too - where no Director reads it - and to mint
/// a throwaway device id the Director never presents. The Director then started with no Gateway, and
/// connecting it from inside would have enrolled the same machine a second time.
///
/// The in-app wizard does not have this problem because it runs INSIDE the Director: it writes into the
/// Director's own home and enrolls with the Director's own id. This class gives the command line exactly
/// those two answers for the default instance, computed by the same rules the Director uses
/// (<see cref="InstanceContext.InstanceHomeOf"/>, <see cref="DirectorIdentitySlot"/>), so the two paths
/// produce the same machine.
/// </summary>
public sealed class DefaultDirectorConnection
{
    /// <summary>The default Director's storage home: <c>&lt;install root&gt;\instances\default</c>.</summary>
    public string StorageRoot { get; }

    /// <summary>The executable the default Director runs as - the first half of its identity slot key.</summary>
    public string DirectorExecutable { get; }

    private DefaultDirectorConnection(string storageRoot, string directorExecutable)
    {
        StorageRoot = storageRoot;
        DirectorExecutable = directorExecutable;
    }

    /// <summary>The default Director of <paramref name="layout"/>'s install, on this machine's platform.</summary>
    public static DefaultDirectorConnection For(InstallLayout layout) => For(layout, HostPlatform.Current);

    /// <summary>The default Director of <paramref name="layout"/>'s install on <paramref name="platform"/>.</summary>
    public static DefaultDirectorConnection For(InstallLayout layout, OSPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new DefaultDirectorConnection(
            InstanceContext.InstanceHomeOf(layout.LocalRoot, InstanceContext.DefaultSlug),
            layout.DirectorExecutablePath(platform));
    }

    /// <summary>The id file the default Director will read its identity from.</summary>
    public string DirectorIdFile => DirectorIdentitySlot.FilePathFor(
        DirectorIdentitySlot.DirectoryFor(StorageRoot), SlotKey);

    private string SlotKey => DirectorIdentitySlot.KeyFor(DirectorExecutable, InstanceContext.DefaultSlug);

    /// <summary>
    /// The default Director's id: the one already on disk, or a fresh one written into the Director's own
    /// slot so that the Director, when it starts, reuses it as its id. Enrolling with this id is what keeps
    /// a command-line install to ONE device on the account.
    /// </summary>
    public string LoadOrCreateDirectorId()
    {
        var id = DirectorIdentitySlot.LoadOrCreate(DirectorIdFile, SlotKey);
        EngineLog.Write($"[DefaultDirectorConnection] LoadOrCreateDirectorId: id={id}, file={DirectorIdFile}");
        return id;
    }

    /// <summary>The default Director's current gateway connection, read from its own home.</summary>
    public GatewayConfig LoadGateway() => GatewayConfig.LoadFrom(StorageRoot);

    /// <summary>
    /// Persist an enrolled (gateway url, per-device key) pair into the default Director's own home - the
    /// persist step handed to <see cref="GatewayAccountEnrollRunner"/>. The key is never logged (DT-05).
    /// </summary>
    public void SaveEnrolledKey(string gatewayUrl, string deviceKey)
    {
        GatewayCredentialStore.SaveEnrolledKeyAt(StorageRoot, gatewayUrl, deviceKey);
        EngineLog.Write($"[DefaultDirectorConnection] SaveEnrolledKey: connection written to the default Director's home {StorageRoot} (url={gatewayUrl})");
    }
}
