using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;

namespace CcDirector.Avalonia;

public class SessionViewModel : INotifyPropertyChanged
{
    private static readonly Dictionary<ActivityState, ISolidColorBrush> ActivityBrushes = new()
    {
        { ActivityState.Starting, new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) },
        { ActivityState.Idle, new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)) },
        { ActivityState.Working, new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) },
        { ActivityState.WaitingForInput, new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)) },
        { ActivityState.WaitingForPerm, new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)) },
        { ActivityState.Exited, new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)) },
    };

    // The sidebar color strip reads the SessionStatusWingman's color (Session.StatusColor)
    // directly, so Desktop and Gateway always show the same color. The live states the
    // wingman actually emits (see SessionStatusWingman.ColorFor) are:
    //   blue   = working          red    = needs you
    //   yellow = wingman narrating purple = parked on its own background task
    //   gray   = process exited
    // Green is defined below but is NOT currently emitted by the wingman (the old
    // "greenfield/idle" state was removed); kept only so a legacy green value still maps.
    private static readonly ISolidColorBrush GreenStatusBrush   = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly ISolidColorBrush BlueStatusBrush    = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly ISolidColorBrush YellowStatusBrush  = new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08));
    private static readonly ISolidColorBrush RedStatusBrush     = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    // Purple "running in background" - the Wingman determined the session is parked on its own
    // background task, not on the user. Matches Web/directory.html --purple (#a855f7).
    private static readonly ISolidColorBrush PurpleStatusBrush  = new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly ISolidColorBrush UnknownStatusBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));

    // Light gray shown when the user has manually parked a session on hold. Deliberately
    // lighter than the exited/unknown gray (#6a6a6a) and distinct from every wingman color
    // so held sessions recede and can be ignored at a glance. OnHold is an orthogonal user
    // override (see Session.OnHold), so it sits on top of the wingman's StatusColor in the
    // list strip rather than the wingman writing it.
    private static readonly ISolidColorBrush OnHoldStatusBrush  = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    // Session-list headline color. Warm amber when the session needs you (red) so the eye is
    // drawn to actionable sessions in a multi-session list; muted otherwise.
    private static readonly ISolidColorBrush HeadlineNeedsYouBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xB8, 0x48));
    private static readonly ISolidColorBrush HeadlineMutedBrush    = new SolidColorBrush(Color.FromRgb(0x9F, 0xB0, 0xC3));

    private static readonly Dictionary<ActivityState, string> ActivityLabels = new()
    {
        { ActivityState.Starting, "Starting" },
        { ActivityState.Idle, "Idle" },
        { ActivityState.Working, "Working" },
        { ActivityState.WaitingForInput, "Your Turn" },
        { ActivityState.WaitingForPerm, "Permission" },
        { ActivityState.Exited, "Exited" },
    };

    public Session Session { get; }

    public SessionViewModel(Session session)
    {
        Session = session;
        session.OnActivityStateChanged += OnActivityStateChanged;
        session.OnVerificationStatusChanged += OnVerificationStatusChanged;
        session.OnTerminalVerificationStatusChanged += OnTerminalVerificationStatusChanged;
        session.OnStatusColorChanged += OnStatusColorChanged;
        session.OnCachedExplainChanged += OnCachedExplainChangedVm;
        session.OnHoldChanged += OnHoldChangedVm;

        if (session.PromptQueue != null)
        {
            _queueCount = session.PromptQueue.Count;
            session.PromptQueue.OnQueueChanged += OnQueueChanged;
        }
    }

    private void OnStatusColorChanged(string oldColor, string newColor, string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(StatusColorBrush));
            OnPropertyChanged(nameof(StatusReason));
            OnPropertyChanged(nameof(WingmanHeadlineBrush));
            OnPropertyChanged(nameof(WaitingDurationLabel));
            OnPropertyChanged(nameof(HasWaitingDuration));
        });
    }

    /// <summary>
    /// Phase 4d: the sidebar color strip's brush. Reads <see cref="Session.StatusColor"/>
    /// written by the SessionStatusWingman. Desktop and Gateway use the same field
    /// so the same session shows the same color in both windows.
    ///
    /// Exception: when the user has parked the session on hold (<see cref="Session.OnHold"/>),
    /// the strip shows a light gray so held sessions recede and read as "set aside" at a glance.
    /// OnHold is an orthogonal user override that sits on top of the wingman's color.
    /// </summary>
    public ISolidColorBrush StatusColorBrush
    {
        get
        {
            if (Session.OnHold) return OnHoldStatusBrush;
            return (Session.StatusColor?.ToLowerInvariant()) switch
            {
                "green"  => GreenStatusBrush,
                "blue"   => BlueStatusBrush,
                "yellow" => YellowStatusBrush,
                "red"    => RedStatusBrush,
                "purple" => PurpleStatusBrush,
                _        => UnknownStatusBrush,
            };
        }
    }

    /// <summary>True when the user has parked this session on hold. Drives the menu toggle
    /// label and the light-gray strip color.</summary>
    public bool IsOnHold => Session.OnHold;

    /// <summary>Tooltip-ready reason for the current strip color. Reflects the on-hold
    /// override when set, otherwise the wingman's reason for <see cref="Session.StatusColor"/>.</summary>
    public string StatusReason => Session.OnHold
        ? "On hold (set aside by you)"
        : Session.LastStatusReason ?? "";

    private void OnHoldChangedVm(bool onHold)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(StatusColorBrush));
            OnPropertyChanged(nameof(StatusReason));
            OnPropertyChanged(nameof(IsOnHold));
        });
    }

    private void OnCachedExplainChangedVm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(WingmanHeadline));
            OnPropertyChanged(nameof(HasWingmanHeadline));
            OnPropertyChanged(nameof(WingmanHeadlineBrush));
            // The waiting duration is proxied from CachedExplainAt, which this briefing just
            // set. When a session is already red and its first briefing lands, HasWaitingDuration
            // flips false->true here; without raising it the "waiting Xm" list label would not
            // appear until the next 15s timer tick.
            OnPropertyChanged(nameof(HasWaitingDuration));
            OnPropertyChanged(nameof(WaitingDurationLabel));
        });
    }

    /// <summary>The latest wingman headline. Shown under the session name in the list so the
    /// sidebar gives an at-a-glance "what this session is about / needs" without opening the
    /// Wingman tab. Updates when the ProactiveExplainService caches a new briefing.</summary>
    public string WingmanHeadline => Session.CachedExplainHeadline ?? "";

    public bool HasWingmanHeadline => !string.IsNullOrWhiteSpace(Session.CachedExplainHeadline);

    /// <summary>Headline color: warm amber when the session needs you (red), muted otherwise,
    /// so actionable sessions stand out in the list at a glance.</summary>
    public ISolidColorBrush WingmanHeadlineBrush =>
        string.Equals(Session.StatusColor, "red", StringComparison.OrdinalIgnoreCase)
            ? HeadlineNeedsYouBrush
            : HeadlineMutedBrush;

    /// <summary>How long this session has been waiting on you, shown in the list only when red,
    /// so you can see at a glance WHICH needs-you session is the most stale and triage it first.
    /// Proxied from the last briefing time (generated at turn-end, when the session goes red).</summary>
    public bool HasWaitingDuration =>
        string.Equals(Session.StatusColor, "red", StringComparison.OrdinalIgnoreCase)
        && Session.CachedExplainAt is not null;

    public string WaitingDurationLabel
    {
        get
        {
            if (!HasWaitingDuration) return "";
            var d = DateTime.UtcNow - Session.CachedExplainAt!.Value;
            if (d.TotalMinutes < 1) return "waiting <1m";
            if (d.TotalMinutes < 60) return $"waiting {(int)d.TotalMinutes}m";
            return $"waiting {(int)d.TotalHours}h";
        }
    }

    /// <summary>Re-raise time-derived list labels; called periodically so the waiting duration
    /// ticks up without an event.</summary>
    public void RefreshTimeLabels()
    {
        OnPropertyChanged(nameof(WaitingDurationLabel));
        OnPropertyChanged(nameof(HasWaitingDuration));
    }

    public string DisplayName => Session.CustomName
        ?? Path.GetFileName(Session.RepoPath.TrimEnd('\\', '/'));

    public string? CustomColor
    {
        get => Session.CustomColor;
        set
        {
            Session.CustomColor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomColor));
            OnPropertyChanged(nameof(CustomColorBrush));
            OnPropertyChanged(nameof(DragHandleBrush));
        }
    }

    public bool HasCustomColor => !string.IsNullOrWhiteSpace(CustomColor);

    private static readonly ISolidColorBrush DefaultDragHandleBrush = new SolidColorBrush(Color.Parse("#3C3C3C"));

    public ISolidColorBrush DragHandleBrush => HasCustomColor ? CustomColorBrush : DefaultDragHandleBrush;

    public ISolidColorBrush CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CustomColor))
                return new SolidColorBrush(Colors.Transparent);
            try
            {
                var color = Color.Parse(CustomColor);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(Colors.Transparent);
            }
        }
    }

    public string ActivityLabel =>
        ActivityLabels.TryGetValue(Session.ActivityState, out var label) ? label : "Unknown";

    public ISolidColorBrush ActivityBrush =>
        ActivityBrushes.TryGetValue(Session.ActivityState, out var brush) ? brush : Brushes.Gray;

    // Agent badge for the session list. Colored pill shown next to the session name
    // so it's visually obvious which agent CLI this session is running.
    private static readonly ISolidColorBrush ClaudeAgentBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly ISolidColorBrush PiAgentBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly ISolidColorBrush CodexAgentBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xA3, 0x7F));
    private static readonly ISolidColorBrush GeminiAgentBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0x43, 0x35));
    private static readonly ISolidColorBrush OpenCodeAgentBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));

    public string AgentLabel => Session.AgentKind switch
    {
        AgentKind.Pi => "Pi",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        AgentKind.OpenCode => "OpenCode",
        _ => "Claude Code"
    };

    public ISolidColorBrush AgentBadgeBrush => Session.AgentKind switch
    {
        AgentKind.Pi => PiAgentBrush,
        AgentKind.Codex => CodexAgentBrush,
        AgentKind.Gemini => GeminiAgentBrush,
        AgentKind.OpenCode => OpenCodeAgentBrush,
        _ => ClaudeAgentBrush
    };

    // Session-type badge (issue #211): OUTLINED chip so identity never reads as a status
    // color. Only the non-default types are shown - Implement carries no badge, keeping
    // the common case clutter-free. Cyan/magenta deliberately sit outside the state palette.
    private static readonly ISolidColorBrush DiscussTypeBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
    private static readonly ISolidColorBrush BugReportTypeBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0x48, 0x99));
    private static readonly ISolidColorBrush IssueSubmitterTypeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber (#225)
    private static readonly ISolidColorBrush QaTypeBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7));            // violet (#225)

    /// <summary>True only for non-default types - drives the badge's IsVisible.</summary>
    public bool ShowSessionTypeBadge => Session.SessionType != SessionType.Implement;

    public string SessionTypeLabel => Session.SessionType switch
    {
        SessionType.Discuss => "[D] Discuss",
        SessionType.BugReport => "[B] Bug Report",
        SessionType.IssueSubmitter => "[S] Issue Submitter",
        SessionType.QA => "[Q] QA",
        _ => ""
    };

    public ISolidColorBrush SessionTypeBadgeBrush => Session.SessionType switch
    {
        SessionType.BugReport => BugReportTypeBrush,
        SessionType.IssueSubmitter => IssueSubmitterTypeBrush,
        SessionType.QA => QaTypeBrush,
        _ => DiscussTypeBrush
    };

    public string SessionTypeTooltip => Session.SessionType switch
    {
        SessionType.Discuss => "Discussion session - talk only, no edits or commits",
        SessionType.BugReport => "Bug-report session - investigate and file an issue, never fix here",
        SessionType.IssueSubmitter => "Issue-submitter session - files GitHub issues only, never writes code",
        SessionType.QA => "QA session - verifies what was built, never fixes; reports findings",
        _ => ""
    };

    // ===== Group membership (issue #225) =====

    private static readonly ISolidColorBrush GroupAccentBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x6D, 0xA3));

    /// <summary>The group this session belongs to (issue #225), or null when solo. Set by
    /// MainWindow after construction and on reorder so the bracket/header reflow.</summary>
    public Guid? GroupId => Session.GroupId;

    /// <summary>True when this session is a group member - drives all group visuals.</summary>
    public bool IsGroupMember => Session.GroupId is not null;

    private bool _isGroupFirst;
    private bool _isGroupLast;

    /// <summary>True for the TOP member of its group: renders the group header + top bracket.</summary>
    public bool IsGroupFirst
    {
        get => _isGroupFirst;
        set { if (_isGroupFirst != value) { _isGroupFirst = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowGroupHeader)); } }
    }

    /// <summary>True for the BOTTOM member of its group: renders the bottom bracket.</summary>
    public bool IsGroupLast
    {
        get => _isGroupLast;
        set { if (_isGroupLast != value) { _isGroupLast = value; OnPropertyChanged(); } }
    }

    /// <summary>The group header ("PRODUCT GROUP ...") renders above the first member only.</summary>
    public bool ShowGroupHeader => IsGroupMember && IsGroupFirst;

    /// <summary>Header label, e.g. "PRODUCT GROUP" - on the first member only.</summary>
    public string GroupHeaderText =>
        string.IsNullOrWhiteSpace(Session.GroupName) ? "GROUP" : Session.GroupName.ToUpperInvariant() + " GROUP";

    /// <summary>The brush for the group's left accent stripe + bracket.</summary>
    public ISolidColorBrush GroupAccent => GroupAccentBrush;

    private static readonly ISolidColorBrush GroupRowTintBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x30));

    /// <summary>Subtle tint behind a group member's row (transparent for solo sessions),
    /// binding the members visually together.</summary>
    public IBrush GroupRowBackground => IsGroupMember ? GroupRowTintBrush : Brushes.Transparent;

    public string RepoPath => Session.RepoPath;

    private int _uncommittedCount;
    public int UncommittedCount
    {
        get => _uncommittedCount;
        set
        {
            if (_uncommittedCount == value) return;
            _uncommittedCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUncommittedChanges));
        }
    }

    public bool HasUncommittedChanges => _uncommittedCount > 0;

    private int _queueCount;
    public int QueueCount
    {
        get => _queueCount;
        set
        {
            if (_queueCount == value) return;
            _queueCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQueuedItems));
        }
    }

    public bool HasQueuedItems => _queueCount > 0;

    public void Rename(string? newName, string? color = null)
    {
        Session.CustomName = newName;
        if (color != null)
            Session.CustomColor = color;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CustomColor));
        OnPropertyChanged(nameof(HasCustomColor));
        OnPropertyChanged(nameof(CustomColorBrush));
        OnPropertyChanged(nameof(DragHandleBrush));
    }

    public bool IsVerified => Session.VerificationStatus == SessionVerificationStatus.Verified;

    public bool HasVerificationWarning =>
        Session.VerificationStatus is SessionVerificationStatus.FileNotFound
                                    or SessionVerificationStatus.Error
                                    or SessionVerificationStatus.ContentMismatch;

    public string VerificationStatusText => Session.VerificationStatus switch
    {
        SessionVerificationStatus.Verified => "Verified",
        SessionVerificationStatus.FileNotFound => "Session file not found",
        SessionVerificationStatus.NotLinked => "Waiting for Claude session ID...",
        SessionVerificationStatus.ContentMismatch => "Session content mismatch",
        SessionVerificationStatus.Error => "Verification error",
        _ => ""
    };

    public string? VerifiedFirstPrompt => Session.VerifiedFirstPrompt;

    public TerminalVerificationStatus TerminalVerificationStatus => Session.TerminalVerificationStatus;

    public string TerminalVerificationStatusText => Session.TerminalVerificationStatus switch
    {
        TerminalVerificationStatus.Waiting => "Waiting...",
        TerminalVerificationStatus.Potential => "Potential Match",
        TerminalVerificationStatus.Matched => "Matched",
        TerminalVerificationStatus.Failed => "Verification Failed",
        _ => ""
    };

    public bool ShowVerificationDot => Session.TerminalVerificationStatus is TerminalVerificationStatus.Waiting
                                                                           or TerminalVerificationStatus.Potential
                                                                           or TerminalVerificationStatus.Failed;

    private static readonly ISolidColorBrush VerificationWaitingBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly ISolidColorBrush VerificationPotentialBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly ISolidColorBrush VerificationFailedBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    public ISolidColorBrush VerificationDotBrush => Session.TerminalVerificationStatus switch
    {
        TerminalVerificationStatus.Waiting => VerificationWaitingBrush,
        TerminalVerificationStatus.Potential => VerificationPotentialBrush,
        TerminalVerificationStatus.Failed => VerificationFailedBrush,
        _ => VerificationWaitingBrush
    };

    private void OnQueueChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            QueueCount = Session.PromptQueue?.Count ?? 0;
        });
    }

    private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ActivityLabel));
            OnPropertyChanged(nameof(ActivityBrush));
        });
    }

    private void OnVerificationStatusChanged(SessionVerificationStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(HasVerificationWarning));
            OnPropertyChanged(nameof(VerificationStatusText));
            OnPropertyChanged(nameof(VerifiedFirstPrompt));
        });
    }

    private void OnTerminalVerificationStatusChanged(TerminalVerificationStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(TerminalVerificationStatus));
            OnPropertyChanged(nameof(TerminalVerificationStatusText));
            OnPropertyChanged(nameof(ShowVerificationDot));
            OnPropertyChanged(nameof(VerificationDotBrush));
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(HasVerificationWarning));
            OnPropertyChanged(nameof(VerificationStatusText));
        });
    }

    /// <summary>Refresh Claude metadata from sessions-index.json.</summary>
    public void RefreshClaudeMetadata()
    {
        Session.RefreshClaudeMetadata();
    }

    /// <summary>Notify UI that display properties may have changed.</summary>
    public void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CustomColor));
        OnPropertyChanged(nameof(HasCustomColor));
        OnPropertyChanged(nameof(CustomColorBrush));
        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(AgentBadgeBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
