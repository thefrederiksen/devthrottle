using System.Runtime.CompilerServices;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The administrator service token is read from ONE process-wide environment variable
/// (<see cref="AdminTrialEndpoint.ServiceTokenEnvVar"/>), and every test class that exercises an
/// administrator route sets it to its own value in its constructor and puts the old one back when it is
/// disposed. This assembly runs four classes at once, so two such classes side by side overwrite each
/// other: a request carrying one class's token meets the other class's value and the gate answers 401
/// where the test expects 400 or 200. That is what failed on Windows continuous integration, on main
/// (run 35137461078) and on unrelated branches alike. Every class that sets the variable joins this
/// collection, which runs alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdminServiceTokenCollection
{
    public const string Name = "Admin service token";
}

/// <summary>
/// Keeps the collection above complete. A new test class that sets the token without joining it brings the
/// race straight back, and nothing else would notice until a runner happened to schedule it badly.
/// </summary>
public sealed class AdminServiceTokenCollectionTests
{
    [Fact]
    public void EveryClassThatSetsTheAdminToken_RunsInTheAdminServiceTokenCollection()
    {
        // Built from parts so this file does not match its own search.
        var marker = nameof(AdminTrialEndpoint) + "." + nameof(AdminTrialEndpoint.ServiceTokenEnvVar);
        var attribute = "[Collection(" + nameof(AdminServiceTokenCollection) + "." + nameof(AdminServiceTokenCollection.Name) + ")]";

        var setters = Directory
            .EnumerateFiles(ProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("SetEnvironmentVariable(" + marker, StringComparison.Ordinal))
            .ToList();

        // An empty search is a broken search, not a clean one: three classes set the token today.
        Assert.True(setters.Count >= 3, $"expected at least 3 classes that set the admin token, found {setters.Count}");
        var missing = setters.Where(f => !File.ReadAllText(f).Contains(attribute, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "not in the admin service token collection: " + string.Join(", ", missing.Select(Path.GetFileName)));
    }

    private static string ProjectDirectory([CallerFilePath] string thisFile = "")
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/Api/AdminServiceTokenCollection.cs
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
}
