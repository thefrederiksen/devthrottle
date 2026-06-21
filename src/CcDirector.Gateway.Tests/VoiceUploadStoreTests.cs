using System.Security.Cryptography;
using System.Text;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="VoiceUploadStore"/> - the Gateway-side resumable upload staging
/// behind the guaranteed audio-turn front door. Each test stages under an isolated temp root.
/// </summary>
public sealed class VoiceUploadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-upload-" + Guid.NewGuid().ToString("N"));
    private readonly VoiceUploadStore _store;

    public VoiceUploadStoreTests() => _store = new VoiceUploadStore(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* test cleanup */ }
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void Register_NoId_MintsGuidShapedId()
    {
        var id = _store.Register(null);
        Assert.True(Guid.TryParse(id, out _));
        Assert.True(_store.Exists(id));
    }

    [Fact]
    public void Register_SuppliedGuid_IsReused()
    {
        var key = Guid.NewGuid().ToString();
        var id = _store.Register(key);
        Assert.Equal(Guid.Parse(key).ToString("N"), id);
    }

    [Fact]
    public async Task Assemble_AllChunksPresent_ConcatenatesInOrder()
    {
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 1, Bytes("BBB"), null);
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);

        var result = await _store.AssembleAsync(id, 3);

        Assert.Equal("ok", result.Status);
        Assert.Equal("AAABBBCCC", Encoding.UTF8.GetString(result.Audio!));
    }

    [Fact]
    public async Task Assemble_MissingChunk_ReportsIncompleteWithIndices()
    {
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        // chunk 1 deliberately not sent
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);

        var result = await _store.AssembleAsync(id, 3);

        Assert.Equal("incomplete", result.Status);
        Assert.Equal(new[] { 1 }, result.Missing);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task Assemble_ZeroByteChunk_RefusedAsIncomplete()
    {
        // Issue #592: a TRUNCATED upload (a chunk landed but is empty/zero-byte) must be refused
        // by the completeness gate, never transcribed. The gate treats a zero-byte chunk the same
        // as a missing one (the #586 contract) and names the index to re-send.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);
        // Simulate a truncated landing of chunk 1: the file exists but is empty.
        var chunkPath = Path.Combine(_root, Guid.Parse(id).ToString("N"), "00001.part");
        await File.WriteAllBytesAsync(chunkPath, Array.Empty<byte>());

        var result = await _store.AssembleAsync(id, 3);

        Assert.Equal("incomplete", result.Status);
        Assert.Equal(new[] { 1 }, result.Missing);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task Assemble_ResumeAfterMissing_Succeeds()
    {
        // The whole point: a partial upload is preserved, the client re-sends only what is
        // missing, and the second complete succeeds without re-sending the landed chunks.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);
        Assert.Equal("incomplete", (await _store.AssembleAsync(id, 3)).Status);

        await _store.StoreChunkAsync(id, 1, Bytes("BBB"), null);
        var result = await _store.AssembleAsync(id, 3);

        Assert.Equal("ok", result.Status);
        Assert.Equal("AAABBBCCC", Encoding.UTF8.GetString(result.Audio!));
    }

    [Fact]
    public async Task StoreChunk_IdenticalRetry_IsIdempotentNoOp()
    {
        var id = _store.Register(null);
        var bytes = Bytes("AAA");
        await _store.StoreChunkAsync(id, 0, bytes, Sha(bytes));
        await _store.StoreChunkAsync(id, 0, bytes, Sha(bytes)); // retry, no throw

        var result = await _store.AssembleAsync(id, 1);
        Assert.Equal("AAA", Encoding.UTF8.GetString(result.Audio!));
    }

    [Fact]
    public async Task StoreChunk_ShaMismatch_IsRejected()
    {
        var id = _store.Register(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.StoreChunkAsync(id, 0, Bytes("AAA"), Sha(Bytes("not-the-same"))));
    }

    [Fact]
    public async Task Assemble_UnknownUpload_ReportsUnknown()
    {
        var result = await _store.AssembleAsync(Guid.NewGuid().ToString(), 1);
        Assert.Equal("unknown_upload", result.Status);
    }

    [Fact]
    public async Task Delete_RemovesStaging()
    {
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        _store.Delete(id);
        Assert.False(_store.Exists(id));
    }
}
