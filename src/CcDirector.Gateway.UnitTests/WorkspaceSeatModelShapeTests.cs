using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A seat's model arrives as TWO TYPES depending on the state of the thing it describes (issue #2722).
///
/// The hand-written restart index writes <c>"model": "claude-opus-5"</c> when the agent had reported one
/// and the whole folded display object when it had not. That is a shape none of the other defects on this
/// change had: everywhere else it was one VALUE carrying two facts, and the fix was to separate them.
/// Here the TYPE depends on the state, and a reader written against either shape is silently wrong
/// against the other - except that reading an object into a string is not silent at all, it throws, so a
/// real index handed to this schema was refused rather than stored.
///
/// These tests are what the answer "both shapes have a home" means in practice, rather than as a claim.
/// </summary>
public sealed class WorkspaceSeatModelShapeTests
{
    private static WorkspaceDocument Parse(string modelJson)
    {
        var json = $$"""
            {
              "id": "director-restart",
              "name": "DevThrottle_1 restart",
              "origin": "authored",
              "seats": [ { "name": "a seat", "agent": "ClaudeCode", "repoPath": "D:/repo",
                           "model": {{modelJson}} } ]
            }
            """;
        return JsonSerializer.Deserialize<WorkspaceDocument>(json, WorkspaceStore.DocumentJsonOptions)!;
    }

    [Fact]
    public void The_string_shape_is_the_recorded_model_id()
    {
        Assert.Equal("claude-opus-5", Parse("\"claude-opus-5\"").Seats[0].Model);
    }

    [Fact]
    public void The_object_shape_is_accepted_and_its_id_kept()
    {
        // The exact object the hand-written index carries for a session that had completed no turn. It
        // used to THROW - a JSON object cannot be read into a string - so the real index could not be
        // handed to this schema at all.
        var doc = Parse("""
            { "kind": "reported", "text": "opus-5", "modelId": "claude-opus-5",
              "tooltip": "claude-opus-5", "isAbsent": false }
            """);

        Assert.Equal("claude-opus-5", doc.Seats[0].Model);
    }

    [Fact]
    public void The_object_shape_with_no_recorded_model_reads_as_no_model()
    {
        // The commoner half of the real index: the folded verdict for a session that has not finished a
        // turn. Its modelId is null, and null is exactly what "no model recorded" means.
        var doc = Parse("""
            { "kind": "notRecordedYet", "text": "no model yet", "modelId": null,
              "tooltip": "No model recorded yet.", "isAbsent": true }
            """);

        Assert.Null(doc.Seats[0].Model);
    }

    [Fact]
    public void A_shape_that_is_neither_is_refused_and_says_so()
    {
        // Not a third silent behaviour. A number or an array here is a document nobody wrote on purpose.
        var ex = Assert.Throws<JsonException>(() => Parse("42"));
        Assert.Contains("recorded model id", ex.Message);
    }

    [Fact]
    public void It_is_always_written_back_as_the_string()
    {
        // So a document that has been through this build has ONE shape, and the next reader does not
        // have to know this history.
        var doc = Parse("""
            { "kind": "reported", "text": "opus-5", "modelId": "claude-opus-5",
              "tooltip": "claude-opus-5", "isAbsent": false }
            """);

        var written = JsonSerializer.Serialize(doc, WorkspaceStore.DocumentJsonOptions);
        Assert.Contains("\"model\":\"claude-opus-5\"", written);
    }

    [Fact]
    public void The_folded_verdict_has_its_own_field_and_survives_beside_the_id()
    {
        // The other four fields of the object shape are a FOLD - the Gateway computes them from the
        // recorded model and the driver's capabilities - and a workspace carries the folded verdict in
        // its own field when a capture stamps one. That is why keeping only the id off the object shape
        // is not a loss of fact: this is where the rendering lives.
        const string json = """
            {
              "id": "director-restart", "name": "x", "origin": "authored",
              "seats": [ { "name": "a seat", "agent": "ClaudeCode", "repoPath": "D:/repo",
                           "model": "claude-opus-5",
                           "modelDisplay": { "kind": "reported", "text": "opus-5",
                                             "modelId": "claude-opus-5", "tooltip": "claude-opus-5",
                                             "isAbsent": false } } ]
            }
            """;

        var doc = JsonSerializer.Deserialize<WorkspaceDocument>(json, WorkspaceStore.DocumentJsonOptions)!;
        Assert.Equal("claude-opus-5", doc.Seats[0].Model);
        Assert.Equal("reported", doc.Seats[0].ModelDisplay!.Kind);
        Assert.False(doc.Seats[0].ModelDisplay!.IsAbsent);
    }
}
