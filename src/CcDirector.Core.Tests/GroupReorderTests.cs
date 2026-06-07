using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The "group moves as one unit" reorder math (issue #225): a group never splits, members
/// keep their internal order, and a drop never lands between two members of another group.
/// </summary>
public class GroupReorderTests
{
    // Each item is (id, groupId). Distinct ids so IndexOf is unambiguous.
    private static readonly Guid G = Guid.NewGuid();   // a group
    private static readonly Guid H = Guid.NewGuid();   // another group

    private static (string id, Guid? g) S(string id) => (id, null);          // solo
    private static (string id, Guid? g) M(string id, Guid g) => (id, g);     // group member

    private static List<(string id, Guid? g)> Move(List<(string id, Guid? g)> items, int dragged, int rawTarget)
        => GroupReorder.MoveBlock(items, x => x.g, dragged, rawTarget);

    private static string Order(IEnumerable<(string id, Guid? g)> items)
        => string.Join(",", items.Select(x => x.id));

    [Fact]
    public void SoloMove_BehavesLikePlainReorder()
    {
        var items = new List<(string, Guid?)> { S("a"), S("b"), S("c") };
        // drag "a" (index 0) to the end (rawTarget=3)
        Assert.Equal("b,c,a", Order(Move(items, 0, 3)));
    }

    [Fact]
    public void DraggingAnyGroupMember_MovesTheWholeGroup_PreservingInternalOrder()
    {
        // [a] [g1 g2 g3] [z]  -> drag g2 to the very top
        var items = new List<(string, Guid?)> { S("a"), M("g1", G), M("g2", G), M("g3", G), S("z") };
        var result = Move(items, 2, 0); // dragged the MIDDLE member, target = top
        Assert.Equal("g1,g2,g3,a,z", Order(result)); // whole group moved, order kept
    }

    [Fact]
    public void GroupMovedToEnd_StaysContiguous()
    {
        var items = new List<(string, Guid?)> { M("g1", G), M("g2", G), S("a"), S("b") };
        Assert.Equal("a,b,g1,g2", Order(Move(items, 0, 4)));
    }

    [Fact]
    public void SoloDroppedIntoAnotherGroupsInterior_SnapsPastTheGroup_NeverSplitsIt()
    {
        // [a] [g1 g2 g3] [z] - drag "z" so its raw target lands BETWEEN g1 and g2 (index 2).
        var items = new List<(string, Guid?)> { S("a"), M("g1", G), M("g2", G), M("g3", G), S("z") };
        var result = Move(items, 4, 2);
        // z must NOT end up between g1 and g2; it snaps to just after the group.
        Assert.Equal("a,g1,g2,g3,z", Order(result));
        // group stayed contiguous
        var ids = result.Select(x => x.id).ToList();
        Assert.Equal(new[] { "g1", "g2", "g3" }, ids.Where(s => s.StartsWith("g")).ToArray());
        int gi = ids.IndexOf("g1");
        Assert.Equal("g2", ids[gi + 1]);
        Assert.Equal("g3", ids[gi + 2]);
    }

    [Fact]
    public void GroupDroppedBeforeAnotherGroup_BothStayContiguous()
    {
        // [g1 g2] [h1 h2] - drag group G to AFTER group H (target = end)
        var items = new List<(string, Guid?)> { M("g1", G), M("g2", G), M("h1", H), M("h2", H) };
        var result = Move(items, 0, 4);
        Assert.Equal("h1,h2,g1,g2", Order(result));
    }

    [Fact]
    public void DroppingGroupOntoItself_IsNoOp()
    {
        var items = new List<(string, Guid?)> { S("a"), M("g1", G), M("g2", G), S("z") };
        // target within the block (index 2) -> unchanged
        Assert.Equal("a,g1,g2,z", Order(Move(items, 1, 2)));
        // target immediately after the block (index 3) -> also unchanged
        Assert.Equal("a,g1,g2,z", Order(Move(items, 1, 3)));
    }

    [Fact]
    public void OutOfRangeDragged_ReturnsCopyUnchanged()
    {
        var items = new List<(string, Guid?)> { S("a"), S("b") };
        Assert.Equal("a,b", Order(Move(items, 5, 0)));
    }
}
