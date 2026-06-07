using System.Text;
using System.Text.Json;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// THE frozen v3 turn-brief contract in code form (issue #185; v2.4 added the
/// mission-complete suggestedAction #201; v3 adds the COLD-READER bar #205: situation
/// recap, complete options with a recommended pick, ifIgnored, allClear as a first-class
/// verdict; v3.1 adds the review-round-1 rules #208: PARKED REPLY, statement-as-ask
/// tightness, clean evidence receipts, and the ContractVersion stamp; v3.2 makes the
/// parked reply MECHANICAL #208 rounds 2-3: TurnPackage.ParkedComposerText gets its own
/// prompt section + two validation invariants, plus JARGON/NAME-THE-PROJECT/limit-banner
/// rules): the prompt that
/// asks the model to interpret one turn, and the mechanical validation of its JSON
/// answer. The ONE producer is the Gateway's warm-brain brief agent (#187 deleted the
/// Director-side pipeline) - a prompt change lands here and reaches the whole fleet via
/// the Gateway.
///
/// Everything here is pure: no model calls, no I/O beyond logging. D5/D6 apply - validation
/// is mechanical (length caps, invariants, verbatim evidence checks), never interpretation.
/// </summary>
public static class TurnBriefContract
{
    /// <summary>Stamped into every brief (issue #208) so review rounds and the eval
    /// harness can tell which contract produced a brief. Bump on every prompt/validation
    /// change.</summary>
    public const string Version = "v3.2";

    // ====================================================================
    // Prompt - built from the captured examples (docs/architecture/wingman/examples/)
    // ====================================================================

    public static string BuildPrompt(TurnPackage p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the WINGMAN: you brief a busy engineering lead the moment one of their");
        sb.AppendLine("AI coding agents finishes a turn. The lead runs many agents; your brief is the");
        sb.AppendLine("only thing they read. Your job is INTERPRETATION - reduce their cognitive load.");
        sb.AppendLine();
        sb.AppendLine("THE COLD-READER BAR (every needsYou must pass it): the reader has not seen this");
        sb.AppendLine("session for HOURS and remembers NOTHING. They will read ONLY your brief. They");
        sb.AppendLine("must be able to act correctly within 10 seconds. TIGHT beats complete: every");
        sb.AppendLine("sentence that is not needed for the decision is clutter - cut it.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object (no fences, no prose) in exactly this shape:");
        sb.AppendLine("""
{
  "headline": "<=6 words, newspaper-tight (e.g. 'Cockpit gets session story column'): the current CHAPTER's title - WHAT the session is working on, never how. Usually copied verbatim from the current title; refine the wording only if the same work drifted.",
  "newChapter": false | true ONLY when this turn moved the session to a genuinely DIFFERENT piece of work (then headline is that new chapter's title),
  "turnTitle": "<=8 words, past tense: what THIS turn did - a card header, not a sentence.",
  "intent": "1-2 sentences: what the USER is trying to get done, carried/updated across turns. NEVER the literal last message (a 'yes' must become what was approved).",
  "did": ["3-6 bullets, past tense, specific, <=15 words each. Proportional: trivial turn -> 1-2 bullets."],
  "needsYou": null OR {
    "statement": "Opens with ONE sentence of situation recap that assumes zero memory (what the work is, where it stands), THEN the decision. Lead the decision part with whether anything is broken/blocking.",
    "answerVia": "reply" | "keys",
    "selectionMode": "single" | "multiple",
    "submit": null | "\r",
    "options": [ { "key": "short label", "send": "exact text or key sequence", "note": "REQUIRED: the consequence and risk of choosing this (<=18 words) - 'yes' to WHAT, and what happens then", "recommended": false | true on AT MOST ONE option, reason inside its note } ],
    "evidence": "EXACT verbatim quote from the AGENT REPLY or the SCREEN - copied character-for-character, never paraphrased. Prefer a clean sentence from the agent reply over picker/menu UI fragments (a garbled '[ ] Commit Approve...' receipt is worthless to the reader).",
    "urgency": "blocking" | "review" | "fyi",
    "confidence": "high" | "ambiguous",
    "railLine": "<= 8 words",
    "ifIgnored": "one line: what happens if the user does NOTHING - is the session blocked, will the agent proceed anyway, does it expire? REQUIRED when urgency=blocking."
  },
  "allClear": null OR "REQUIRED when needsYou is null: one CONCRETE line proving nothing is needed ('v0.3.0 published and live - nothing to do'), never generic filler.",
  "suggestedAction": null OR { "type": "close_session", "reason": "<=12 words: why this session is finished" }
}
""");
        sb.AppendLine();
        sb.AppendLine("Rules learned from real captures:");
        sb.AppendLine("- PARKED REPLY (review rounds 1-3's headline finding): when this prompt contains a");
        sb.AppendLine("  '=== PARKED, UNSENT USER REPLY ===' section, the user has ALREADY typed that");
        sb.AppendLine("  reply but NOT sent it - it was extracted mechanically from the composer, do not");
        sb.AppendLine("  second-guess it. The brief's job collapses to getting it sent: the statement");
        sb.AppendLine("  MUST quote it ('Your reply \"<parked text>\" is typed but unsent'); answerVia=keys;");
        sb.AppendLine("  the recommended option is key 'send my typed reply', send \"\\r\" (Enter submits");
        sb.AppendLine("  exactly what is parked - never resend the text itself, that would double it).");
        sb.AppendLine("  Alternative options stay regular sendable options whose note warns the parked");
        sb.AppendLine("  text must be cleared first. NEVER present a decision as open when the parked");
        sb.AppendLine("  text already makes it. (No section, but the screen bottom clearly shows typed");
        sb.AppendLine("  composer text? Apply the same treatment and say the send-state is uncertain.)");
        sb.AppendLine("- JARGON: write for a veteran developer who is NOT versed in this specific stack.");
        sb.AppendLine("  Explain every term of art inline or cut it; give issue/work-item numbers a 2-4");
        sb.AppendLine("  word gloss on first use ('#7624 - the deploy-protection fix'); NEVER put commit");
        sb.AppendLine("  shas on the card. Product names (Director, Gateway, Cockpit, wingman) are known");
        sb.AppendLine("  vocabulary. An option may not reference a group/scope the card never defines.");
        sb.AppendLine("- NAME THE PROJECT: the reader runs many sessions across repos - the first time");
        sb.AppendLine("  issue numbers or files appear, anchor them ('cc-director #218', 'mindzieWeb");
        sb.AppendLine("  #7624') unless the statement already names the product.");
        sb.AppendLine("- THE STATEMENT IS AN ASK, NOT A HOW-TO: at most ONE imperative in the statement.");
        sb.AppendLine("  When the agent gave multi-step instructions, name the outcome and where the steps");
        sb.AppendLine("  are ('the steps are on screen in the terminal') - never enumerate them in the");
        sb.AppendLine("  statement. Side-tasks get half a sentence at the end, not the middle.");
        sb.AppendLine("- REPLY-PENDING + an on-screen menu (picker/permission/plan approval): the question");
        sb.AppendLine("  exists ONLY on the SCREEN. Read it there. answerVia=keys; options' send = the key");
        sb.AppendLine("  that selects (e.g. \"1\") - pickers confirm with Enter, so send \"1\\r\" for them.");
        sb.AppendLine("- Permission prompts: surface SCOPE. 'Yes, and don't ask again for: git *' is a");
        sb.AppendLine("  standing grant, not a yes - flag it in the option's note.");
        sb.AppendLine("- 'pick any that apply' checklists: selectionMode=multiple, option sends TOGGLE");
        sb.AppendLine("  (just the number), submit=\"\\r\" completes.");
        sb.AppendLine("- Plan approval: summarize THE PLAN in the statement - the decision is about the");
        sb.AppendLine("  plan's content, not menu mechanics.");
        sb.AppendLine("- Nothing blocking but a real decision exists: urgency=fyi, statement starts");
        sb.AppendLine("  'Nothing needed. FYI: ...'. No ask at all: needsYou=null + a concrete allClear.");
        sb.AppendLine("- OPTIONS COMPLETE: never offer an option whose consequence the reader cannot");
        sb.AppendLine("  see ('sweep all' of WHAT?). The note carries consequence + risk. If one choice");
        sb.AppendLine("  is clearly safer/better, mark it recommended and say why in its note. Never");
        sb.AppendLine("  recommend when genuinely unsure - a wrong recommendation costs trust.");
        sb.AppendLine("- A named action ALWAYS comes with its option/button. If the statement tells the");
        sb.AppendLine("  user to do X, there must be an option (or suggestedAction) that does X.");
        sb.AppendLine("- Operational side-facts (usage limits, etc.) go in ifIgnored, not mid-statement.");
        sb.AppendLine("  A usage/limit banner visible on screen ALWAYS lands in ifIgnored - never dropped.");
        sb.AppendLine("- Unclear what the agent wants: confidence=ambiguous and SAY SO in the statement");
        sb.AppendLine("  ('unclear; likely X or Y'). Never invent certainty. Never invent options.");
        sb.AppendLine("- The screen may contain rendering tears (overdrawn lines); read through them.");
        sb.AppendLine("- CHAPTERS: the headline is the current chapter's title - WHAT is being worked on,");
        sb.AppendLine("  not how. Several turns share one chapter: questions, fix-ups, tests, reviews, and");
        sb.AppendLine("  sub-steps of the same work are the SAME chapter (newChapter=false). Rewording the");
        sb.AppendLine("  title because the same work drifted is NOT a new chapter. newChapter=true only");
        sb.AppendLine("  when the session moved to a different piece of work (new feature, new bug, new");
        sb.AppendLine("  goal) - returning to earlier work later IS a new chapter (same title is fine).");
        sb.AppendLine("  When in doubt: newChapter=false and KEEP the current title.");
        sb.AppendLine("- MISSION COMPLETE: when the session's goal has been DELIVERED - the requested");
        sb.AppendLine("  bug report/issue was filed and confirmed (URL in the reply), the question was");
        sb.AppendLine("  answered, the artifact was produced - and the agent asks nothing that blocks,");
        sb.AppendLine("  set suggestedAction={\"type\":\"close_session\",\"reason\":...}. Then needsYou");
        sb.AppendLine("  reflects it: statement names what was delivered and says the real action is");
        sb.AppendLine("  closing this session; urgency=fyi; railLine like 'done - close session?'.");
        sb.AppendLine("- NEVER suggest close_session when requested work is unfinished, changes the user");
        sb.AppendLine("  wanted committed are uncommitted, an approval or question is pending, or you");
        sb.AppendLine("  are unsure. The user approves the close with one click - a wrong suggestion");
        sb.AppendLine("  costs trust. When in doubt: suggestedAction=null.");
        sb.AppendLine();
        sb.AppendLine("=== SESSION CONTEXT ===");
        sb.AppendLine($"Current chapter title: {(string.IsNullOrWhiteSpace(p.CurrentHeadline) ? "(none yet - write the first one)" : p.CurrentHeadline)}");
        sb.AppendLine($"Prior rolling intent: {p.RollingIntent ?? "(first brief of this session)"}");
        if (p.PriorRailLines.Count > 0)
            sb.AppendLine("Recent needs-you lines: " + string.Join(" | ", p.PriorRailLines));
        sb.AppendLine($"First user prompt (session goal seed): {Truncate(p.FirstUserPrompt, 600)}");
        sb.AppendLine();
        sb.AppendLine("=== THIS TURN'S USER PROMPT ===");
        sb.AppendLine(Truncate(p.LastUserPrompt, 2000));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(p.ParkedComposerText))
        {
            sb.AppendLine("=== PARKED, UNSENT USER REPLY (extracted mechanically from the composer) ===");
            sb.AppendLine(p.ParkedComposerText);
            sb.AppendLine();
        }
        sb.AppendLine($"=== TRANSCRIPT OF THE TURN (reply-pending: {p.ReplyPending}) ===");
        sb.AppendLine(p.TranscriptDelta);
        sb.AppendLine();
        sb.AppendLine("=== CURRENT SCREEN (bottom of the terminal) ===");
        sb.AppendLine(p.ScreenTail);
        return sb.ToString();
    }

    // ====================================================================
    // Parse + validate (D5/D6: mechanical VALIDATION of the model, never interpretation)
    // ====================================================================

    public static TurnBriefDto? ParseAndValidate(string raw, TurnPackage package, string generatorId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Models sometimes wrap JSON in fences despite instructions; unwrap mechanically.
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var first = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && lastFence > first) json = json[(first + 1)..lastFence].Trim();
        }

        // v3.2 (issue #208, replay rounds 2+4): models also narrate a sentence before the
        // JSON despite instructions. Same class of quirk as fences - absorbed mechanically
        // by trimming to the outermost brace pair. Still rejected if what remains is not
        // valid JSON.
        if (!json.StartsWith('{'))
        {
            var open = json.IndexOf('{');
            var close = json.LastIndexOf('}');
            if (open >= 0 && close > open) json = json[open..(close + 1)];
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            FileLog.Write($"[TurnBriefContract] validation: not JSON ({ex.Message})");
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var brief = new TurnBriefDto
            {
                SessionId = package.SessionId.ToString(),
                TurnNumber = package.TurnCount,
                GeneratedAtUtc = DateTime.UtcNow,
                Model = generatorId,
                ContractVersion = Version,
                // The turn's user prompt as the wingman saw it (v3.2, issue #208):
                // dictated @file prompts were already resolved to words on the package,
                // so consumers can render a real YOU ASKED instead of an opaque path.
                YouAsked = string.IsNullOrWhiteSpace(package.LastUserPrompt)
                    ? null
                    : Truncate(package.LastUserPrompt, 600),
                Degraded = false,
                Headline = Str(root, "headline"),
                TurnTitle = Str(root, "turnTitle"),
                Intent = root.TryGetProperty("intent", out var i) ? (i.GetString() ?? "").Trim() : "",
            };
            if (string.IsNullOrWhiteSpace(brief.Intent))
            {
                FileLog.Write("[TurnBriefContract] validation: missing intent");
                return null;
            }

            // Headline = chapter title (v2.3): an omitted headline carries the session's
            // current one forward (mechanical carry, not invention) - the standing title must
            // survive a model that skipped the field. Length caps are mechanical validation.
            if (string.IsNullOrWhiteSpace(brief.Headline))
                brief.Headline = package.CurrentHeadline ?? "";
            if (brief.Headline.Length > 60) brief.Headline = brief.Headline[..60];
            if (brief.TurnTitle.Length > 60) brief.TurnTitle = brief.TurnTitle[..60];

            // Chapter break (v2.3): explicit wingman judgment, never string comparison.
            brief.NewChapter = root.TryGetProperty("newChapter", out var nc) && nc.ValueKind == JsonValueKind.True;
            // The session's FIRST title mechanically starts the first chapter, whatever the
            // model said - a session with briefs but no chapter start renders nowhere.
            if (string.IsNullOrWhiteSpace(package.CurrentHeadline) && !string.IsNullOrWhiteSpace(brief.Headline))
                brief.NewChapter = true;

            if (root.TryGetProperty("did", out var did) && did.ValueKind == JsonValueKind.Array)
                brief.Did = did.EnumerateArray()
                    .Select(b => (b.GetString() ?? "").Trim())
                    .Where(s => s.Length > 0)
                    .Take(8)
                    .ToList();

            if (root.TryGetProperty("needsYou", out var ny) && ny.ValueKind == JsonValueKind.Object)
            {
                var n = new TurnBriefNeedsYou
                {
                    Statement = Str(ny, "statement"),
                    AnswerVia = Str(ny, "answerVia") is "keys" ? "keys" : "reply",
                    SelectionMode = Str(ny, "selectionMode") is "multiple" ? "multiple" : "single",
                    Submit = ny.TryGetProperty("submit", out var sub) && sub.ValueKind == JsonValueKind.String ? sub.GetString() : null,
                    Evidence = Str(ny, "evidence"),
                    Urgency = Str(ny, "urgency") switch { "blocking" => "blocking", "fyi" => "fyi", _ => "review" },
                    Confidence = Str(ny, "confidence") is "ambiguous" ? "ambiguous" : "high",
                    RailLine = Str(ny, "railLine"),
                    IfIgnored = ny.TryGetProperty("ifIgnored", out var ig) && ig.ValueKind == JsonValueKind.String
                        ? (ig.GetString() ?? "").Trim() is { Length: > 0 } igText ? igText : null
                        : null,
                };
                if (string.IsNullOrWhiteSpace(n.Statement) || string.IsNullOrWhiteSpace(n.RailLine))
                {
                    FileLog.Write("[TurnBriefContract] validation: needsYou missing statement/railLine");
                    return null;
                }
                if (n.RailLine.Length > 60) n.RailLine = n.RailLine[..60];
                if (n.IfIgnored is { Length: > 200 }) n.IfIgnored = n.IfIgnored[..200];

                if (ny.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        var key = Str(o, "key");
                        var send = Str(o, "send");
                        // v3.2 (issue #208): a send of just "\r" (press Enter - submits a
                        // parked composer reply) is all control characters, which Str()'s
                        // Trim destroys. Preserve control-only sends verbatim; everything
                        // else keeps the historical trim.
                        if (send.Length == 0
                            && o.TryGetProperty("send", out var sendRaw)
                            && sendRaw.ValueKind == JsonValueKind.String
                            && sendRaw.GetString() is { Length: > 0 } rawSend
                            && rawSend.All(c => c is '\r' or '\n'))
                        {
                            send = rawSend;
                        }
                        if (key.Length == 0 || send.Length == 0) continue;
                        n.Options.Add(new TurnBriefOption
                        {
                            Key = key.Length > 60 ? key[..60] : key,
                            Send = send,
                            Note = o.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null,
                            Recommended = o.TryGetProperty("recommended", out var rec) && rec.ValueKind == JsonValueKind.True,
                        });
                        if (n.Options.Count == 6) break;
                    }

                    // v3 invariant: AT MOST ONE recommended option - extra flags are dropped
                    // mechanically (first wins), never the brief.
                    var seenRecommended = false;
                    foreach (var option in n.Options)
                    {
                        if (!option.Recommended) continue;
                        if (seenRecommended)
                        {
                            FileLog.Write("[TurnBriefContract] validation: multiple recommended options; dropping extra flags");
                            option.Recommended = false;
                        }
                        seenRecommended = true;
                    }
                }

                // Contract invariants (the captures' findings):
                // multiple REQUIRES a submit send - a checklist without submit is unanswerable.
                if (n.SelectionMode == "multiple" && string.IsNullOrEmpty(n.Submit))
                {
                    FileLog.Write("[TurnBriefContract] validation: multiple without submit");
                    return null;
                }

                // v3 invariant (issue #205): a BLOCKING ask without "what happens if I do
                // nothing" fails the cold-reader test - the brief is rejected, the degrade
                // stub marks the failure honestly.
                if (n.Urgency == "blocking" && string.IsNullOrWhiteSpace(n.IfIgnored))
                {
                    FileLog.Write("[TurnBriefContract] validation: blocking without ifIgnored");
                    return null;
                }

                // v3.2 invariant (issue #208, review rounds 1-3): a mechanically captured
                // parked composer reply MUST be quoted in the statement - a brief that
                // re-asks a decision the user already typed is the worst staleness. The
                // check is whitespace-tolerant, same machinery as evidence receipts.
                if (!string.IsNullOrWhiteSpace(package.ParkedComposerText)
                    && BriefBuilder.FindVerbatim(n.Statement, package.ParkedComposerText) is null)
                {
                    FileLog.Write("[TurnBriefContract] validation: parked composer reply not quoted in statement");
                    return null;
                }

                // Evidence must be VERBATIM from the reply or the screen (whitespace-tolerant).
                // Failed validation does not kill the brief - it kills the RECEIPTS, visibly.
                if (!string.IsNullOrWhiteSpace(n.Evidence))
                {
                    var inReply = package.LastAssistantText is not null && BriefBuilder.FindVerbatim(package.LastAssistantText, n.Evidence) is not null;
                    var onScreen = BriefBuilder.FindVerbatim(package.ScreenTail, n.Evidence) is not null;
                    if (!inReply && !onScreen)
                    {
                        FileLog.Write("[TurnBriefContract] validation: evidence not verbatim; dropping receipts");
                        n.Evidence = "";
                    }
                }

                brief.NeedsYou = n;
            }

            // v3.2 invariant (issue #208): a parked composer reply means the user must act
            // (send or clear it) - a needsYou=null brief would hide that. Rejected.
            if (!string.IsNullOrWhiteSpace(package.ParkedComposerText) && brief.NeedsYou is null)
            {
                FileLog.Write("[TurnBriefContract] validation: parked composer reply but needsYou is null");
                return null;
            }

            // All-clear verdict (v3, issue #205): the concrete "nothing needed" line for
            // needsYou=null briefs. Mechanical: parsed when present and the turn truly
            // needs nothing - an allClear next to a needsYou is contradictory and dropped.
            if (root.TryGetProperty("allClear", out var ac) && ac.ValueKind == JsonValueKind.String)
            {
                var allClear = (ac.GetString() ?? "").Trim();
                if (allClear.Length > 0)
                {
                    if (brief.NeedsYou is not null)
                        FileLog.Write("[TurnBriefContract] validation: allClear alongside needsYou; dropping allClear");
                    else
                        brief.AllClear = allClear.Length > 250 ? allClear[..250] : allClear;
                }
            }

            // Suggested action (v2.4, issue #201): ENUMERATED vocabulary, mechanically
            // enforced - an unknown type or a missing reason drops the ACTION, never the
            // brief (same philosophy as evidence receipts).
            if (root.TryGetProperty("suggestedAction", out var sa) && sa.ValueKind == JsonValueKind.Object)
            {
                var actionType = Str(sa, "type");
                var reason = Str(sa, "reason");
                if (actionType == "close_session" && reason.Length > 0)
                {
                    brief.SuggestedAction = new TurnBriefSuggestedAction
                    {
                        Type = actionType,
                        Reason = reason.Length > 100 ? reason[..100] : reason,
                    };
                }
                else
                {
                    FileLog.Write($"[TurnBriefContract] validation: suggestedAction invalid (type='{actionType}', reason len={reason.Length}); dropping action");
                }
            }

            return brief;
        }
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";
}
