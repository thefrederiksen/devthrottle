using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The reservation store leaves a pooled worktree slot alone, because cc-worktrees already owns it.
/// </summary>
public sealed class PooledWorktreeSessionTests : IDisposable
{
    private readonly string _root;
    private readonly string _slot;

    public PooledWorktreeSessionTests()
    {
        _root = TestTempRoot.For("ccd-pooled-session-");
        _slot = Path.Combine(_root, "primary.worktrees", "wt01");
        Directory.CreateDirectory(_slot);
    }

    public void Dispose() => TestTempRoot.DeleteTree(_root);

    [Fact]
    public void APoolSlotIsNotReserved_BecauseCcWorktreesAlreadyOwnsIt()
    {
        var store = new WorktreeReservationStore(Path.Combine(_root, "reservations-direct"));

        store.Reserve(_slot, Guid.NewGuid().ToString());
        Assert.Empty(store.LiveReservedPaths());

        // The control: an ordinary worktree IS reserved, so the test above is not passing because
        // nothing is ever reserved.
        var plain = Path.Combine(_root, "devthrottle-somefeature");
        Directory.CreateDirectory(plain);
        store.Reserve(plain, Guid.NewGuid().ToString());
        Assert.Single(store.LiveReservedPaths());
    }
}
