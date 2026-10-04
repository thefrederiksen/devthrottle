using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Tests.Teams.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Prompts;

/// <summary>The Gateway's stamp on a pushed prompt record (devthrottle_internal#2305).</summary>
public sealed class PromptStampTests
{
    private static readonly DateTime At = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ForTeam_StampsTheGivenPerson_AndAFreshIdOnEveryRecord_OverwritingWhatTheClientSent()
    {
        var forged = MentorRig.Record(At, "hello") with { PersonSubject = "sub-someone-else", PromptId = "client-chosen" };
        var plain = MentorRig.Record(At, "again");
        var ids = new Queue<string>(new[] { "g1", "g2" });

        var stamped = PromptStamp.ForTeam(new[] { forged, plain }, "sub-rob", () => ids.Dequeue());

        Assert.All(stamped, r => Assert.Equal("sub-rob", r.PersonSubject));
        Assert.Equal(new[] { "g1", "g2" }, stamped.Select(r => r.PromptId));
        Assert.Equal(new[] { "hello", "again" }, stamped.Select(r => r.Text));
    }

    [Fact]
    public void ForTeam_MintsADistinctIdPerRecord_ByDefault()
    {
        var stamped = PromptStamp.ForTeam(new[] { MentorRig.Record(At, "a"), MentorRig.Record(At, "b") }, "sub-rob");

        Assert.Equal(2, stamped.Select(r => r.PromptId).Distinct().Count());
        Assert.All(stamped, r => Assert.Equal(32, r.PromptId!.Length));
    }

    [Fact]
    public void ForTeam_NoPerson_Throws()
    {
        Assert.Throws<ArgumentException>(() => PromptStamp.ForTeam(new[] { MentorRig.Record(At, "a") }, " "));
    }

    [Fact]
    public void ForPersonal_ClearsBothGatewayFields_AndLeavesAnUnstampedRecordExactlyAsItWas()
    {
        var plain = MentorRig.Record(At, "plain");
        var forged = MentorRig.Record(At, "forged") with { PersonSubject = "sub-x", PromptId = "id-x" };

        var result = PromptStamp.ForPersonal(new[] { plain, forged });

        Assert.Same(plain, result[0]);
        Assert.Null(result[1].PersonSubject);
        Assert.Null(result[1].PromptId);
        Assert.Equal("forged", result[1].Text);
    }

    [Fact]
    public void APersonalRecord_SerializesExactlyAsBefore_WithNeitherNewField()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(PromptStamp.ForPersonal(new[] { MentorRig.Record(At, "x") })[0],
            new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        Assert.DoesNotContain("\"person\"", json);
        Assert.DoesNotContain("\"id\"", json);
    }
}
