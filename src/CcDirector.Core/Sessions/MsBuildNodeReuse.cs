namespace CcDirector.Core.Sessions;

/// <summary>
/// The Director turns MSBuild node reuse OFF for every session it starts.
///
/// With node reuse on - the MSBuild default - a build leaves its worker nodes (MSBuild.exe, and the
/// compiler server they keep warm) running after the build ends, waiting to serve the next build. One
/// developer at one checkout gains from that. A fleet does not: each session builds in its own worktree,
/// the next build rarely lands on the same nodes, and the idle helpers pile up across every session on the
/// machine until they hold gigabytes of memory nobody is using.
///
/// Setting the variable makes each build's helpers exit when that build ends. The cost is a slower start
/// for the next build, which is measured in the report that came with this change.
/// </summary>
public static class MsBuildNodeReuse
{
    /// <summary>The environment variable MSBuild reads; "1" disables node reuse.</summary>
    public const string DisableEnvVar = "MSBUILDDISABLENODEREUSE";

    /// <summary>The value that turns node reuse off.</summary>
    public const string Disabled = "1";
}
