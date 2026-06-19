using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using CcDirector.Core.Tools;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Shared brushes for tool status so the list chip and the detail chip stay in sync.
/// ASCII-only labels per the project's no-Unicode rule.
/// </summary>
internal static class ToolStatusVisuals
{
    public static (string Label, IBrush Brush) For(ToolStatus status) => status switch
    {
        ToolStatus.Pass => ("PASS", Brush("#22C55E")),
        ToolStatus.Fail => ("FAIL", Brush("#DC2626")),
        // "NOT BUILT" now means genuinely unavailable: on neither the bundled bin dir nor the user's
        // PATH (issue #448). A tool that resolves on PATH is available and is never labelled this.
        ToolStatus.NotBuilt => ("UNAVAILABLE", Brush("#6B7280")),
        _ => ("untested", Brush("#888888")),
    };

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// <summary>
/// Backing item for the left tool list. Holds the immutable <see cref="ToolDescriptor"/> plus the
/// mutable status that changes as tests run.
/// </summary>
public sealed class ToolItemViewModel : INotifyPropertyChanged
{
    private ToolStatus _status;

    internal ToolItemViewModel(ToolDescriptor descriptor)
    {
        Descriptor = descriptor;
        // Availability (PATH or bundled bin), not bin-only IsBuilt, decides the initial chip: a
        // PATH-available tool starts "untested" (its checks will run), never "unavailable" (issue #448).
        _status = descriptor.IsAvailable ? ToolStatus.Untested : ToolStatus.NotBuilt;
    }

    internal ToolDescriptor Descriptor { get; }

    public string Name => Descriptor.Name;
    public string Category => Descriptor.Category;
    public bool IsBuilt => Descriptor.IsBuilt;

    /// <summary>The user-facing availability signal: runnable from the bundled bin dir OR the user's PATH.</summary>
    public bool IsAvailable => Descriptor.IsAvailable;

    internal ToolStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
        }
    }

    public string StatusText => ToolStatusVisuals.For(_status).Label;
    public IBrush StatusBrush => ToolStatusVisuals.For(_status).Brush;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Backing item for a single check row in the Tests tab.</summary>
public sealed class TestResultViewModel
{
    internal TestResultViewModel(ToolTestResult result)
    {
        Label = result.Label;
        Passed = result.Passed;
        ResultText = result.Passed ? "[PASS]" : "[FAIL]";
        ResultBrush = ToolStatusVisuals.Brush(result.Passed ? "#22C55E" : "#DC2626");
        Detail = $"{result.DurationMs} ms  -  {result.Message}";
    }

    public string Label { get; }
    public bool Passed { get; }
    public string ResultText { get; }
    public IBrush ResultBrush { get; }
    public string Detail { get; }
}

/// <summary>Backing item for a skill link row in the Skills tab.</summary>
public sealed class SkillLinkViewModel
{
    internal SkillLinkViewModel(SkillToolLink link)
    {
        SkillName = "/" + link.SkillName;
        Relation = link.Relation == SkillLinkRelation.Drives ? "drives" : "uses";
        Source = link.Source == SkillLinkSource.Declared ? "declared" : "discovered";
        SourceBrush = ToolStatusVisuals.Brush(link.Source == SkillLinkSource.Declared ? "#E0883C" : "#888888");
    }

    public string SkillName { get; }
    public string Relation { get; }
    public string Source { get; }
    public IBrush SourceBrush { get; }
}
