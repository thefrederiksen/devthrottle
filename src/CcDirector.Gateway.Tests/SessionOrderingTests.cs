using System;
using System.Linq;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="SessionOrdering"/> - the shared client-side policy the Cockpit's
/// session rail uses for desktop-stable ordering (tree view) and needs-you-first triage.
/// </summary>
public sealed class SessionOrderingTests
{
    private static SessionDto S(string id, int sortOrder = 0, string color = "blue",
        bool onHold = false, DateTime createdAt = default) => new()
    {
        SessionId = id,
        SortOrder = sortOrder,
        StatusColor = color,
        OnHold = onHold,
        CreatedAt = createdAt == default ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : createdAt,
    };

    [Fact]
    public void InDesktopOrder_SortsBySortOrder_Ascending()
    {
        var sessions = new[] { S("c", 2), S("a", 0), S("b", 1) };

        var ordered = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(s => s.SessionId));
    }

    [Fact]
    public void InDesktopOrder_EqualSortOrder_FallsBackToCreatedAt()
    {
        // Every session reports SortOrder 0 (e.g. a Director predating the field): the
        // CreatedAt tie-break must give a deterministic, stable order.
        var t = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var sessions = new[]
        {
            S("late",  0, createdAt: t.AddMinutes(10)),
            S("early", 0, createdAt: t),
            S("mid",   0, createdAt: t.AddMinutes(5)),
        };

        var ordered = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "early", "mid", "late" }, ordered.Select(s => s.SessionId));
    }

    [Fact]
    public void InDesktopOrder_DoesNotMutateInput()
    {
        var sessions = new[] { S("c", 2), S("a", 0) };

        _ = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "c", "a" }, sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Classify_Red_NotHeld_IsNeedsYou()
    {
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou,
            SessionOrdering.Classify(S("x", color: "red")));
    }

    [Fact]
    public void Classify_NonRed_NotHeld_IsActive()
    {
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "blue")));
    }

    [Fact]
    public void Classify_OnHold_TakesPrecedenceOverRed()
    {
        // A parked session sinks to the bottom even when it would otherwise be "needs you".
        Assert.Equal(SessionOrdering.TriageBucket.OnHold,
            SessionOrdering.Classify(S("x", color: "red", onHold: true)));
    }

    [Fact]
    public void InBucket_FiltersToBucket_AndKeepsDesktopOrder()
    {
        var sessions = new[]
        {
            S("active1", sortOrder: 1, color: "blue"),
            S("needs1",  sortOrder: 3, color: "red"),
            S("held1",   sortOrder: 0, color: "red", onHold: true),
            S("needs0",  sortOrder: 2, color: "red"),
        };

        var needs = SessionOrdering.InBucket(sessions, SessionOrdering.TriageBucket.NeedsYou);
        var active = SessionOrdering.InBucket(sessions, SessionOrdering.TriageBucket.OnHold);

        // Only the two non-held red sessions, in SortOrder order (2 before 3).
        Assert.Equal(new[] { "needs0", "needs1" }, needs.Select(s => s.SessionId));
        // The held-red session lands in OnHold, not NeedsYou.
        Assert.Equal(new[] { "held1" }, active.Select(s => s.SessionId));
    }
}
