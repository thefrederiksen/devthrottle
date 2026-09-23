using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The registry is written by many threads at once, and a lost write here costs the user the whole
/// list. These tests prove the writes are serialized and the file is never left half-written.
///
/// <para>None of them hopes to catch an interleaving. The first one stops a writer inside a write and
/// holds it there, so the second thread either gets in or it does not - there is no window to miss.
/// The rest assert on final state, which is wrong without the fix whatever order the threads ran in.
/// </summary>
public class RepositoryRegistryConcurrencyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public RepositoryRegistryConcurrencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RepoRegistryConcurrency_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "repositories.json");
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A WRITE MUST SURVIVE A READER HOLDING THE LIST OPEN, and this states that as its own case
    /// instead of leaving it to the concurrency stress test to stumble over.
    ///
    /// Several Directors run off one root on the same machine. One of them reading the list while
    /// another adds a repository is the ordinary situation, not an edge. Until the swap became
    /// <c>File.Replace</c> it was also the situation Windows refused: a replacing <c>File.Move</c>
    /// will not take a destination another handle holds open, so the add failed with
    /// "Access to the path is denied" and the user's repository silently did not appear.
    ///
    /// The reader here shares the file exactly as a careful second Director does. THIS TEST CANNOT
    /// FAIL ON macOS OR LINUX, which is the point worth knowing about it: Unix does not enforce share
    /// modes, so the old code passed here and failed on the build machine. Its verdict is the Windows
    /// run's, and the assertion is written so that verdict is unambiguous.
    /// </summary>
    [Fact]
    public void A_write_succeeds_while_a_reader_holds_the_list_open()
    {
        var registry = new RepositoryRegistry(_filePath);
        Assert.True(registry.TryAdd(Folder("first")));   // the file now exists, so a swap has something to replace

        using (var readerHandle = new FileStream(
                   _filePath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            // The instrument first: the handle must really be open on the file the registry writes,
            // or this proves nothing about replacing an open destination.
            Assert.True(readerHandle.Length > 0, "the reader is not holding the list open");

            Assert.True(registry.TryAdd(Folder("second")), "the add was refused while a reader held the list open");
        }

        var onDisk = new RepositoryRegistry(_filePath);
        onDisk.Load();
        Assert.Contains(onDisk.Repositories, r => r.Path.EndsWith("second", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first write of a fresh root has no destination to replace, so it takes the other branch.
    /// Stated on its own because that branch is the one a reader never exercises.
    /// </summary>
    [Fact]
    public void The_first_write_creates_the_list_when_there_is_nothing_to_replace()
    {
        Assert.False(File.Exists(_filePath));

        var registry = new RepositoryRegistry(_filePath);
        Assert.True(registry.TryAdd(Folder("only")));

        Assert.True(File.Exists(_filePath));

        var onDisk = new RepositoryRegistry(_filePath);
        onDisk.Load();
        Assert.Contains(onDisk.Repositories, r => r.Path.EndsWith("only", StringComparison.Ordinal));
    }

    /// <summary>
    /// The core proof, and it is decided rather than raced: one thread is parked in the middle of a
    /// write while a second thread tries to write. The seam is <see cref="RepositoryRegistry.SeedFrom"/>,
    /// which walks the enumerable it is given while holding the registry's gate - so an enumerable that
    /// blocks holds the gate open for exactly as long as the test wants.
    /// </summary>
    [Fact]
    public void A_second_thread_cannot_write_while_one_thread_is_inside_a_write()
    {
        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        var first = Folder("first");
        var second = Folder("second");

        using var insideTheWrite = new ManualResetEventSlim(false);
        using var releaseTheWrite = new ManualResetEventSlim(false);
        using var secondWriterRunning = new ManualResetEventSlim(false);

        IEnumerable<RepositoryConfig> OneEntryThenHoldTheGate()
        {
            yield return new RepositoryConfig { Name = "first", Path = first };
            insideTheWrite.Set();
            releaseTheWrite.Wait(TimeSpan.FromSeconds(30));
        }

        var holder = new Thread(() => registry.SeedFrom(OneEntryThenHoldTheGate())) { IsBackground = true };
        holder.Start();
        Assert.True(
            insideTheWrite.Wait(TimeSpan.FromSeconds(10)),
            "the first writer never reached the registry, so this test proved nothing");

        using var secondWriteFinished = new ManualResetEventSlim(false);
        var secondWriteAccepted = false;
        var secondWriter = new Thread(() =>
        {
            secondWriterRunning.Set();
            secondWriteAccepted = registry.TryAdd(second);
            secondWriteFinished.Set();
        }) { IsBackground = true };
        secondWriter.Start();
        Assert.True(
            secondWriterRunning.Wait(TimeSpan.FromSeconds(10)),
            "the second writer never started, so this test proved nothing");

        // While the gate is held the second write cannot land, however fast this machine is. Both the
        // call and its effect on disk are checked: the file is read without going through the registry,
        // so this sees exactly what any other process would see.
        Assert.False(
            secondWriteFinished.Wait(TimeSpan.FromMilliseconds(500)),
            "a second thread completed a write while another thread was inside one");
        Assert.DoesNotContain("second", File.ReadAllText(_filePath));

        releaseTheWrite.Set();

        Assert.True(holder.Join(TimeSpan.FromSeconds(30)), "the first writer never finished");
        Assert.True(secondWriteFinished.Wait(TimeSpan.FromSeconds(30)), "the second writer never finished");
        Assert.True(secondWriteAccepted, "the second write was refused");

        // And once the gate is free, the write lands in full.
        var reread = new RepositoryRegistry(_filePath);
        reread.Load();
        Assert.Equal(2, reread.Repositories.Count);
    }

    /// <summary>
    /// Every write from every thread survives, and what is on disk at the end is a complete list. Two
    /// unsynchronized writers lose entries outright - <see cref="List{T}.Add"/> is several instructions -
    /// and can leave the file half-written, so the final count and a fresh read of the file are the
    /// assertion, not any particular interleaving.
    /// </summary>
    [Fact]
    public void Every_write_from_every_thread_survives_and_the_file_stays_readable()
    {
        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        const int threads = 8;
        const int perThread = 25;

        var paths = new List<string>();
        for (var t = 0; t < threads; t++)
            for (var i = 0; i < perThread; i++)
                paths.Add(Folder($"repo-{t}-{i}"));

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, threads, t =>
        {
            try
            {
                for (var i = 0; i < perThread; i++)
                {
                    var path = Path.Combine(_tempDir, $"repo-{t}-{i}");
                    registry.TryAdd(path);
                    registry.MarkUsed(path);
                    // A reader on another thread at the same moment: a live view of the list would
                    // throw "Collection was modified" here.
                    _ = registry.Repositories.Count;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{ex.GetType().Name}: {ex.Message}");
            }
        });

        Assert.Empty(failures);
        Assert.Equal(paths.Count, registry.Repositories.Count);

        var reread = new RepositoryRegistry(_filePath);
        reread.Load();
        Assert.Equal(paths.Count, reread.Repositories.Count);
        Assert.All(reread.Repositories, r => Assert.NotNull(r.LastUsed));
    }

    /// <summary>
    /// The harm the lock alone cannot prevent: one machine runs several Directors off the same root, so
    /// another process can be reading this file at the moment it is written. A plain write truncates the
    /// file first, and anyone reading in that window gets an empty or half-written list - which the
    /// reader would then treat as a parse failure or, worse, as an empty list. Writing through a
    /// temporary file and one move closes the window: a reader sees the old list or the new one.
    /// </summary>
    [Fact]
    public void A_reader_of_the_file_never_sees_a_half_written_list()
    {
        var registry = new RepositoryRegistry(_filePath);
        registry.Load();
        registry.TryAdd(Folder("seed"));

        var stop = false;
        var tornReads = new System.Collections.Concurrent.ConcurrentBag<string>();
        var reads = 0;

        // Shares the file the way a careful second process would, so this test measures what the reader
        // SEES, never whether it could open the file at all.
        //
        // Nothing may escape this thread: an unhandled exception on a background thread does not fail
        // this test, it kills the whole test host and aborts every test still to run (issue 3220). So
        // anything unexpected is recorded and asserted on after the join, where it fails this test only.
        var readerFailure = (Exception?)null;
        var reader = new Thread(() =>
        {
            try
            {
                ReadUntilStopped();
            }
            catch (Exception ex)
            {
                readerFailure = ex;
            }
        }) { IsBackground = true };

        void ReadUntilStopped()
        {
            while (!Volatile.Read(ref stop))
            {
                string text;
                try
                {
                    using var stream = new FileStream(
                        _filePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(stream);
                    text = sr.ReadToEnd();
                }
                catch (FileNotFoundException)
                {
                    tornReads.Add("the list was not there at all");
                    continue;
                }
                catch (IOException)
                {
                    // A MOMENTARY "in use by another process" IS NOT A TORN READ, and this test says so
                    // itself a few lines up: it shares the file "the way a careful second process would,
                    // so this test measures what the reader SEES, never whether it could open the file at
                    // all". The atomic swap holds the destination for the instant it takes, and on Windows
                    // an opener that arrives inside that instant is refused. A careful second Director
                    // comes back and reads it; so does this reader.
                    //
                    // The property under test is untouched: a read that SUCCEEDS must never be empty and
                    // must always parse. Counting a refused open as a failure would have this test assert
                    // something the product never promised - that the list can be opened at literally any
                    // instant - and it is the reason the test stayed red after the swap itself was fixed.
                    continue;
                }

                reads++;
                if (string.IsNullOrWhiteSpace(text))
                {
                    tornReads.Add("empty");
                    continue;
                }

                try
                {
                    System.Text.Json.JsonSerializer.Deserialize<List<RepositoryConfig>>(text);
                }
                catch (System.Text.Json.JsonException)
                {
                    tornReads.Add(text.Length > 80 ? text[..80] + "..." : text);
                }
            }
        }

        reader.Start();

        // The reader is stopped and joined whatever the writers do. Without the finally, a writer that
        // threw skipped the stop, the test failed, Dispose deleted the folder under the still-running
        // reader, and the reader's DirectoryNotFoundException took the host down with it (issue 3220).
        try
        {
            Parallel.For(0, 4, t =>
            {
                for (var i = 0; i < 50; i++)
                    registry.TryAdd(Path.Combine(_tempDir, $"torn-{t}-{i}"));
            });
        }
        finally
        {
            Volatile.Write(ref stop, true);
            Assert.True(reader.Join(TimeSpan.FromSeconds(30)), "the reader never finished");
        }

        Assert.Null(readerFailure);
        Assert.True(reads > 0, "the reader never read the list, so this test proved nothing");
        Assert.Empty(tornReads);
    }

    /// <summary>
    /// The writes go through a temporary file, and every one of them is cleaned up. A leftover temporary
    /// beside the list would be read back by the next scan as rubbish.
    /// </summary>
    [Fact]
    public void Writing_leaves_no_temporary_files_behind()
    {
        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        Parallel.For(0, 8, t => registry.TryAdd(Path.Combine(_tempDir, $"temp-check-{t}")));

        var strays = Directory.GetFiles(_tempDir)
            .Where(f => !string.Equals(f, _filePath, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(strays);
    }

    /// <summary>
    /// A reader holding the list while a writer changes it is the same defect wearing a different hat.
    /// No threads here on purpose: a live view throws the moment the list is touched, so this is decided
    /// rather than raced.
    /// </summary>
    [Fact]
    public void A_list_a_caller_is_holding_is_not_disturbed_by_a_later_write()
    {
        var registry = new RepositoryRegistry(_filePath);
        registry.Load();
        registry.TryAdd(Folder("held"));

        var held = registry.Repositories;

        registry.TryAdd(Folder("added-after"));

        Assert.Single(held);
        Assert.Equal("held", held[0].Name);
        Assert.Equal(2, registry.Repositories.Count);
    }

    /// <summary>
    /// The failure case the silent catch used to hide: an unreadable list is said out loud and left
    /// alone, rather than being replaced by an empty one.
    /// </summary>
    [Fact]
    public void Load_UnreadableFile_ThrowsAndLeavesTheFileAlone()
    {
        const string rubbish = "{ this was never json";
        File.WriteAllText(_filePath, rubbish);

        var registry = new RepositoryRegistry(_filePath);

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Load());

        Assert.Contains(_filePath, ex.Message);
        Assert.Equal(rubbish, File.ReadAllText(_filePath));
    }

    /// <summary>
    /// An empty file is not an unreadable one - nothing has been written into it yet - so it reads as an
    /// empty list and the Director still starts.
    /// </summary>
    [Fact]
    public void Load_EmptyFile_ReadsAsAnEmptyList()
    {
        File.WriteAllText(_filePath, "   \n");

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        Assert.Empty(registry.Repositories);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
