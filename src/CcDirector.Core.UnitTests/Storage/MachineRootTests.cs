using CcDirector.Core.Instances;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// The one rule this mission exists for: the tools live at the MACHINE root, and a Director's own
/// folder is never an install root.
///
/// Every Director points the CC_DIRECTOR_ROOT setting at its own folder -
/// <c>&lt;machine root&gt;/instances/&lt;name&gt;</c> - and hands that setting to every session it
/// starts. So anything that asked <see cref="CcStorage.Root"/> where the tools were, or where to
/// install them, took a Director's folder for the whole machine whenever it ran from inside a Director
/// or a session. That is how one computer finished with seven complete copies of the tools, each with
/// its own interpreter, each ageing at its own pace, with whichever copy happened to be first on the
/// search path answering for all of them.
///
/// The other half of the rule matters just as much: a root that is NOT a Director's folder - a
/// throwaway root a test rig pins - comes back untouched and keeps its own tools. That separation is
/// what makes a rig safe to run at all, so it is asserted here rather than assumed.
/// </summary>
// ConfigEnvSerial, not CcStorageRoot. This assembly runs its tests IN PARALLEL, so the collection
// name has to be the one every other root-mutating class in THIS assembly uses; "CcStorageRoot"
// has no CollectionDefinition and exists only inside the serialized half, where it is decorative.
// Sharing ConfigEnvSerial (DisableParallelization) is what stops this class racing them for the
// one process-wide CC_DIRECTOR_ROOT.
[Collection("ConfigEnvSerial")]
public sealed class MachineRootTests
{
    /// <summary>
    /// Path segments joined with this platform's separator. The segments are deliberately RELATIVE: the
    /// resolver is pure string work, and a hard-coded Windows path with a drive letter would not split
    /// into parent directories on Linux or macOS, so the same assertion would quietly stop testing
    /// anything on two of the three platforms these suites run on.
    /// </summary>
    private static string P(params string[] parts) => Path.Combine(parts);

    // ---------------------------------------------------------------- the pure resolver

    [Fact]
    public void A_Directors_own_folder_resolves_to_the_machine_root_above_it()
    {
        Assert.Equal(
            P("Users", "someone", "AppData", "Local", "cc-director"),
            CcStorage.MachineRootOf(
                P("Users", "someone", "AppData", "Local", "cc-director", "instances", "default")));
    }

    [Fact]
    public void A_named_Directors_folder_resolves_to_the_same_machine_root()
    {
        var machine = P("root", "cc-director");
        Assert.Equal(machine, CcStorage.MachineRootOf(P(machine, "instances", "default")));
        Assert.Equal(machine, CcStorage.MachineRootOf(P(machine, "instances", "slot-5")));
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_answer()
    {
        var machine = P("root", "cc-director");
        Assert.Equal(
            machine,
            CcStorage.MachineRootOf(P(machine, "instances", "default") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void The_folder_name_instances_is_matched_whatever_its_case()
    {
        // Windows paths are case-insensitive, and the folder is read off disk as often as it is
        // composed. A resolver that only recognised the lower-case spelling would climb out of some
        // Director folders and not others, which is worse than not climbing at all.
        var machine = P("root", "cc-director");
        Assert.Equal(machine, CcStorage.MachineRootOf(P(machine, "Instances", "default")));
    }

    [Fact]
    public void A_nested_Director_folder_is_climbed_all_the_way_out()
    {
        // The leak this fixes did not stop at one level: the computer that prompted the work has an
        // instances/default/instances/default, made when a repair ran from inside a session. One climb
        // out of that lands on another Director's folder, which is still not the machine.
        var machine = P("root", "cc-director");
        Assert.Equal(
            machine,
            CcStorage.MachineRootOf(P(machine, "instances", "default", "instances", "default")));
    }

    [Fact]
    public void A_rig_root_of_its_own_is_returned_exactly_as_it_was_given()
    {
        // The protection that makes an isolated test rig safe. A rig pins CC_DIRECTOR_ROOT at a
        // throwaway folder and keeps its own tools there; nothing about that shape is the fault above,
        // and climbing out of it would send a rig's install onto the real machine.
        var rig = P("Temp", "rig-9f2c");
        Assert.Equal(rig, CcStorage.MachineRootOf(rig));

        var deeperRig = P("work", "throwaway", "cc-director");
        Assert.Equal(deeperRig, CcStorage.MachineRootOf(deeperRig));
    }

    [Fact]
    public void A_folder_called_instances_is_not_itself_a_Directors_folder()
    {
        // <root>/instances is the container, not a Director. Its parent is not named "instances", so
        // the shape test answers false and it comes back unchanged.
        var instances = P("root", "cc-director", "instances");
        Assert.Equal(instances, CcStorage.MachineRootOf(instances));
    }

    [Fact]
    public void A_blank_root_comes_back_untouched_rather_than_throwing()
    {
        Assert.Equal("", CcStorage.MachineRootOf(""));
        Assert.Equal("   ", CcStorage.MachineRootOf("   "));
    }

    // ---------------------------------------------------------------- the shape test

    [Theory]
    [InlineData("default")]
    [InlineData("slot-5")]
    public void The_shape_test_recognises_a_Directors_folder(string slug)
        => Assert.True(CcStorage.IsDirectorInstanceHome(P("root", "instances", slug)));

    [Theory]
    [InlineData("root", "cc-director")]
    [InlineData("Temp", "rig-9f2c")]
    public void The_shape_test_leaves_every_other_root_alone(params string[] parts)
        => Assert.False(CcStorage.IsDirectorInstanceHome(P(parts)));

    [Fact]
    public void The_shape_test_has_ONE_definition_shared_with_the_Directors_identity()
    {
        // Two implementations of one shape cannot stay equal, and these two have to agree: the storage
        // paths climb out of a Director's folder using this shape, and the launcher refuses to serve a
        // root using the same one. InstanceContext delegates here; this asserts it still does.
        var directorFolder = P("root", "cc-director", "instances", "default");
        var machineRoot = P("root", "cc-director");

        Assert.Equal(
            CcStorage.IsDirectorInstanceHome(directorFolder),
            InstanceContext.LooksLikeAnInstanceHome(directorFolder));
        Assert.Equal(
            CcStorage.IsDirectorInstanceHome(machineRoot),
            InstanceContext.LooksLikeAnInstanceHome(machineRoot));
    }

    // ---------------------------------------------------------------- the paths themselves

    [Fact]
    public void The_tool_paths_come_back_from_the_machine_root_when_a_Director_folder_is_pinned()
    {
        using var root = new PinnedRoot(machineRootName: "cc-director", directorSlug: "slot-5");

        Assert.Equal(root.Machine, CcStorage.MachineRoot());
        Assert.Equal(Path.Combine(root.Machine, "bin"), CcStorage.Bin());
        Assert.Equal(Path.Combine(root.Machine, "python"), CcStorage.PythonRuntime());

        // And the data paths still redirect into the Director's own folder, because the tools moving
        // out must not drag the sessions, settings and logs with them.
        Assert.Equal(Path.Combine(root.Director, "config"), CcStorage.Config());
        Assert.Equal(Path.Combine(root.Director, "logs"), CcStorage.Logs());
    }

    [Fact]
    public void A_rig_root_keeps_its_own_tools()
    {
        using var root = new PinnedRoot(rigRootName: "rig-9f2c");

        Assert.Equal(root.Pinned, CcStorage.MachineRoot());
        Assert.Equal(Path.Combine(root.Pinned, "bin"), CcStorage.Bin());
        Assert.Equal(Path.Combine(root.Pinned, "python"), CcStorage.PythonRuntime());
    }
}
