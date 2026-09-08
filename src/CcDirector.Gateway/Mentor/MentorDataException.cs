namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The mentor cannot read what it was asked for: a missing file, a malformed row, an unknown token, a
/// tenant that is not minted. The message names the thing and what to do. This is the port of the
/// reference's <c>common.fail</c> (which prints the message and exits 1): nothing is skipped, nothing is
/// defaulted, and the run stops here.
/// </summary>
public sealed class MentorDataException : Exception
{
    public MentorDataException(string message) : base(message) { }
}
