using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The tests that install <c>VoiceUploadStore.BetweenRecordReadAndWriteForTests</c> - a process-wide static
/// seam - run in this collection, which xUnit runs with no other collection alongside it. Two classes that
/// both set that hook and run in parallel can replace or clear each other's callback mid-test, and the
/// interleaving proofs would then be proving nothing about the gate they are aimed at.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VoiceUploadStoreRecordHookCollection
{
    public const string Name = "VoiceUploadStore record hook";
}
