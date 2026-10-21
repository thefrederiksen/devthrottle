using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Teams;

/// <summary>
/// The words a cross-tenant refusal uses (piece D of the mission "a new install lands in the tenant you
/// intended").
///
/// The refusal used to say only that DevThrottle could not confirm the thing was yours. That is true, and it
/// is what a Director enrolled in a team is told for EVERYTHING when the things it addresses belong to the
/// person's own account - which is what a reinstall produced on devthrottle-mac-mini on 20 October 2026. Read
/// on its own it looks like a broken sign-in, and it was investigated as one for several hours. So the
/// refusal now names both causes, one of which the reader can act on.
///
/// What it must NOT do is name the other tenant or anything in it: the caller is told about its OWN situation
/// and where to look, nothing more.
/// </summary>
public sealed class OwnershipRefusalNamesBothCausesTests
{
    [Fact]
    public void OwnershipUnknownRefusal_NamesBothCauses_AndWhereToLook()
    {
        var refusal = TeamEndpointGate.OwnershipUnknownRefusal;

        // It still says what it refused and that nothing happened.
        Assert.Contains("refuses it", refusal);
        Assert.Contains("Nothing was done.", refusal);

        // Cause one: inside the team, somebody else's.
        Assert.Contains("another member of the team", refusal);

        // Cause two: the Director is in the wrong tenant - the one a reader can fix.
        Assert.Contains("enrolled in a different account or team", refusal);

        // And where to look to settle which of the two it is.
        Assert.Contains("Settings, Team", refusal);
    }
}
