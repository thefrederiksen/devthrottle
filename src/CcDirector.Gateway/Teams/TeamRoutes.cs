using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Skills;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Teams;

/// <summary>What the team routes are mapped with. Built once by the host; built again, over a throwaway database, by
/// the tests that walk the mapped route table.</summary>
internal sealed record TeamRouteServices(
    TeamRegistry Teams,
    TeamAccess Access,
    HostedTenantBoundary Boundary,
    TenantRegistry Tenants,
    DeviceRegistry Devices,
    TeamFleetMap FleetMap,
    ITeamInvitationMailer InvitationMailer,
    Func<string?> PublicBase,
    TeamRequestStore Requests,
    SkillStore Skills,
    WorkflowStore Workflows,
    TeamMentorStore MentorStore,
    Func<TenantId, string> TimeZoneOf,
    bool MentorRunning,
    DevReportStore Reports,
    DevReportRecipients Recipients,
    DevReportPersonComments Comments,
    DevReportDelivery Delivery,
    Factory.Registry.FactoryRegistryStore Factories,
    Factory.FactoryActivityRecord Activity,
    FactoryContributors FactoryContributors,
    Factory.FactoryAgentsSwitch FactorySwitch,
    Factory.Inbox.FactoryInboxStore FactoryInbox,
    Action<TenantId, string, DateTime> FactoryInboxMessageSent,
    Func<DateTime>? UtcNow = null);

/// <summary>
/// EVERY TEAM ROUTE IS MAPPED HERE, IN ONE PLACE: everything under <c>/teams</c> and <c>/team-invitations</c>, and the
/// personal Mentor page beside them. The host calls this only while Teams is released (<see cref="TeamsReleaseSwitch"/>).
///
/// It is one method so the route table it produces can be walked in memory, with no port: the person-only walk
/// (<c>TeamPersonOnlyRouteWalkTests</c>) maps exactly what the host maps and calls every route under the two families
/// with a Director's key and with a session key. A team route mapped anywhere else would not be walked, so a new team
/// route family is added HERE.
/// </summary>
internal static class TeamRoutes
{
    /// <summary>Maps every team route.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamRouteServices s)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(s);

        TeamEndpoints.Map(app, s.Teams, s.Boundary, s.Tenants, s.FleetMap, s.Devices);
        // Invitations by email that expire (devthrottle_internal#2301).
        TeamInvitationEndpoints.Map(app, s.Teams, s.Boundary, s.Tenants, s.InvitationMailer, s.PublicBase);
        // Requests to the Owner and Managers (devthrottle_internal#2308). People only - a request is never readable
        // with a session key or a Director's key.
        // The Owner's "Forward to the factory" (piece 4.3) is the one way those words reach a factory: quoted, marked, in
        // the Owner's name, into a factory of the same team.
        var forwarding = new TeamRequestForwarding(s.Requests, s.Teams, s.Factories, s.FactorySwitch,
            (tenant, input, ct) => s.FactoryInbox.SendAsync(tenant, input, ct), s.FactoryInboxMessageSent, s.UtcNow);
        TeamRequestEndpoints.Map(app, s.Requests, forwarding, s.Teams, s.Boundary, s.Tenants);
        // The team's shared skills and workflows (devthrottle_internal#2304): the existing skill and workflow routes
        // mounted again under /teams/{teamId}, answering for the team's tenant, plus the Skills and workflows page's read.
        TeamLibraryEndpoints.Map(app, s.Skills, s.Workflows, s.Teams, s.Access, s.Boundary, s.Tenants);
        // The team's Governance tab (Teams v1): its rules and the record of every change.
        TeamGovernanceEndpoints.Map(app, s.Teams, s.Skills, s.Workflows, s.Boundary, s.Tenants);
        // The Mentor's weekly page, read (devthrottle_internal#2305). Whether the writer runs is a separate switch; the
        // page reads whatever is stored.
        TeamMentorEndpoints.Map(app, s.Teams, s.Access, s.MentorStore, s.Boundary, s.Tenants, s.TimeZoneOf, s.MentorRunning);
        // The same page for a person's own account (owner, 8 Oct 2026), from the same tables keyed by their own tenant.
        PersonalMentorEndpoints.Map(app, s.MentorStore, s.Boundary, s.Tenants, s.TimeZoneOf);
        // A dev report sent to a member of the team, and that member's Reports page (devthrottle_internal#2309); the
        // questions in those reports, waiting on the members they were sent to (devthrottle_internal#2307): the choice
        // goes to the session through the one answer delivery, the words to the author person.
        var questions = new TeamQuestions(s.Reports, s.Recipients, s.Comments, s.Teams, s.Access, s.Delivery);
        TeamReportEndpoints.Map(app, s.Reports, s.Recipients, s.Comments, s.Teams, s.Access, s.Boundary, s.Tenants, questions);
        TeamQuestionEndpoints.Map(app, questions, s.Boundary, s.Tenants);
        // A contributor's Factories and Inbox (Team implementation piece 4.1): the factories sent to them by name as customer
        // pages, through the one check (ContributorFactoryAccess), and only what was sent to them; and the Owner's share of
        // a whole factory with a contributor.
        var customerAccess = new ContributorFactoryAccess(s.Teams, s.Factories, s.FactoryContributors);
        ContributorEndpoints.Map(app, new ContributorPages(customerAccess, s.Activity, s.Reports, s.Recipients, questions,
            s.Requests, s.Teams, s.TimeZoneOf, s.UtcNow), s.Boundary, s.Tenants);
        FactoryShareEndpoints.Map(app, new FactoryShares(s.Teams, s.Factories, s.FactoryContributors, s.FactorySwitch, s.UtcNow), s.Boundary, s.Tenants);

        FileLog.Write("[TeamRoutes] mapped every route under /teams and /team-invitations, and /account/mentor");
    }
}
