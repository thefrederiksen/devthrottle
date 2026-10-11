using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// The License Switching mission, trap 4 (contract rule 6.3): Claude Code's usage-limit dialog gets Escape and nothing
/// else from the Director.
///
/// The six dialog screens below are REAL: captured by the Gateway when a session stopped at its limit (the saved turn
/// packages), each whole, with personal details replaced (the time zone, file paths, a branch name, a user name, a
/// balance) and long horizontal rules shortened. They are deliberately different shapes - rows intact, the options
/// run onto one row, spaces dropped, the cancel hint welded onto the options row, the monthly spend limit, a wide
/// terminal - because a check keyed to one drawn shape rejects every other real one. Characters outside plain ASCII
/// are kept as escapes, as the terminal drew them.
/// </summary>
public sealed class UsageLimitDialogTests
{
    private static readonly string[] RowsIntact =
    {
        "on, still enabled \u2014 plugins live in user data, untouched by the install), and no Listen tab",
        "  (TTS now lives in standalone cc-texttospeech).",
        "  Two still-open threads from earlier, unchanged: PR #57 (the Listen removal) is open for your review on GitHub, and cc-texttospeech's source is",
        "  committed locally only (cc-tools remote is archived). The deploy used the branch code directly, so neither blocks your installed app being",
        "  current.",
        "\u273b Cooked for 5m 44s",
        "\u276f C:/work/project/File.cs 2026-06-08 085753.png This screen seems really slow switching between card view and list      view. We need to do some of this asynchronously so we're not tying up the screen, and you need to review that in other places. We want this app    to be responsive everywhere. So don't do anything, tell me you understand first of all and let's lay out a plan for how we make more of this       whole application responsive. It's not that big yet. And then we need to put that into our design doc. And I do think also we need to look at      the director and see where we're using the SenCon method and we have several skills to commit. We need to move some of that in here as well. So    try to review all of this and tell me what you think and give me a plan for doing a few of these things.                                           \u23bf \u00a0You've hit your session limit \u00b7 resets 10:20am (UTC)",
        "     /usage-credits to finish what you\u2019re working on.",
        "\u273b Baked for 0s                                                                                                                                                                                                                                                                                        \u276f /rate-limit-options",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500  What do you want to do?",
        "  \u276f 1. Stop and wait for limit to reset",
        "    2. Add funds to continue with usage credits",
        "    3. Switch to Team plan",
        "  Enter to confirm \u00b7 Esc to cancel",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500 feature-branch \u2500\u2500",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500 feature-branch \u2500\u2500\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 \u2190 for agents\u25cf high \u00b7 /effort",
        "PR #57 \u00b7 \u2190 for agentscontinue\u276f continue",
        "\u2722 Hyperspacing\u2026",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500 feature-branch \u2500\u2500",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5bypasspermissionson (shift+tabtocycle)\u00b7PR#57 \u00b7esctointerrupt\u25cfhigh\u00b7/effort\u00b7cig\u2026ng\u2026\u2722*\u2736\u273b  \u23bf \u00a0You've hit your session limit \u00b7 resets 10:20am (UTC)",
        "    /usage-credits to finish what you\u2019re working on.\u273b Cooked for 0s",
        " feature-branch",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5bypasspermissionson (shift+tabtocycle)\u00b7PR#57 \u00b7\u2190foragents",
    };

    private static readonly string[] OptionsOnOneRow =
    {
        "\u273b2Galloping\u202614\u273dGalloping\u20265Galloping\u20267\u25cfGalloping\u20268\u273bGalloping\u20265002\u2736Galloping\u20263* \u2722Galloping\u2026\u25cfWaitGalloping\u20262\u2191\u00b7\u25cf{\"sessionId\":\"663e3482-f620-47ff-9487-06e7a087baae\",",
        "      \"directorId\": \"37cea6e2-da6d-418c-a5d5-7eeb417da297\",",
        "     \u2026 +40 lines (ctrl+o to expand)\u00b7 Galloping\u2026 (running stop hooks\u2026 0/2 \u00b7 12s \u00b7 \u2191 503 tokens)",
        "  \u23bf \u00a0Tip: Use /permissions to pre-approve and pre-deny bash, edit, and MCP tools",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt",
        "You've used 91% of your session limit \u00b7 resets 9am (UTC) \u00b7 /upgrade to keep using Claude Code",
        "\u2722*\u2736Gal\u273bGlalop3li\u273donpgin\u2026g\u2026\u273b\u2736*\u27224\u00b7GaGllao\u2722lploinpg*i\u2026n\u2736g\u2026\u273b5\u273d\u273b\u2736G*al\u2722Gl\u2193 \u00b7 thinking)\u00b7Gl6thinkingthinkinglthinking21thinking\u2722a3thinking8thinking*G9\u273641\u273b2thinking\u273d74thinking6thinking8thinking9thinking",
        "\u273b51thinking\u27363thinking*\u27228thinking\u00b7thinkingthinkingthinkingthinking\u2722thinking*thinking\u2736\u273b9thinking\u273dthinkingthinkingthinkingthinking\u273bthinking\u2736thinking*\u272220thinking\u00b7thinkingthinking\u2026thinkingthinking\u2722gthinking*nthinking\u2736i\u2026\u273b1thinking\u273dpgthinkingthinkingonthinkingthinking\u273blithinking\u2736lpthinking*\u2722ao2thinkingthought for 6s)\u00b7Gl78616l4166\u2722a91*G716\u273653\u273b783803\u273d2866859\u273b907\u273618*22\u2722349)\u00b73235\u2722648*9\u273651\u273b2567\u273d7356\u273b901.k tokens)\u273bBaked for 25s",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 \u2190 for agents",
        "Checking for updates\u2736 Honking\u2026 (running stop hooks\u2026 0/2)                                                                                      \u23bf \u00a0Tip: Use /permissions to pre-approve and pre-deny bash, edit, and MCP tools",
        "                                                                                                                        \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5bypasspermissionson (shift+tabtocycle)\u00b7esctointerrupt",
        "*\u273b Claude resumig /loop wakeup (Jun 11 7:23am)",
        "\u2722Honking\u2026 (running stop hooks\u2026 0/2)",
        "  \u23bf \u00a0Tip: Use /permissions to pre-approve and pre-deny bash, edit, and MCP tools",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt",
        "\u00b7HoHnkoi  \u23bf \u00a0You've hit your session limit \u00b7 resets 9am (UTC)",
        "\u273b Baked for 0s\u276f\u00a0\u2190 for agents",
        "\u276f /rate-limit-options",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500  What do you want to do?",
        "  \u276f 1. Stop and wait for limit to reset  2. Add funds to continue with usage credits3.SwitchtoTeamplan",
        "  Enter to confirm \u00b7 Esc to cancel",
        "  \u23bf \u00a0You've hit your session limit \u00b7 resets 9am (UTC)",
        "     /usage-credits to finish what you\u2019re working on.                                                                   \u273b Cooked for 0s",
        "\u276f /rate-limit-options",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500What do you want to do?\u276f 1. Stop and wait for limit to reset2.Addfundstocontinuewithusagecredits",
        "    3. Switch to Team plan",
        "  Enter to confirm \u00b7 Esc to cancel",
    };

    private static readonly string[] SpacesDropped =
    {
        "nting\u2026  *\u27361\u273b\u273d\u25cf\u273b\u2736*2 \u2722Gallivanting\u2026 \u00b7\u2722\u25cf*\u27363\u273b \u273dGallivanting\u2026 \u25cf\u27364*\u2722\u00b7 \u2722*\u27365\u273bGallivanting\u2026 \u25cf\u273d \u273b*6\u2722\u25cf\u00b7Gallivanting\u2026 \u2722* \u27367\u273b\u273d\u25cf\u273dGallivanting\u2026 \u273b\u2736*8\u2722 \u00b7\u2193 \u00b7 thinking)\u00b7Gallivanting\u2026 thinking7thinking\u2722thinking\u25cfthinking*\u27369\u273bthinking\u273d\u2026thinking thinkingthinkinggthinkingthinking\u273bnthinking\u2736thinkingi\u2026*\u25cf20s \u00b7 \u2193 5.8k tokens \u00b7 thinking)\u2722tgthinking\u00b7nnthinkingthinkingaithinking9thinking thinking\u2722vtthinking*inthinking\u2736160\u273blathinking\u25cf\u273dlvthinkingthinkingthinkingaithinking1thinking\u273bGlthinking thinking\u2736l*2a2\u2722thinking\u25cf\u00b7Gthinking\u2722thinking*thinking\u27363 3\u273bthinking\u273dthinking\u25cf\u273bthinkingthinking\u27364thought for 5s)*4\u2722 5\u00b7\u27226\u25cf Read 1 file (ctrl+o to expand)",
        "\u25cfNowIhavethefullstructure:apool,3lanes,allnodeDIbounds,andexplicit waypoints on every edge.LetmereadtherestofAppendFlowto",
        " copy the exact valid connector XML pattern, then writethespike.\u25cf Reading 1 file\u2026 (ctrl+o to expand)",
        "\u2722 Gallivanting\u2026 (2m 4s \u00b7 \u2193 6.6k tokens \u00b7 thought for 5s)",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt",
        "*\u27365\u273b7\u273d 8\u273b\u2736)\u2026*69\u25cf\u2722g\u2722Gallivanting\u2026 (2m 6s \u00b7 \u2193 6.9k tokens)\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u276f\u00a0  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt",
        "\u25cfReading 1 file\u2026 (ctrl+o to expand)",
        "  \u23bf  C:/work/project/File.cs",
        "                                                                                                                                                   \u2722 Gallivanting\u2026 (2m 6s \u00b7 \u21936.9k tokens)",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5bypasspermissionson (shift+tabtocycle)\u00b7esctointerrupt",
        "Gallivanting\u2026\u00b7Gallivanting\u202670You'veused100%ofyoursessionlimit\u00b7resets10:20am(UTC)\u00b7/upgradetokeepusingClaudeCode",
        "Gallivanting\u2026\u2191ivntainntgin\u2026 \u2722g1\u2026 Read 1 file (ctrl+o to expand)\u00a0You've hit your session limit \u00b7 resets 10:20am (UTC)\u273bBaked for 2m 6s\u276f\u00a0\u2190 for agents Reading 1 file\u2026 (ctrl+o to expand) C:/work/project/File.cs /rate-limit-options                                                                                                                              \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500  What do you want to do?",
        "\u276f 1. Stop and wait for limit to reset2. Addfundstocontinuewithusage credits3.SwitchtoTeamplan",
        "  Enter to confirm \u00b7 Esc to cancel",
        " Read 1 file (ctrl+o to expand)\u00a0You've hit your session limit \u00b7 resets 10:20am (UTC)/usage-credits to finish what you\u2019re working on.",
        "                                                                                                                                                   \u273bBrewed for 2m 6s",
        "                                                                                                                                                   \u276f /rate-limit-options                                                                                                                              \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500What do you want to do?\u276f1. Stop and wait for limit to reset  2. Add funds to continue with usagecredits3.SwitchtoTeamplan",
        "  Enter to confirm \u00b7 Esc to cancel",
    };

    private static readonly string[] HintOnTheOptionsRow =
    {
        "\u2026\u25cf\u00b7Running live proof\u2026Running live proof\u2026Running live proof\u2026Running live proof\u20261\u2722Running live proof\u2026 *Running live proof\u20269\u2736\u273bRunning live proof\u2026\u273dRunning live proof\u2026Running live proof\u2026\u25cfRunning live proof\u2026Running live proof\u20262\u273bRunning live proof\u2026\u2736Running live proof\u202630* \u2722Running live proof\u2026PowerShell($proof = 'C:/work/project/File.cs          Select-String -Path \"C:Users\\user\\AppData\\Localc-directr\\log\\director\\director-20\u2026)Running\u2026PowerShell(Get-Process-Namecc-director6-ErrorActionSilentlyContinue|Select-ObjectId,Path;Get-ScheduledTask-TaskName          'cc-dietor6lunch' -ErorAction SilenlyCo\u2026)Running\u2026PowerShell((Invoke-RestMethodhttp://localhost:7878/directors)|Where-Object{$_.pid-in56400,39020,58048,62220}|ConvertTo-Json          -Compress)Running\u2026\u2026 +180 tool uses (ctrl+o to expand)",
        "    (ctrl+b to run in background)",
        "\u00b7Running live proof\u2026 (48m 12s \u00b7 \u219383.0k tokens)",
        "  \u23bf \u00a0Next: PR + proof commit + label swap to flow:ready-qa",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt \u00b7 ctrl+t to show tasks \u00b7 \u2193 to manage",
        " \u25cfmain\u2191/\u2193toselect\u00b7Entertoview",
        " \u25efgeneral-purposeDEVphaseissue#32446m30sRunning live proof\u2026Running live proof\u20263\u25cfRunning live proof\u2026\u2722Running live proof\u2026*Running live proof\u20261\u2736\u273bRunning live proof\u2026 \u273dRunning live proof\u2026Running live proof\u2026Running live proof\u20264Running live proof\u2026\u273bRunning live proof\u2026\u25cf\u2736Running live proof\u20262*\u2722Running live proof\u2026\u00b7Running live proof\u2026Running live proof\u2026Running live proof\u20265Running live proof\u20263\u2722Running live proof\u2026*Running live proof\u20264\u2736 \u273bRunning live proof\u2026\u273dRunning live proof\u2026\u25cf",
        "\u273dRunning live proof\u2026 (48m 15s \u00b7 \u219183.0k tokens)",
        "  \u23bf \u00a0Next: PR + proof commit + label swap to flow:ready-qa",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt \u00b7 ctrl+t to show tasks",
        "\u25cf",
        "  \u23bf \u00a0Done(183tooluses\u00b70tokens\u00b746m34s)",
        "  (ctrl+o to expand)",
        "\u273dRunning live proof\u2026 (48m 15s \u00b7 \u219183.0k tokens)",
        "  \u23bf \u00a0Next: PR + proof commit + label swap to flow:ready-qa",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5bypasspermissionson (shift+tabtocycle)\u00b7esctointerrupt\u00b7ctrl+ttoshowtasks6\u273b",
        "\u276f /rate-limit-options",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500  What do you want to do?",
        "  \u276f 1. Stop and wait for limit to reset  2. Add funds to continue with usage credits3.SwitchtoTeamplanEntertoconfirm\u00b7Esctocancel  \u23bf \u00a0You've hit your session limit \u00b7 resets 9am (UTC)",
        "     /usage-credits to finish what you\u2019re working on.                                                                                              \u273b Crunched for 48m 16s",
        "\u276f /rate-limit-options",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500What do you want to do?\u276f 1. Stop and wait for limit to reset2.Addfundstocontinuewithusagecredits3.SwitchtoTeamplanEntertoconfirm\u00b7Esctocancel",
    };

    private static readonly string[] MonthlySpendLimit =
    {
        ")\u2722*\u273b203\u273d\u273b\u2736*\u27224\u00b7\u27221*\u2736\u273b\u273d5\u273b\u2736*2\u2722\u00b76\u2722*\u2736\u273b\u273d73\u273b\u2736*\u2722\u00b78\u2722*\u27364\u273b\u273d9\u273b\u2736*\u2722\u00b720s \u00b7 \u2193 2.4k tokens \u00b7 thinking some more)\u2722*\u2736\u273b\u273d15\u273b\u2736*\u2722\u00b72\u2722*\u27366\u273b\u273d3\u273b\u2736*\u2722\u00b747\u2722*\u2736\u273b\u273d5\u273b\u2736*8\u27226\u00b7\u27229*\u2736\u273b7\u273dalmost done thinking)\u273b\u2736*30\u27228\u00b7\u2722*\u2736\u273b\u273d91\u273b\u2736*\u2722\u00b710s \u00b7 \u2193 3.1k tokens \u00b7 almost done thinking)\u27222*\u2736\u273b\u273d1\u273b\u2736*\u2722\u00b723\u2722*\u2736\u273b\u273d3\u273b4\u2736*\u27224\u00b7\u2722*\u27365\u273b\u273d5\u273b\u2736*\u2722\u00b76\u2722*\u27366\u273b\u273d7\u273b\u2736*\u2722\u00b787\u2722*\u2736\u273b\u273bPrestidigitating\u2026 thinking)ought for 57s)\u273de9r Searching for 1 pattern\u2026 (ctrl+o to expand)\u273d Prestidigitating\u2026 (2m 19s \u00b7 \u2193 3.7k tokens \u00b7 thought for 57s)",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt                                                        Now using usage credits",
        "\u25cfBuilding v10.Firs I eed the exact v9 sourcefortheareasI'mchanging\u2014header,launcher,members,overlays,andtheactionswitch: Searching for 1 pattern\u2026 (ctrl+o to expand)\u273d Prestidigitating\u2026 (2m 19s \u00b7 \u2193 3.7k tokens \u00b7 thought for 57s)",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt                                                        Now using usage credits",
        "\u273bP\u25cf\u2736*\u272220\u00b7 \u27228*\u2736\u25cf\u273b)1\u273d\u273b   \u23bf  \"function header|h-search|function closeMenus|else if \\(e.key===\"Escape\"|function overlays|newModal\\?|createOpen\\?\"",
        "\u273bPrestidigitating\u2026 (2m 21s \u00b7 \u21933.8k tokens)",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5bypasspermissionson (shift+tabtocycle)\u00b7esctointerruptNowusingusagecredits",
        "\u2736Pre\u2191",
        "Nowusingusagecredits",
        "Fable 5 is now consuming usage credits instead of your plan limits. Update Claude Code to the latest version to learn more",
        "Prst*eisd\u2722ti9idgiit2\u00b7ga\u25cfittiantg40i\u2026ng\u2722\u2026 Searched for 1 pattern (ctrl+o to expand)\u00a0You've hit your monthly spend limit.\u273bBaked for 2m 22s\u276f\u00a0\u2190 for agentsFable 5 is now consuming usage credits instead of your plan limits. Update Claude Code to the latest version to learn more",
        "\u25cfSearching for 1 pattern\u2026 (ctrl+o to expand) \"function header|h-search|function closeMenus|else if \\(e.key===\"Escape\"|function overlays|newModal\\?|createOpen\\?\"\u276f /rate-limit-options                                                                                                                              \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500  What do you want to do?",
        "\u276f 1. Stop and wait for limit to reset2. Addfundstocontinue with usage credits3.SwitchtoTeamplan",
        "  Enter to confirm \u00b7 Esc to cancel",
        " Searched for 1 pattern (ctrl+o to expand)\u00a0You've hit your monthly spend limit./usage-credits to adjust your monthly spend limit.",
        "                                                                                                                                                   \u273bBaked for 2m 22s",
        "                                                                                                                                                   \u276f /rate-limit-options                                                                                                                              \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500What do you want to do?\u276f1. Stop and wait for limit to reset  2. Add funds to continue with usagecredits3.SwitchtoTeamplan",
        "  Enter to confirm \u00b7 Esc to cancel",
        "Usage credit balance: $0.00 Adjust mothly spend: \u2026      \u2190 or \u2192 to adjust \u00b7 Del to remove limitWait for limi to reset                 Resets 1pm (UTC)Enter to confirm \u00b7 Esc to cancel",
        "Unlimited\u2190or \u2192 to set a",
    };

    private static readonly string[] WideTerminal =
    {
        "it. Why not make this a SaaS offering? Is that, I guess, what he's against? Where is this? Can you bring up",
        "  his webpage again so I can read what it is he's talking about, why this product needs to exist? I need to review this again. I still don't",
        "  quite understand what this is about or why it's needed.",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "  \u23f5\u23f5 bypass permissions on (shift+tab to cycle)",
        "\u276f So it's just a project management tool that he's created. But this would, I guess, only be for solo practitioner because there's not a database.   And you have to install it. Why not make this a SaaS offering? Is that, I guess, what he's against? Where is this? Can you bring up his webpage    again so I can read what it is he's talking about, why this product needs to exist? I need to review this again. I still don't quite understand    what this is about or why it's needed.",
        "\u273d Whatchamacalliting\u2026",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
        "  \u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 esc to interrupt",
        "ha\u273bamacal\u2736caliti\u2722iting\u2026ng  \u23bf \u00a0You've hit your session limit \u00b7 resets 10:20am (UTC)",
        "\u273bCrunched for 0s",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5bypasspermissionson (shift+tabtocycle)\u00b7\u2190foragents",
        "\u276f /rate-limit-options",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500  What do you want to do?",
        "  \u276f 1. Stop and wait for limit to reset  2. Add funds to continue with usage credits3.SwitchtoTeamplan",
        "  Enter to confirm \u00b7 Esc to cancel",
        "  \u23bf \u00a0You've hit your session limit \u00b7 resets 10:20am (UTC)",
        "     /usage-credits to finish what you\u2019re working on.                                                                                              \u273b Crunched for 0s                                                                                                                                                                                                                                                                                   \u276f /rate-limit-options",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500What do you want to do?\u276f 1. Stop and wait for limit to reset2.Addfundstocontinuewithusagecredits",
        "    3. Switch to Team plan",
        "  Enter to confirm \u00b7 Esc to cancel",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u276f",
        "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u23f5\u23f5 bypass permissions on (shift+tab to cycle) \u00b7 \u2190 for agents\u25cf high \u00b7 /effort",
        "Checking for updatesChecking for updatesnew task? /clear to save 295.5k tokens",
    };

    public static TheoryData<string> RealDialogs() => new()
    {
        nameof(RowsIntact), nameof(OptionsOnOneRow), nameof(SpacesDropped), nameof(HintOnTheOptionsRow),
        nameof(MonthlySpendLimit), nameof(WideTerminal),
    };

    private static string[] Screen(string name) => name switch
    {
        nameof(RowsIntact) => RowsIntact,
        nameof(OptionsOnOneRow) => OptionsOnOneRow,
        nameof(SpacesDropped) => SpacesDropped,
        nameof(HintOnTheOptionsRow) => HintOnTheOptionsRow,
        nameof(MonthlySpendLimit) => MonthlySpendLimit,
        nameof(WideTerminal) => WideTerminal,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(RealDialogs))]
    public void IsShowing_RealDialogScreen_IsTrue(string screen)
    {
        Assert.True(UsageLimitDialog.IsShowing(Screen(screen)));
    }

    [Fact]
    public void IsShowing_AfterEscapeOnlyTheLimitNoticeRemains_IsFalse()
    {
        var rows = new[]
        {
            "  \u23bf \u00a0You've hit your session limit \u00b7 resets 9am (UTC)",
            "     /usage-credits to finish what you\u2019re working on.",
            "\u273b Cooked for 0s",
            "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            "\u276f ",
            "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            "  \u23f5\u23f5 bypass permissions on (shift+tab to cycle)",
        };

        Assert.False(UsageLimitDialog.IsShowing(rows));
    }

    [Fact]
    public void IsShowing_PermissionPromptWithTheSameCancelHint_IsFalse()
    {
        var rows = new[]
        {
            " Bash command",
            "   git status",
            " Do you want to proceed?",
            " \u276f 1. Yes",
            "   2. Yes, and don't ask again for git status commands",
            "   3. No, and tell Claude what to do differently (esc)",
            " Esc to cancel",
        };

        Assert.False(UsageLimitDialog.IsShowing(rows));
    }

    [Fact]
    public void IsShowing_DialogQuotedHighUpThenWorkGoesOn_IsFalse()
    {
        // An agent quoting the dialog in its own output, after which the session keeps working: the words are all
        // there, but not as a menu open at the bottom of the screen.
        var dialogStart = Array.FindIndex(RowsIntact, r => r.Contains("1. Stop and wait", StringComparison.Ordinal));
        var dialog = RowsIntact.Skip(dialogStart).Take(4).ToList();
        Assert.True(UsageLimitDialog.IsShowing(dialog));   // on its own, at the bottom, it IS the dialog

        var rows = new List<string>(dialog);
        for (var i = 0; i < 12; i++) rows.Add($"\u25cf Read src/File{i}.cs (ctrl+o to expand)");
        rows.Add("\u273b Working\u2026 (esc to interrupt)");

        Assert.False(UsageLimitDialog.IsShowing(rows));
    }

    [Fact]
    public void IsShowing_TheCheckOwnSourceOnScreen_IsFalse()
    {
        // A session with the detector's own source open: the squashed words are on the screen, in the wrong shape.
        var rows = new[]
        {
            "    private const string CancelHint = \"esctocancel\";",
            "    private const string FirstOption = \"1stopandwaitforlimittoreset\";",
            "",
            "    /// <summary>True when the visible screen shows the usage-limit dialog open and waiting.</summary>",
        };

        Assert.False(UsageLimitDialog.IsShowing(rows));
    }

    [Fact]
    public async Task Write_DialogShowing_RefusesEverythingButEscape()
    {
        var inner = new RecordingBackend();
        var guard = new UsageLimitInputGuard(inner, Guid.NewGuid(), () => true);

        guard.Write(new byte[] { 0x0D });
        guard.Write(System.Text.Encoding.ASCII.GetBytes("2"));
        guard.Write(new byte[] { 0x1B, 0x5B, 0x42 });   // arrow down starts with Escape but is not Escape
        await guard.SendTextAsync("continue");
        await guard.SendEnterAsync();
        guard.Write(new byte[] { 0x1B });

        Assert.Equal(new[] { new byte[] { 0x1B } }, inner.Writes);
        Assert.Empty(inner.SentTexts);
        Assert.Equal(0, inner.Enters);
    }

    [Fact]
    public void Write_DialogShowingAndAPersonTypesAtTheTerminal_Passes()
    {
        var inner = new RecordingBackend();
        var guard = new UsageLimitInputGuard(inner, Guid.NewGuid(), () => true);

        using (UsageLimitInputGuard.PersonAtTheTerminal())
            guard.Write(new byte[] { 0x0D });
        guard.Write(new byte[] { 0x0D });

        Assert.Single(inner.Writes);
    }

    [Fact]
    public async Task Write_NoDialog_PassesEverything()
    {
        var inner = new RecordingBackend();
        var guard = new UsageLimitInputGuard(inner, Guid.NewGuid(), () => false);

        guard.Write(new byte[] { 0x0D });
        await guard.SendTextAsync("continue");

        Assert.Single(inner.Writes);
        Assert.Single(inner.SentTexts);
    }

    [Theory]
    [MemberData(nameof(RealDialogs))]
    public void Session_RealDialogOnScreen_AutomaticInputRefusedAndEscapeSentOnce(string screen)
    {
        var backend = new RecordingBackend(withScreen: true);
        var session = NewTerminalSession(backend);
        backend.Show(Screen(screen));

        session.SendInput(new byte[] { 0x0D });   // no origin: automatic
        session.SendInput(System.Text.Encoding.ASCII.GetBytes("2"));
        var first = session.EscapeUsageLimitDialogIfShowing();
        var second = session.EscapeUsageLimitDialogIfShowing();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(new[] { new byte[] { 0x1B } }, backend.Writes);
    }

    [Fact]
    public void Session_PersonTypesAtTheTerminalWhileTheDialogShows_ReachesIt()
    {
        var backend = new RecordingBackend(withScreen: true);
        var session = NewTerminalSession(backend);
        backend.Show(RowsIntact);

        session.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped);

        Assert.Equal(new[] { new byte[] { 0x0D } }, backend.Writes);
    }

    [Fact]
    public void Session_DialogGoneAndBack_EscapesItAgain()
    {
        var backend = new RecordingBackend(withScreen: true);
        var session = NewTerminalSession(backend);

        backend.Show(RowsIntact);
        Assert.True(session.EscapeUsageLimitDialogIfShowing());
        backend.Show(new[] { "\u276f ", "  \u23f5\u23f5 bypass permissions on" });
        Assert.False(session.EscapeUsageLimitDialogIfShowing());
        backend.Show(WideTerminal);
        Assert.True(session.EscapeUsageLimitDialogIfShowing());
    }

    [Fact]
    public void Session_NoDialog_AutomaticInputPasses()
    {
        var backend = new RecordingBackend(withScreen: true);
        var session = NewTerminalSession(backend);
        backend.Show(new[] { "\u276f ", "  \u23f5\u23f5 bypass permissions on" });

        session.SendInput(new byte[] { 0x0D });

        Assert.False(session.EscapeUsageLimitDialogIfShowing());
        Assert.Equal(new[] { new byte[] { 0x0D } }, backend.Writes);
    }

    [Fact]
    public async Task TerminalStateDetector_DialogSettlesOnScreen_SendsEscapeOnce()
    {
        // The production path: nothing calls the Escape by hand. The dialog is drawn, the screen goes quiet, the
        // detector settles the session - and that settle sends the one Escape.
        using var manager = new SessionManager(new CcDirector.Core.Configuration.AgentOptions { DefaultBufferSizeBytes = 1 << 20 });
        var backend = new RecordingBackend(withScreen: true);
        var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        using var detector = new CcDirector.Core.Wingman.TerminalStateDetector(manager, driveState: true, TimeSpan.FromMilliseconds(100));
        detector.Start();

        // The limit is hit during a turn: the session is working when the dialog is drawn.
        session.IsBrandNew = false;
        session.ApplyTerminalActivityState(ActivityState.Working);
        backend.Show(WideTerminal);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (backend.Writes.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        await Task.Delay(400);   // several more quiet periods: still one Escape, never a second

        Assert.Equal(new[] { new byte[] { 0x1B } }, backend.Writes);
        Assert.NotNull(session);
    }

    private static Session NewTerminalSession(RecordingBackend backend)
    {
        var session = new Session(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, backend, SessionBackendType.ConPty);
        session.MarkRunning();
        return session;
    }

    /// <summary>A backend that records what it was given, starts no process, and can put a screen in its buffer.</summary>
    private sealed class RecordingBackend : ISessionBackend
    {
        public RecordingBackend(bool withScreen = false)
        {
            if (withScreen) Buffer = new CircularTerminalBuffer(1 << 20);
        }

        public List<byte[]> Writes { get; } = new();
        public List<string> SentTexts { get; } = new();
        public int Enters { get; private set; }

        public int ProcessId => 1234;
        public string Status => "Recording";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; }

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        /// <summary>Clears the terminal and draws these rows, as the agent's output would.</summary>
        public void Show(IEnumerable<string> rows)
        {
            var text = "\u001b[2J\u001b[H" + string.Join("\r\n", rows);
            Buffer!.Write(System.Text.Encoding.UTF8.GetBytes(text));
        }

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Writes.Add(data);
        public Task SendTextAsync(string text) { SentTexts.Add(text); return Task.CompletedTask; }
        public Task SendEnterAsync() { Enters++; return Task.CompletedTask; }
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() => Buffer?.Dispose();
    }
}
