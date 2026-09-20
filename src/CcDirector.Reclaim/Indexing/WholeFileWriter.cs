using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Indexing;

/// <summary>
/// Writes a file so that a reader only ever meets a whole one.
///
/// The text goes to a file of another name beside the real one, and is moved onto the real name only
/// after every byte of it is on the disk. A move inside one folder is a single step to the file
/// system: a reader sees the file that was there before, or the new one, and never part of either.
///
/// This exists because the background scan is routinely interrupted. The Launcher that hosts it is
/// asked to quit by its own update, by a person, and by the machine shutting down, and a whole-disk
/// scan was measured at 1,406 seconds, so a write that is cut short is an ordinary event and not a
/// rare one. A saved scan that was cut short while being written in place would be half a file under
/// the real name, and the old scan it replaced would be gone with it.
/// </summary>
internal static class WholeFileWriter
{
    /// <summary>
    /// The ending of the file the text is written to before it is moved into place. It is not the
    /// ending any saved file uses, so a listing of saved files never picks one up.
    /// </summary>
    public const string PartialFileEnding = ".partial";

    /// <summary>
    /// Write the text to the path, whole or not at all.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="text">What it holds.</param>
    /// <param name="whenHalfWritten">
    /// Called once, after the first half of the bytes are on the disk and before the rest. It is here
    /// for one reason: so a test can stop the write at the moment a real interruption would and then
    /// read what a later reader would find. Nothing that ships passes it.
    /// </param>
    public static void Write(string path, string text, Action? whenHalfWritten = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path to write cannot be blank.", nameof(path));
        ArgumentNullException.ThrowIfNull(text);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(folder))
            throw new InvalidOperationException($"The path {path} names no folder to write into.");

        Directory.CreateDirectory(folder);

        var bytes = new UTF8Encoding(false).GetBytes(text);
        var half = bytes.Length / 2;
        var partialPath = path + PartialFileEnding;

        using (var stream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, half);
            stream.Flush(flushToDisk: true);
            whenHalfWritten?.Invoke();
            stream.Write(bytes, half, bytes.Length - half);
            stream.Flush(flushToDisk: true);
        }

        File.Move(partialPath, path, overwrite: true);
        FileLog.Write($"[WholeFileWriter] Write done: path={path}, bytes={bytes.Length}");
    }
}
