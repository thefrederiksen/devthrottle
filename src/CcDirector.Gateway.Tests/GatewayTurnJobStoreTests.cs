using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="GatewayTurnJobStore"/>, focused on the upload -> turn idempotency
/// index that prevents a retried completion from starting a duplicate turn.
/// </summary>
public sealed class GatewayTurnJobStoreTests
{
    [Fact]
    public void Create_WithUploadId_IsFindableByUpload()
    {
        var store = new GatewayTurnJobStore();
        var uploadId = Guid.NewGuid().ToString("N");

        var job = store.Create("sid-1", uploadId);
        var found = store.FindTurnByUpload(uploadId);

        Assert.NotNull(found);
        Assert.Equal(job.TurnId, found!.TurnId);
        Assert.Equal(uploadId, job.UploadId);
    }

    [Fact]
    public void FindTurnByUpload_UnknownOrEmpty_ReturnsNull()
    {
        var store = new GatewayTurnJobStore();
        Assert.Null(store.FindTurnByUpload(Guid.NewGuid().ToString("N")));
        Assert.Null(store.FindTurnByUpload(""));
    }

    [Fact]
    public void FindTurnByUpload_AfterTtlExpiry_ReturnsNull()
    {
        var store = new GatewayTurnJobStore();
        var uploadId = Guid.NewGuid().ToString("N");
        var job = store.Create("sid-1", uploadId);

        // Age the job past its TTL via the documented test seam; the next lookup must drop it.
        job.OverrideCreatedAtForTest(DateTime.UtcNow.AddMinutes(-11), GatewayTurnJobStore.Ttl);

        Assert.Null(store.FindTurnByUpload(uploadId));
    }

    [Fact]
    public void Create_WithoutUploadId_HasEmptyUploadIdAndNoIndex()
    {
        var store = new GatewayTurnJobStore();
        var job = store.Create("sid-1");

        Assert.Equal("", job.UploadId);
        // A retried completion with no upload context never collides with this job.
        Assert.Null(store.FindTurnByUpload(""));
    }
}
