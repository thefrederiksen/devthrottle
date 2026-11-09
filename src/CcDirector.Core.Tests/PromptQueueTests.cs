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

    // ONE LOCK, AND Items IS A SNAPSHOT (issue #4815). Before 9 November 2026 the queue was a bare List with
    // no lock and Items was AsReadOnly() over the live list, so a caller holding it saw another thread's
    // writes appear underneath them, and an open enumerator threw InvalidOperationException. The desktop and
    // the eight queue-* tunnel verbs are not the same thread; nothing but the Director's single command
    // delivery loop was keeping them apart, and that is not a lock.

    [Fact]
    public void Items_IsASnapshot_SoAWriteAfterTheReadIsNotInIt()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");

        var taken = queue.Items;
        queue.Enqueue("written after the read");

        Assert.Single(taken);                       // the live view said 2
        Assert.Equal("first", taken[0].Text);
        Assert.Equal(2, queue.Count);               // and the queue itself did take it
    }

    [Fact]
    public void Items_SurvivesAWriteMadeWhileItIsBeingEnumerated()
    {
        var queue = new PromptQueue();
        queue.Enqueue("first");
        queue.Enqueue("second");

        using var walking = queue.Items.GetEnumerator();
        Assert.True(walking.MoveNext());
        Assert.Equal("first", walking.Current.Text);

        queue.Enqueue("arrived mid-enumeration");   // the live view made the next step throw

        Assert.True(walking.MoveNext());
        Assert.Equal("second", walking.Current.Text);
        Assert.False(walking.MoveNext());
    }

    // WHAT THESE TWO CAN AND CANNOT PROVE. A race does not have to happen, so neither of them fails reliably
    // on the unlocked queue - they failed most runs, not every run. What they do is pin the only outcome the
    // locked queue is ever allowed to produce, on any machine and under any interleaving: nothing lost,
    // nothing removed twice. Neither can fail on correct code, which is why they are safe to keep.

    [Fact]
    public async Task Enqueue_FromManyThreadsAtOnce_LosesNothing()
    {
        var queue = new PromptQueue();
        const int threads = 8, each = 250;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < each; i++) queue.Enqueue($"{t}:{i}");
        })));

        Assert.Equal(threads * each, queue.Count);
        Assert.Equal(threads * each, queue.Items.Select(i => i.Text).Distinct().Count());
    }

    [Fact]
    public async Task Remove_FromManyThreadsAtOnce_TakesEachItemExactlyOnce()
    {
        var queue = new PromptQueue();
        var ids = Enumerable.Range(0, 2000).Select(i => queue.Enqueue($"item {i}").Id).ToList();

        var removed = await Task.WhenAll(ids.Chunk(100).Select(chunk => Task.Run(() =>
            chunk.Count(id => queue.Remove(id)))));

        Assert.Equal(ids.Count, removed.Sum());     // every id removed, and none of them twice
        Assert.Equal(0, queue.Count);
        Assert.False(queue.HasItems);
    }

    // TAKING AN ITEM IS THE CLAIM ON IT (found reviewing #4815). queue-send used to find the item, read its
    // text, call Remove and throw the answer away, then send - so the desktop's pop and the Gateway's
    // queue-send could both find the same item and both act on it: typed into the agent twice, or sent once
    // and left in the composer to be sent again by hand. One of the two Remove calls said false and nobody
    // was listening.

    [Fact]
    public void TryTake_GivesTheTextAndRemovesTheItem()
    {
        var queue = new PromptQueue();
        var item = queue.Enqueue("ship the release notes");

        Assert.True(queue.TryTake(item.Id, out var text));

        Assert.Equal("ship the release notes", text);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public void TryTake_AnItemThatIsNotThere_GivesNothing()
    {
        var queue = new PromptQueue();
        queue.Enqueue("still here");

        Assert.False(queue.TryTake(Guid.NewGuid(), out var text));

        Assert.Equal("", text);
        Assert.Single(queue.Items);
    }

    [Fact]
    public void TryTake_TheSameItemTwice_AnswersTrueExactlyOnce()
    {
        var queue = new PromptQueue();
        var item = queue.Enqueue("send me once");

        Assert.True(queue.TryTake(item.Id, out var first));
        Assert.False(queue.TryTake(item.Id, out var second));

        Assert.Equal("send me once", first);
        Assert.Equal("", second);
    }

    [Fact]
    public async Task TryTake_FromManyThreadsAtOnce_GivesEachItemToExactlyOneOfThem()
    {
        // The property that stops a queued prompt being sent twice: whatever the interleaving, one caller
        // and only one is handed each item's text. Cannot fail on correct code.
        var queue = new PromptQueue();
        var ids = Enumerable.Range(0, 500).Select(i => queue.Enqueue($"prompt {i}").Id).ToList();

        var taken = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var mine = new List<string>();
            foreach (var id in ids)
                if (queue.TryTake(id, out var text)) mine.Add(text);
            return mine;
        })));

        var all = taken.SelectMany(t => t).ToList();
        Assert.Equal(ids.Count, all.Count);                 // every item taken
        Assert.Equal(ids.Count, all.Distinct().Count());    // and none of them twice
        Assert.Equal(0, queue.Count);
    }
}
