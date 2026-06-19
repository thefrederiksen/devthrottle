using CcDirector.AgentBrain;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The editable, versioned wingman-instructions surface for the Cockpit settings page (issue #537).
/// The wingman uses the ACTIVE instructions (the user's custom version, else the deployed default);
/// this exposes viewing, editing (new version), version history + revert, and the managed-default
/// flow (see the dev team's changes, switch to the latest default). Backed by
/// <see cref="WingmanInstructionsStore"/>.
/// </summary>
internal static class WingmanInstructionsEndpoint
{
    /// <summary>Cap on records re-run per A/B test - each one is a (serial, sometimes slow) brain call.</summary>
    private const int MaxTestRecords = 5;

    public static void Map(IEndpointRouteBuilder app, WingmanInstructionsStore store,
        WingmanTrainingStore training, Func<CancellationToken, Task<IAgentBrain>> brainProvider)
    {
        var translator = new WingmanTranslator(brainProvider);

        // Current state: the active instructions, whether they are customized, whether the dev team
        // has shipped a newer default, and the deployed-default identity.
        app.MapGet("/gateway/wingman/instructions", () =>
        {
            var active = store.Active();
            return Results.Json(new
            {
                active = Project(active),
                isCustomized = store.IsCustomized,
                updateAvailable = store.UpdateAvailable,
                defaultVersion = store.DefaultVersion,
                defaultHash = store.DefaultHash,
            });
        });

        // Save edited instructions as a new version and make them active.
        app.MapPut("/gateway/wingman/instructions", (WingmanInstructionsBody? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Content))
                return Results.Json(new { error = "content is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var v = store.Save(req.Content, req.Label);
                FileLog.Write($"[WingmanInstructionsEndpoint] saved version {v.Id}");
                return Results.Json(new { active = Project(v), isCustomized = store.IsCustomized });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Version history, newest first.
        app.MapGet("/gateway/wingman/instructions/versions", () =>
            Results.Json(new { versions = store.Versions().Select(Project).ToList() }));

        // Make an existing version active again.
        app.MapPost("/gateway/wingman/instructions/revert", (RevertBody? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Id))
                return Results.Json(new { error = "id is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (!store.Revert(req.Id))
                return Results.Json(new { error = "unknown version id" }, statusCode: StatusCodes.Status404NotFound);
            return Results.Json(new { active = Project(store.Active()), isCustomized = store.IsCustomized });
        });

        // The deployed default (the DevThrottle dev team's shipped instructions).
        app.MapGet("/gateway/wingman/instructions/default", () =>
        {
            var d = store.DefaultAsVersion();
            return Results.Json(new { version = store.DefaultVersion, hash = store.DefaultHash, content = d.Content });
        });

        // The managed-default review: is a newer default available, and what did the dev team change
        // (the acknowledged/based-on default -> the new default), so the page can show the diff.
        app.MapGet("/gateway/wingman/instructions/update", () =>
        {
            var (ackVersion, ackContent) = store.AcknowledgedDefault();
            return Results.Json(new
            {
                updateAvailable = store.UpdateAvailable,
                isCustomized = store.IsCustomized,
                acknowledgedDefaultVersion = ackVersion,
                acknowledgedDefaultContent = ackContent,
                newDefaultVersion = store.DefaultVersion,
                newDefaultContent = store.DefaultContent,
            });
        });

        // Adopt the deployed default (drop the custom version, acknowledge the latest default).
        app.MapPost("/gateway/wingman/instructions/switch-to-default", () =>
        {
            store.SwitchToDefault();
            return Results.Json(new { active = Project(store.Active()), isCustomized = store.IsCustomized, updateAvailable = store.UpdateAvailable });
        });

        // Recent captured training sessions (issue #537): the pool the user picks from to A/B-test a
        // draft prompt. Empty until the wingman_training_capture setting has been on for some turns.
        app.MapGet("/gateway/wingman/instructions/records", (int? limit) =>
        {
            var n = Math.Clamp(limit ?? 20, 1, 100);
            var records = training.ListRecords(n).Select(r => new
            {
                id = r.Id, source = r.Source, atUtc = r.AtUtc, sessionId = r.SessionId,
                replyPreview = r.ReplyPreview, spokenPreview = r.SpokenPreview,
            }).ToList();
            return Results.Json(new { records, captureEnabled = training.Enabled });
        });

        // A/B test (issue #537): re-run the DRAFT instructions over the chosen saved sessions and
        // return, per record, the agent reply, the wingman's ORIGINAL spoken output, and the NEW one
        // the draft produces - so the user sees the effect before saving. Does NOT change the live
        // instructions. Each record is a brain call, so the count is capped.
        app.MapPost("/gateway/wingman/instructions/test", async (InstructionsTestBody? req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Content))
                return Results.Json(new { error = "content (the draft instructions) is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (req.RecordIds is null || req.RecordIds.Length == 0)
                return Results.Json(new { error = "pick at least one saved session" }, statusCode: StatusCodes.Status400BadRequest);

            var ids = req.RecordIds.Take(MaxTestRecords).ToList();
            var results = new List<object>();
            foreach (var id in ids)
            {
                var rec = training.GetRecord(id);
                if (rec is null) { results.Add(new { id, error = "record not found" }); continue; }
                if (string.IsNullOrWhiteSpace(rec.Reply)) { results.Add(new { id, error = "record has no agent reply to translate" }); continue; }
                try
                {
                    var t = await translator.TranslateWithAsync(req.Content, rec.RecentContext, rec.Reply, ct);
                    results.Add(new { id, source = rec.Source, reply = rec.Reply, oldSpoken = rec.Spoken, newSpoken = t.Spoken, replySeconds = t.ReplySeconds });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[WingmanInstructionsEndpoint] test record {id} FAILED: {ex.Message}");
                    results.Add(new { id, source = rec.Source, reply = rec.Reply, oldSpoken = rec.Spoken, error = ex.Message });
                }
            }
            return Results.Json(new { results, ranCount = results.Count, capped = req.RecordIds.Length > MaxTestRecords });
        });
    }

    private static object Project(WingmanInstructionsStore.InstructionVersion v) => new
    {
        id = v.Id,
        label = v.Label,
        source = v.Source,
        createdAt = v.CreatedAtUtc,
        hash = v.Hash,
        contentLength = v.Content.Length,
        content = v.Content,
    };
}

/// <summary>Body of the save route: the edited instructions and an optional label.</summary>
public sealed class WingmanInstructionsBody
{
    public string Content { get; set; } = "";
    public string? Label { get; set; }
}

/// <summary>Body of the revert route: the version id to make active.</summary>
public sealed class RevertBody
{
    public string Id { get; set; } = "";
}

/// <summary>Body of the A/B test route: the draft instructions and the saved-session ids to re-run.</summary>
public sealed class InstructionsTestBody
{
    public string Content { get; set; } = "";
    public string[] RecordIds { get; set; } = Array.Empty<string>();
}
