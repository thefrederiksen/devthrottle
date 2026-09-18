namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// A real, throwaway storage root on disk with CC_DIRECTOR_ROOT pointed at it for the life of the
/// test, restored on dispose.
///
/// It exists so the machine-root tests can ask the question the way the product is asked it: a
/// Director's folder arrives through that setting, not through a parameter, and a resolver that is
/// only ever handed a hand-built string has never watched the environment it will actually meet.
///
/// Two shapes, because the rule has two halves:
///   - a MACHINE root with a Director's folder under it, pinned at the Director's folder - the shape
///     every Director on a real computer produces, and the one the tools must climb out of;
///   - a RIG root of its own, pinned directly - the shape a test rig produces, and the one that must
///     come back untouched and keep its own tools.
///
/// Nothing here ever touches the real root: the folders are made under the system temporary directory
/// and deleted again.
/// </summary>
internal sealed class PinnedRoot : IDisposable
{
    private const string RootVariable = "CC_DIRECTOR_ROOT";

    private readonly string? _previous;
    private readonly string _scratch;

    /// <summary>A machine root with a Director's folder under it; the Director's folder is pinned.</summary>
    public PinnedRoot(string machineRootName, string directorSlug)
    {
        _previous = Environment.GetEnvironmentVariable(RootVariable);
        _scratch = NewScratchDirectory();

        Machine = Path.Combine(_scratch, machineRootName);
        Director = Path.Combine(Machine, "instances", directorSlug);
        Directory.CreateDirectory(Director);

        Pinned = Director;
        Environment.SetEnvironmentVariable(RootVariable, Pinned);
    }

    /// <summary>A rig's own root, pinned directly. There is no Director's folder and no machine above it.</summary>
    public PinnedRoot(string rigRootName)
    {
        _previous = Environment.GetEnvironmentVariable(RootVariable);
        _scratch = NewScratchDirectory();

        Machine = Path.Combine(_scratch, rigRootName);
        Director = Machine;
        Directory.CreateDirectory(Machine);

        Pinned = Machine;
        Environment.SetEnvironmentVariable(RootVariable, Pinned);
    }

    /// <summary>The machine root: where the installed tools belong.</summary>
    public string Machine { get; }

    /// <summary>The Director's own data folder. Equal to <see cref="Machine"/> for a rig root.</summary>
    public string Director { get; }

    /// <summary>Whatever CC_DIRECTOR_ROOT was set to for this test.</summary>
    public string Pinned { get; }

    /// <summary>Create <paramref name="parts"/> as a directory under the machine root and return it.</summary>
    public string MachineDirectory(params string[] parts)
    {
        var path = Path.Combine(new[] { Machine }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Write <paramref name="content"/> to a file under the machine root and return its path.</summary>
    public string MachineFile(string content, params string[] parts)
    {
        var path = Path.Combine(new[] { Machine }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Write <paramref name="content"/> to a file under the Director's own folder and return its path.</summary>
    public string DirectorFile(string content, params string[] parts)
    {
        var path = Path.Combine(new[] { Director }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootVariable, _previous);

        // A scratch directory that will not delete is not a test failure - it is a file someone else
        // still holds open, and reporting it as one would make an unrelated test look broken.
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string NewScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cc-machine-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
