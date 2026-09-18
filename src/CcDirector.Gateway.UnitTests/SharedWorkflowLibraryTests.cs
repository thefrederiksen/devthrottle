using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The shared workflow library (devthrottle_internal issue 514, phase 1). The built-in DevThrottle
/// workflows are seeded ONCE into the shared library partition (the reserved System tenant on the
/// hosted Gateway) and served READ-ONLY to every tenant: a tenant's catalog is the shared built-ins
/// UNION its own workflows. These tests run the stores hosted-style - an
/// <see cref="AsyncLocalTenantContext"/> over one database, seeding under the System scope exactly as
/// the composition root does, then reading under real account-tenant scopes.
///
/// The first test is the reproduction of the defect this phase fixes: before the shared-library
/// read, a hosted tenant's catalog was EMPTY (built-ins sat in the System partition, requests read
/// the account partition, and the two never met - no mission workflow anywhere on hosted).
/// </summary>
public sealed class SharedWorkflowLibraryTests : IDisposable
{
    private static readonly TenantId TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly TenantId TenantB = new("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _tenant = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>Open the database hosted-style and construct the stores under the reserved System
    /// scope, exactly as the composition root does at startup (seeding lands in the System partition).</summary>
    private (WorkflowStore Workflows, WorkflowRunStore Runs) OpenHostedStores()
    {
        var db = _harness.Open(_tenant);
        using (_tenant.Enter(TenantId.System))
        {
            var workflows = new WorkflowStore(db);
            var runs = new WorkflowRunStore(db);
            return (workflows, runs);
        }
    }

    private static WorkflowContentRequest PublishableContent(string id) => new()
    {
        Id = id,
        Name = "Acme flow",
        Summary = "Tenant-owned test workflow.",
        Steps = new List<WorkflowStepDto>
        {
            new() { Name = "Do", Description = "Do the thing.", Doer = "Worker", Reviewer = null, Done = "Done." },
        },
        InstructionsMarkdown = "# Acme flow\n\nDo the thing.",
        AuthoredBy = "test-session",
    };

    // ---- the defect reproduction, then the shared read ---------------------------------------------

    [Fact]
    public void HostedTenant_ListsTheBuiltInLibrary()
    {
        var (workflows, _) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            var catalog = workflows.ListPublished();

            // Before the shared-library read this catalog was EMPTY - the hosted defect.
            Assert.Contains(catalog, w => w.Id == "mission");
            Assert.Contains(catalog, w => w.Id == "standalone");
            Assert.Contains(catalog, w => w.Id == "standalone-with-review");
            Assert.All(catalog, w => Assert.True(w.IsBuiltIn));
        }
    }

    [Fact]
    public void HostedTenant_ReadsTheMissionConduct()
    {
        var (workflows, _) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            var dto = workflows.GetPublished("mission");
            Assert.NotNull(dto);
            Assert.True(dto!.IsBuiltIn);
            Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), workflows.GetInstructions("mission", null));
        }
    }

    [Fact]
    public void PinnedVersionRead_ResolvesFromTheLibrary()
    {
        var (workflows, _) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            // A seated run pins an explicit version; the pinned read must resolve from the shared
            // partition so a run's conduct never disappears under it.
            Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), workflows.GetInstructions("mission", 1));
            var versions = workflows.ListVersions("mission");
            Assert.NotNull(versions);
            Assert.Contains(versions!, v => v.Version == 1 && v.Status == "published");
            var detail = workflows.GetVersionDetail("mission", 1);
            Assert.NotNull(detail);
            Assert.Equal("Mission", detail!.Name);
        }
    }

    // ---- tenant isolation around the shared library ------------------------------------------------

    [Fact]
    public void TenantWorkflows_StayIsolated_WhileBuiltInsAreShared()
    {
        var (workflows, _) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            workflows.CreateDraft(PublishableContent("acme-flow"));
            workflows.Publish("acme-flow");

            var mine = workflows.ListPublished();
            Assert.Equal(5, mine.Count); // four shared built-ins + my own
            Assert.Contains(mine, w => w.Id == "acme-flow" && !w.IsBuiltIn);
        }

        using (_tenant.Enter(TenantB))
        {
            var theirs = workflows.ListPublished();
            Assert.Equal(4, theirs.Count); // the shared built-ins ONLY - never another tenant's workflow
            Assert.DoesNotContain(theirs, w => w.Id == "acme-flow");
            Assert.Null(workflows.GetPublished("acme-flow"));
            Assert.Null(workflows.GetInstructions("acme-flow", null));
        }
    }

    [Fact]
    public void CreateDraft_RefusesABuiltInId_TheLibraryCanNeverBeShadowed()
    {
        var (workflows, _) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            // Without this refusal a hosted tenant (whose own partition holds no built-ins) could
            // mint its own "mission" and shadow the library.
            Assert.Throws<WorkflowConflictException>(() => workflows.CreateDraft(PublishableContent("mission")));
        }
    }

    // ---- runs: a hosted tenant can actually execute the library conduct ----------------------------

    [Fact]
    public void HostedTenant_CanRunTheMissionWorkflow()
    {
        var (_, runs) = OpenHostedStores();

        Guid runId;
        using (_tenant.Enter(TenantA))
        {
            var run = runs.Create("mission", "Shared library proof run");
            runId = run.Id;
            Assert.Equal("mission", run.WorkflowId);
            Assert.Equal(1, run.WorkflowVersion); // pinned to the published library version
            Assert.NotNull(runs.Get(runId));
        }

        using (_tenant.Enter(TenantB))
        {
            // The RUN is tenant data and stays isolated even though the WORKFLOW is shared.
            Assert.Null(runs.Get(runId));
        }
    }

    [Fact]
    public void HostedTenant_MissionCreatePathAnswers_EnsureRunnableAndEnabled()
    {
        var (_, runs) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            // The Gateway's mission-create path asks these two questions before creating a mission;
            // both must resolve the shared library on hosted.
            runs.EnsureRunnable("mission"); // must not throw
            Assert.True(runs.IsWorkflowEnabled("mission"));
            Assert.True(runs.GetWorkflowEnabled("mission"));
        }
    }

    // ---- self-host stays exactly as it was ---------------------------------------------------------

    [Fact]
    public void SelfHost_SingleTenant_ServesTheSameCatalogAsBefore()
    {
        var db = _harness.Open(); // SingleTenantContext - everything is the Local tenant
        var workflows = new WorkflowStore(db);

        var catalog = workflows.ListPublished();
        Assert.Equal(4, catalog.Count);
        Assert.Contains(catalog, w => w.Id == "mission");
        Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), workflows.GetInstructions("mission", null));
    }
}
