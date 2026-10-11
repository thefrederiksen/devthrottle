using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// THE ROOT-TAKING OVERLOADS on <see cref="CcStorage"/>: each composes, under a root it is given, exactly the path the
/// parameterless member composes under this process's root. They exist so a Gateway host can carry its storage root
/// as a value (GatewayHostOptions.StorageRoot) instead of every store reading CC_DIRECTOR_ROOT at call time; the
/// tests here state the root as an argument and write no environment variable.
/// </summary>
public sealed class CcStorageRootOverloadTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "cc-storage-root-overload-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Each_root_taking_member_composes_its_path_under_the_given_root()
    {
        Assert.Equal(Path.Combine(Root, "keyvault.json"), CcStorage.KeyVaultFile(Root));
        Assert.Equal(Path.Combine(Root, "gateway.db"), CcStorage.GatewayDb(Root));
        Assert.Equal(Path.Combine(Root, "gateway-stats.db"), CcStorage.GatewayStatsDb(Root));
        Assert.Equal(Path.Combine(Root, "config"), CcStorage.Config(Root));
        Assert.Equal(Path.Combine(Root, "config", "gateway"), CcStorage.ToolConfig(Root, "gateway"));
        Assert.Equal(Path.Combine(Root, "config", "director", "instances"), CcStorage.DirectorInstances(Root));
        Assert.Equal(Path.Combine(Root, "turn-log"), CcStorage.TurnLog(Root));
        Assert.Equal(Path.Combine(Root, "prompt-log"), CcStorage.PromptLog(Root));
        Assert.Equal(Path.Combine(Root, "transcription-history"), CcStorage.TranscriptionHistory(Root));
        Assert.Equal(Path.Combine(Root, "transcription-audio"), CcStorage.TranscriptionAudio(Root));
        Assert.Equal(
            Path.Combine(Root, "config", "gateway", "devthrottle-credential.bin"),
            CcStorage.GatewayDevThrottleCredentialBlob(Root));
    }

    /// <summary>
    /// The three members that create their directory when asked keep doing so, under the given root. Checked on a
    /// throwaway root so nothing is created under the machine's own.
    /// </summary>
    [Fact]
    public void The_members_that_create_their_directory_create_it_under_the_given_root()
    {
        var root = Path.Combine(Root, "ensured");
        try
        {
            Assert.Equal(Path.Combine(root, "voice-turn-uploads"), CcStorage.VoiceTurnUploads(root));
            Assert.Equal(Path.Combine(root, "dictation-uploads"), CcStorage.DictationUploads(root));
            Assert.Equal(Path.Combine(root, "typed-prompts"), CcStorage.TypedPrompts(root));
            Assert.True(Directory.Exists(Path.Combine(root, "voice-turn-uploads")));
            Assert.True(Directory.Exists(Path.Combine(root, "dictation-uploads")));
            Assert.True(Directory.Exists(Path.Combine(root, "typed-prompts")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The parameterless member IS the root-taking one applied to the process root, so the two forms cannot name
    /// different files. Only the members that create nothing are compared here: the process root in this test
    /// run is the machine's own, and a test must not create directories under it.
    /// </summary>
    [Fact]
    public void The_parameterless_member_is_the_root_taking_one_applied_to_the_process_root()
    {
        var processRoot = CcStorage.Root();
        Assert.Equal(CcStorage.KeyVaultFile(processRoot), CcStorage.KeyVaultFile());
        Assert.Equal(CcStorage.GatewayDb(processRoot), CcStorage.GatewayDb());
        Assert.Equal(CcStorage.GatewayStatsDb(processRoot), CcStorage.GatewayStatsDb());
        Assert.Equal(CcStorage.Config(processRoot), CcStorage.Config());
        Assert.Equal(CcStorage.ToolConfig(processRoot, "gateway"), CcStorage.ToolConfig("gateway"));
        Assert.Equal(CcStorage.TurnLog(processRoot), CcStorage.TurnLog());
        Assert.Equal(CcStorage.PromptLog(processRoot), CcStorage.PromptLog());
        Assert.Equal(CcStorage.TranscriptionHistory(processRoot), CcStorage.TranscriptionHistory());
        Assert.Equal(CcStorage.TranscriptionAudio(processRoot), CcStorage.TranscriptionAudio());
        Assert.Equal(CcStorage.GatewayDevThrottleCredentialBlob(processRoot), CcStorage.GatewayDevThrottleCredentialBlob());
    }
}
