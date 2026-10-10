using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE ONE COLLECTION FOR EVERY TEST THAT SETS A PROCESS-WIDE ENVIRONMENT VARIABLE. It runs alone.
///
/// This assembly runs four test classes at once, and an environment variable is process-wide: a test that sets
/// one changes it under every test running beside it. That is not a hazard, it is a record. The administrator
/// service token: two classes side by side overwrote each other's token and the gate answered 401 where a test
/// expected 200 (Windows continuous integration, run 35137461078). The Director root: CatalogReadRepositoryNameTests
/// pointed it at a folder of its own and deleted the folder afterwards, and TeamMentorEndpointsTests and
/// TurnVerdictFeedbackRouteTests failed with "SQLite Error 14: unable to open database file" at a path inside that
/// folder (8 and 9 October 2026). The provider selection: DatabaseOpensAfterTheBindTests blanks it for one assertion
/// and TeamDirectorTunnelTests failed in its constructor with "The hosted Gateway tried to open a SQLite database"
/// (10 October 2026). That is why the Gateway unit suite was red in almost half its runs.
///
/// The right fix is configuration passed in, not read from the process, and every setter that could be turned into
/// a parameter has been. What is left reads the process by design - the hosted provider selection and the two
/// service tokens - and until those are handed in as constructor configuration, every test that sets one joins
/// THIS collection and runs alone. <see cref="ProcessEnvironmentCollectionTests"/> keeps that complete.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>
/// Keeps <see cref="ProcessEnvironmentCollection"/> complete: EVERY call to <c>Environment.SetEnvironmentVariable</c>
/// compiled into this assembly sits in a test class carrying the collection, or in a module initializer, which runs
/// before any test. It reads the compiled assembly rather than the source, so a call made through a lambda, an async
/// method or a helper in the same class is found where it lands, and a file-level search for a string is not what
/// decides. An earlier guard covered ONE variable this way (the administrator service token) and the other three
/// variables above still took the suite down; this one covers the call itself, whatever the variable.
/// </summary>
public sealed class ProcessEnvironmentCollectionTests
{
    [Fact]
    public void EveryTestThatSetsAnEnvironmentVariable_RunsInTheProcessEnvironmentCollection()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(ProcessEnvironmentCollectionTests).Assembly.Location);

        var setters = new SortedSet<string>(StringComparer.Ordinal);
        var outside = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in assembly.MainModule.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || !CallsSetEnvironmentVariable(method)) continue;
                if (method.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ModuleInitializerAttribute"))
                    continue;   // runs once, before any test, on purpose - see TestStorageRootRedirect and TestEnvironment

                var owner = type;
                while (owner.IsNested) owner = owner.DeclaringType;   // async state machines and lambdas land in nested types
                setters.Add(owner.FullName);
                if (!InTheCollection(owner)) outside.Add($"{owner.FullName} ({method.Name})");
            }
        }

        // A PRESENCE, so an empty read cannot pass: the administrator token alone is set by six classes today.
        Assert.True(setters.Count >= 9, $"expected at least 9 classes that set an environment variable, found {setters.Count}: {string.Join(", ", setters)}");
        Assert.True(outside.Count == 0,
            "These set a process-wide environment variable outside [Collection(ProcessEnvironmentCollection.Name)], so they " +
            "run beside other tests and change the variable under them. Pass the value in instead, or join the collection: " +
            string.Join(", ", outside));
    }

    private static bool CallsSetEnvironmentVariable(MethodDefinition method)
        => method.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference called
            && called.DeclaringType.FullName == "System.Environment"
            && called.Name == "SetEnvironmentVariable");

    private static bool InTheCollection(TypeDefinition type)
        => type.CustomAttributes.Any(a =>
            a.AttributeType.FullName == "Xunit.CollectionAttribute"
            && a.ConstructorArguments.Count == 1
            && a.ConstructorArguments[0].Value is string name
            && name == ProcessEnvironmentCollection.Name);
}
