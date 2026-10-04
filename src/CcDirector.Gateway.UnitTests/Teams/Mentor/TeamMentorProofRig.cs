using System.Text.Json;
using CcDirector.Gateway.Teams.Mentor;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>
/// The proof for docs/proof/teams-2305 (devthrottle_internal#2305). NOT a test: it writes one team-week for a test
/// team of two people - Rob, who ran sessions and sent prompts, and the Owner, who ran none - with the FAKE model, and
/// writes every stored row (the block, the outcomes, the run marker, and the stamped prompt records the quote came
/// from) to <c>team-week.json</c>. No paid model, no network, a scratch database deleted afterwards.
///
/// SKIPPED unless <c>CC_TEAMS_2305_PROOF</c> names the directory to write into.
/// </summary>
public sealed class TeamMentorProofRig
{
    private const string ProofEnvVar = "CC_TEAMS_2305_PROOF";

    private sealed class ProofFactAttribute : FactAttribute
    {
        public ProofFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProofEnvVar)))
                Skip = $"Proof rig only. Set {ProofEnvVar} to an output directory to run it.";
        }
    }

    [ProofFact]
    public async Task WriteOneTeamWeek_AndDumpTheStoredRows()
    {
        var outDir = Environment.GetEnvironmentVariable(ProofEnvVar)!;
        Directory.CreateDirectory(outDir);
        using var rig = new MentorRig(ownerAndRobOnly: true);

        rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1, 9), "s-rob-signup");
        var quoted = rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 9), "fix the signup thing so it doesnt break on mobile", "s-rob-signup");
        rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 10), "no, the signup page, not the login page", "s-rob-signup");
        rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 10), "Understood - I will change the signup page.", "s-rob-signup", role: "assistant", modality: null);
        // A message another session put into Rob's session: role "user", but not typed or spoken by him, so not read.
        rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 11), "[from the Tech Lead] please rebase", "s-rob-signup", modality: null);
        rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(3, 14), "add the installer fix for the missing folder", "s-rob-installer");
        rig.Brain.Answer = _ => FakeBrain.HardWeek("P1");

        var run = await rig.Writer().WriteWeekAsync(rig.Team, MentorRig.Week, MentorRig.Zone);

        using var ctx = rig.Db.CreateContext(rig.Team);
        var (from, to) = MentorRig.Week.UtcBounds(MentorRig.Zone);
        var dump = new
        {
            week = MentorRig.Week.ToString(),
            timeZone = MentorRig.Zone.Id,
            team = new { tenantId = rig.Team.Value, members = rig.Teams.MembersOf(rig.Team.Value) },
            modelCalls = rig.Brain.Asked.Count,
            modelWasAsked = rig.Brain.Asked,
            run = new { run.BlocksWritten, run.AlreadyRan },
            sessionHistoryPersonColumn = ctx.SessionHistory.AsNoTracking().Select(s => new { s.SessionId, s.PersonSubject, s.StartedAtUtc }).ToList(),
            promptLogRecords = rig.Prompts.Read(rig.Team, from.Date, to.Date),
            teamMentorBlocks = ctx.TeamMentorBlocks.AsNoTracking().ToList(),
            teamMentorOutcomes = ctx.TeamMentorOutcomes.AsNoTracking().ToList(),
            teamMentorRuns = ctx.TeamMentorRuns.AsNoTracking().ToList(),
            quotedPromptId = quoted.PromptId,
        };
        File.WriteAllText(Path.Combine(outDir, "team-week.json"),
            JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(1, run.BlocksWritten);
        Assert.DoesNotContain("from the Tech Lead", Assert.Single(rig.Brain.Asked));
    }
}
