namespace CcDirector.Gateway.Workflows;

/// <summary>
/// One step of a workflow: a named piece of work, who does it, who reviews it, and what finishing it
/// means. Reviewer is null when the step has no separate review seat - which is itself a statement the
/// workflow is making, not an omission.
/// </summary>
public sealed record WorkflowStep(
    string Name,
    string Description,
    string Doer,
    string? Reviewer,
    string Done);

/// <summary>
/// A workflow: a named, saved definition of how a piece of work gets done by agents. The workflow -
/// not the agent, and not a skill file in a repository - decides the shape of the work: which seats
/// exist, which seat starts, which seat reviews, and where the human is asked.
/// </summary>
public sealed record WorkflowDefinition(
    string Id,
    string Name,
    string Summary,
    string WhenToUse,
    string HumanCheckpoint,
    IReadOnlyList<WorkflowStep> Steps);

/// <summary>
/// The workflows the Gateway ships with (issue #1617). The first three are the shapes of work this fleet
/// already runs by hand, written down so they can be seen and chosen rather than being implied by
/// which skill file an agent happened to read. The fourth is the Fleet Manager's own conduct (issue
/// #2933): one per account, so it ships with the product rather than living in one account's library.
///
/// They are BUILT IN and read-only at this step, on purpose. The Gateway is the home for workflows -
/// it serves them, and every Director asks it rather than carrying a private copy - but authoring and
/// editing them (in the Cockpit, and later per-tenant for an organisation) is a later step. Serving a
/// fixed set first means the Cockpit page reads real Gateway data from day one instead of a stub that
/// has to be torn out.
/// </summary>
public static class BuiltInWorkflows
{
    private static readonly IReadOnlyList<WorkflowDefinition> Definitions = new[]
    {
        new WorkflowDefinition(
            Id: "mission",
            Name: "Mission",
            Summary: "An Architect settles the design, a Delivery Lead drives it to done, and Developers "
                   + "build. The owner is bothered once, at the report.",
            WhenToUse: "Work big enough to need a design settled before anyone builds, or work that runs "
                     + "across more than one phase.",
            HumanCheckpoint: "Once, at the report, from the Delivery Lead. There is no per-phase approval "
                           + "and no per-pull-request approval; only a genuinely undecidable call reaches "
                           + "the human before then.",
            Steps: new[]
            {
                new WorkflowStep(
                    Name: "Settle the design",
                    Description: "The Architect decides what is being built and why, and writes the mission "
                               + "document. Nothing is built until the design is settled.",
                    Doer: "Architect",
                    Reviewer: null,
                    Done: "The mission document exists with its required sections, the why and the goal are "
                        + "stated, and the owner has said go."),
                new WorkflowStep(
                    Name: "Drive",
                    Description: "The Delivery Lead drives the mission to done - phase by phase, seating the "
                               + "seats each phase needs. It never builds and it never reads diffs.",
                    Doer: "Delivery Lead",
                    Reviewer: null,
                    Done: "Every phase is merged and the mission's own check passes."),
                new WorkflowStep(
                    Name: "Build",
                    Description: "A Developer does one task, in its own worktree so two workstreams never "
                               + "share a tree, and proves it. The seat that accepts the work runs the "
                               + "mission's check itself rather than trusting the report.",
                    Doer: "Developer",
                    Reviewer: "Tech Lead, or the Delivery Lead when there is no Tech Lead",
                    Done: "A merged pull request with its proof. Committed and pushed is still in progress."),
                new WorkflowStep(
                    Name: "Land the record",
                    Description: "The Delivery Lead lands the mission's own paper trail - the mission document, "
                               + "rulings, handover notes, reviews, evidence - from inside the mission worktree. "
                               + "A record left uncommitted is one disk away from gone, and it is what the next "
                               + "seat rebuilds the mission from.",
                    Doer: "Delivery Lead",
                    Reviewer: null,
                    Done: "The mission's record is merged to the main branch."),
                new WorkflowStep(
                    Name: "Report",
                    Description: "The report goes to the owner, from the Delivery Lead. This is the one "
                               + "interruption the mission is allowed to spend.",
                    Doer: "Delivery Lead",
                    Reviewer: null,
                    Done: "The owner has one page to read."),
            }),

        new WorkflowDefinition(
            Id: "standalone",
            Name: "Standalone",
            Summary: "One agent picks up the work and finishes it. No manager, no review seat.",
            WhenToUse: "Work small enough that a second pair of eyes would cost more than it catches - a "
                     + "typo, a version bump, a one-line fix with a test already around it.",
            HumanCheckpoint: "Once, when the work is merged.",
            Steps: new[]
            {
                new WorkflowStep(
                    Name: "Do the work",
                    Description: "One agent takes the work from the request to a merged pull request, in its own "
                               + "worktree cut from the main branch.",
                    Doer: "Worker",
                    Reviewer: null,
                    Done: "A merged pull request."),
            }),

        new WorkflowDefinition(
            Id: "standalone-with-review",
            Name: "Standalone with review",
            Summary: "One agent does the work, a second and separate agent reviews it before it is called done.",
            WhenToUse: "The default for ordinary work. Small enough not to need an Architect, big enough that "
                     + "nobody should mark their own homework.",
            HumanCheckpoint: "Once, when the review passes.",
            Steps: new[]
            {
                new WorkflowStep(
                    Name: "Do the work",
                    Description: "One agent takes the work to a pull request, with the proof that it does what it "
                               + "is supposed to.",
                    Doer: "Worker",
                    Reviewer: null,
                    Done: "A pull request with proof attached."),
                new WorkflowStep(
                    Name: "Review",
                    Description: "A SEPARATE agent - never the one that wrote it - verifies the work against the "
                               + "reported symptom rather than trusting the report, then passes it or sends it back "
                               + "with a written defect.",
                    Doer: "Reviewer",
                    Reviewer: null,
                    Done: "The reviewer passed it, and the pull request is merged."),
            }),

        new WorkflowDefinition(
            Id: "fleet-manager",
            Name: "Fleet Manager",
            Summary: "The one session the owner talks to. It starts and owns the sessions that do the work, "
                   + "acts on the Wingman's reading of every stop, and brings back only three kinds of news: "
                   + "ready, found, or needs your decision.",
            WhenToUse: "The owner's Fleet Manager session, one per account. Not for any other session.",
            HumanCheckpoint: "Only for a Ready, a Finding or a Decision - product, scope, money and anything "
                           + "irreversible always go to the owner.",
            Steps: new[]
            {
                new WorkflowStep(
                    Name: "Take the request",
                    Description: "Work out the repository and the shape - a change, a report, or a Mission - and "
                               + "write the instructions with the owner's words unchanged as the intent.",
                    Doer: "Fleet Manager",
                    Reviewer: null,
                    Done: "A session it owns has started on the instructions."),
                new WorkflowStep(
                    Name: "Act on each stop",
                    Description: "Read the Wingman's reading of every stop of the sessions it owns and act on it: "
                               + "answer inside the owner's mandate, recover once, or raise a Decision. It never "
                               + "does the work and never summarises a session itself.",
                    Doer: "Fleet Manager",
                    Reviewer: null,
                    Done: "Every stop is answered, recovered, or open as a Decision for the owner."),
                new WorkflowStep(
                    Name: "Bring back the outcome",
                    Description: "Judge the finished work, then bring the owner a Ready or a Finding and carry out "
                               + "the answer: merge, send back, or close once the work has landed.",
                    Doer: "Fleet Manager",
                    Reviewer: "Owner",
                    Done: "The owner has answered and the answer is carried out."),
            }),
    };

    /// <summary>Every workflow the Gateway ships, in the order the Cockpit lists them. Since the
    /// catalog moved to the persisted workflow store these are the SEED SOURCE, not the served set:
    /// the seeder writes them into the store at startup and the endpoints read the store.</summary>
    public static IReadOnlyList<WorkflowDefinition> All() => Definitions;

    /// <summary>
    /// The shipped instruction body (the authoritative conduct markdown) for a built-in workflow, read
    /// from the embedded <c>Workflows/Content/&lt;id&gt;.instructions.md</c> resource. Kept as .md
    /// resources, not string literals, so the text stays diffable. Fail-loud on a missing resource - a
    /// built-in without its conduct is a build defect, not a runtime condition.
    /// </summary>
    public static string InstructionsFor(string id)
    {
        var resourceName = $"CcDirector.Gateway.Workflows.Content.{id}.instructions.md";
        var assembly = typeof(BuiltInWorkflows).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded workflow instructions '{resourceName}' are missing from the Gateway binary. " +
                "Every built-in workflow must ship its instruction body.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
