namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The other spelling of one path: the same path with the case of one letter changed.
///
/// One folder spelled two ways is one folder on Windows and on a default macOS volume, and two
/// folders on every platform whose file system tells them apart, and the tests that prove the saved
/// index follows that rule need the second spelling. On Windows the first letter of a path is the
/// drive letter, so the spelling this returns there is the one an agent produces by lowercasing a
/// drive letter - the exact spelling the review's first finding is about.
/// </summary>
internal static class SpelledPath
{
    /// <summary>
    /// The same path with the case of its first letter flipped. Throws when the path holds no letter
    /// whose case can be flipped, which would be a fact about the machine the test runs on and never
    /// about the path.
    /// </summary>
    /// <param name="path">The path to spell again.</param>
    public static string WithFirstLetterCaseFlipped(string path)
    {
        for (var at = 0; at < path.Length; at++)
        {
            var character = path[at];
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')) continue;

            var flipped = character is >= 'a' and <= 'z'
                ? (char)(character - 'a' + 'A')
                : (char)(character - 'A' + 'a');
            return string.Concat(path[..at], flipped.ToString(), path[(at + 1)..]);
        }

        throw new ArgumentException($"The path {path} holds no letter whose case can be flipped.");
    }
}
