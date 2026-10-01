using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Instances;

/// <summary>
/// The rule that names a Director's stable identity: which file holds its id, and how that id is read
/// or minted. A Director's id is keyed by the executable it runs from AND the instance it runs as, and
/// lives in that instance's own storage home:
///     &lt;storage root&gt;\config\director\director-id-{slot}.txt
/// where {slot} is the first 8 bytes of SHA256(lowercased "&lt;exe path&gt;|instance=&lt;slug&gt;") in hex.
///
/// WHY IT LIVES HERE AND NOT ONLY IN THE DIRECTOR. The Director's own <c>DirectorIdStore</c> applies it
/// for the process that is running. The setup command line has to apply it for a Director that is NOT
/// running yet: <c>enroll</c> signs a machine in before the Director has ever started, and the device it
/// enrolls must be the device the Director will later present, or the account ends up with two
/// workstations for one machine (issue #3506). Two copies of one rule cannot stay equal, so both read it
/// from here.
/// </summary>
public static class DirectorIdentitySlot
{
    /// <summary>The slot key for a Director running <paramref name="executablePath"/> as instance <paramref name="slug"/>.</summary>
    public static string KeyFor(string executablePath, string slug)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("executablePath is required", nameof(executablePath));
        // EVERY instance - default included - gets its own identity slot (and therefore its own mutex,
        // DirectorId and registration file) by folding its slug into the slot key.
        return $"{executablePath}|instance={InstanceContext.Normalize(slug)}";
    }

    /// <summary>
    /// 8-byte hex slot derived from a slot key. Stable across path case and slash style, so
    /// "D:\Foo\bar.exe" and "d:/foo/bar.EXE" map to the same slot.
    /// </summary>
    public static string SlotFor(string slotKey)
    {
        var normalized = slotKey.Replace('/', '\\').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>The folder holding every id slot file of the Director whose storage home is <paramref name="storageRoot"/>.</summary>
    public static string DirectoryFor(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("storageRoot is required", nameof(storageRoot));
        return Path.Combine(storageRoot, "config", "director");
    }

    /// <summary>The id file for <paramref name="slotKey"/> inside the id folder <paramref name="idDirectory"/>.</summary>
    public static string FilePathFor(string idDirectory, string slotKey)
        => Path.Combine(idDirectory, $"director-id-{SlotFor(slotKey)}.txt");

    /// <summary>
    /// Read the persisted id at <paramref name="idFilePath"/>. When the file is missing, malformed or
    /// empty, mint a fresh GUID, write it once, and return it. Later calls for the same file return the
    /// same id - which is what lets the setup command line mint the id the Director then reuses.
    /// </summary>
    public static string LoadOrCreate(string idFilePath, string slotKey)
    {
        var dir = Path.GetDirectoryName(idFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(idFilePath))
        {
            var raw = File.ReadAllText(idFilePath).Trim();
            if (Guid.TryParse(raw, out var existing))
            {
                FileLog.Write($"[DirectorIdentitySlot] LoadOrCreate: reusing id={existing} slot={slotKey} path={idFilePath}");
                return existing.ToString();
            }
            FileLog.Write($"[DirectorIdentitySlot] LoadOrCreate: file at {idFilePath} malformed, regenerating. raw=\"{raw}\"");
        }

        var fresh = Guid.NewGuid().ToString();
        File.WriteAllText(idFilePath, fresh);
        FileLog.Write($"[DirectorIdentitySlot] LoadOrCreate: minted id={fresh}, slot={slotKey}, path={idFilePath}");
        return fresh;
    }
}
