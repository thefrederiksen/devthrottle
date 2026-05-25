using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class PromptQueueTests
{
    [Fact]
    public void Enqueue_AddsItem()
    {
        var queue = new PromptQueue();

        var item = queue.Enqueue("fix the login bug");

        Assert.Single(queue.Items);
        Assert.Equal("fix the login bug", item.Text);
        Assert.Equal(1, queue.Count);
        Assert.True(queue.HasItems);
    }

    [Fact]
    public void Enqueue_MultipleItems_MaintainsOrder()
    {
        var queue = new PromptQueue();

        queue.Enqueue("first");
        queue.Enqueue("second");
        queue.Enqueue("third");

        Assert.Equal(3, queue.Count);
        Assert.Equal("first", queue.Items[0].Text);
        Assert.Equal("second", queue.Items[1].Text);
        Assert.Equal("third", queue.Items[2].Text);
    }

    [Fact]
    public void Remove_ByGuid_RemovesCorrectItem()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");
        var second = queue.Enqueue("second");
        queue.Enqueue("third");

        var removed = queue.Remove(second.Id);

        Assert.True(removed);
        Assert.Equal(2, queue.Count);
        Assert.Equal("first", queue.Items[0].Text);
        Assert.Equal("third", queue.Items[1].Text);
    }

    [Fact]
    public void Remove_NonExistentGuid_NoOp()
    {
        var queue = new PromptQueue();
        queue.Enqueue("something");

        var removed = queue.Remove(Guid.NewGuid());

        Assert.False(removed);
        Assert.Single(queue.Items);
    }

    [Fact]
    public void UpdateText_ExistingItem_ChangesTextAndFiresEvent()
    {
        var queue = new PromptQueue();
        var item = queue.Enqueue("original");
        var fired = false;
        queue.OnQueueChanged += () => fired = true;

        var updated = queue.UpdateText(item.Id, "edited");

        Assert.True(updated);
        Assert.True(fired);
        Assert.Equal("edited", queue.Items[0].Text);
    }

    [Fact]
    public void UpdateText_UnchangedText_NoOp()
    {
        var queue = new PromptQueue();
        var item = queue.Enqueue("same");

        var updated = queue.UpdateText(item.Id, "same");

        Assert.False(updated);
    }

    [Fact]
    public void UpdateText_NonExistentGuid_NoOp()
    {
        var queue = new PromptQueue();
        queue.Enqueue("something");

        var updated = queue.UpdateText(Guid.NewGuid(), "x");

        Assert.False(updated);
    }

    [Fact]
    public void MoveUp_ReordersCorrectly()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");
        var second = queue.Enqueue("second");
        queue.Enqueue("third");

        var moved = queue.MoveUp(second.Id);

        Assert.True(moved);
        Assert.Equal("second", queue.Items[0].Text);
        Assert.Equal("first", queue.Items[1].Text);
        Assert.Equal("third", queue.Items[2].Text);
    }

    [Fact]
    public void MoveUp_AlreadyFirst_ReturnsFalse()
    {
        var queue = new PromptQueue();
        var first = queue.Enqueue("first");
        queue.Enqueue("second");

        var moved = queue.MoveUp(first.Id);

        Assert.False(moved);
        Assert.Equal("first", queue.Items[0].Text);
    }

    [Fact]
    public void MoveDown_ReordersCorrectly()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");
        var second = queue.Enqueue("second");
        queue.Enqueue("third");

        var moved = queue.MoveDown(second.Id);

        Assert.True(moved);
        Assert.Equal("first", queue.Items[0].Text);
        Assert.Equal("third", queue.Items[1].Text);
        Assert.Equal("second", queue.Items[2].Text);
    }

    [Fact]
    public void MoveDown_AlreadyLast_ReturnsFalse()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");
        var second = queue.Enqueue("second");

        var moved = queue.MoveDown(second.Id);

        Assert.False(moved);
        Assert.Equal("second", queue.Items[1].Text);
    }

    [Fact]
    public void Clear_EmptiesQueue()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");
        queue.Enqueue("second");

        queue.Clear();

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.Count);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void Clear_EmptyQueue_NoOp()
    {
        var queue = new PromptQueue();
        int changeCount = 0;
        queue.OnQueueChanged += () => changeCount++;

        queue.Clear();

        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void LoadFrom_RestoresItems()
    {
        var queue = new PromptQueue();
        var items = new[]
        {
            new PromptQueueItem { Text = "restored one" },
            new PromptQueueItem { Text = "restored two" }
        };

        queue.LoadFrom(items);

        Assert.Equal(2, queue.Count);
        Assert.Equal("restored one", queue.Items[0].Text);
        Assert.Equal("restored two", queue.Items[1].Text);
    }

    [Fact]
    public void LoadFrom_ReplacesExistingItems()
    {
        var queue = new PromptQueue();
        queue.Enqueue("old item");

        queue.LoadFrom(new[] { new PromptQueueItem { Text = "new item" } });

        Assert.Single(queue.Items);
        Assert.Equal("new item", queue.Items[0].Text);
    }

    [Fact]
    public void OnQueueChanged_FiresOnEnqueue()
    {
        var queue = new PromptQueue();
        int changeCount = 0;
        queue.OnQueueChanged += () => changeCount++;

        queue.Enqueue("test");

        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void OnQueueChanged_FiresOnRemove()
    {
        var queue = new PromptQueue();
        var item = queue.Enqueue("test");
        int changeCount = 0;
        queue.OnQueueChanged += () => changeCount++;

        queue.Remove(item.Id);

        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void OnQueueChanged_FiresOnMoveUp()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");
        var second = queue.Enqueue("second");
        int changeCount = 0;
        queue.OnQueueChanged += () => changeCount++;

        queue.MoveUp(second.Id);

        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void OnQueueChanged_FiresOnMoveDown()
    {
        var queue = new PromptQueue();
        var first = queue.Enqueue("first");
        queue.Enqueue("second");
        int changeCount = 0;
        queue.OnQueueChanged += () => changeCount++;

        queue.MoveDown(first.Id);

        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void OnQueueChanged_FiresOnClear()
    {
        var queue = new PromptQueue();
        queue.Enqueue("test");
        int changeCount = 0;
        queue.OnQueueChanged += () => changeCount++;

        queue.Clear();

        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void OnQueueChanged_FiresOnLoadFrom()
    {
        var queue = new PromptQueue();
        int changeCount = 0;
        queue.OnQueueChanged += () => changeCount++;

        queue.LoadFrom(new[] { new PromptQueueItem { Text = "loaded" } });

        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void FindById_ReturnsCorrectItem()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");
        var second = queue.Enqueue("second");

        var found = queue.FindById(second.Id);

        Assert.NotNull(found);
        Assert.Equal("second", found.Text);
    }

    [Fact]
    public void FindById_NotFound_ReturnsNull()
    {
        var queue = new PromptQueue();
        queue.Enqueue("something");

        var found = queue.FindById(Guid.NewGuid());

        Assert.Null(found);
    }

    [Fact]
    public void MoveUp_NonExistentId_ReturnsFalse()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");

        var moved = queue.MoveUp(Guid.NewGuid());

        Assert.False(moved);
    }

    [Fact]
    public void MoveDown_NonExistentId_ReturnsFalse()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");

        var moved = queue.MoveDown(Guid.NewGuid());

        Assert.False(moved);
    }
}
