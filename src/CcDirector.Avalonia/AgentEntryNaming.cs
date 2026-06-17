using System;
using System.Collections.Generic;
using System.Linq;

namespace CcDirector.Avalonia;

/// <summary>
/// Pure, UI-free naming logic for the Settings &gt; Agents Add/Edit editor (issue #494).
/// Kept out of the dialog code-behind so the auto-name-from-type rule and the
/// "don't clobber a customized name" rule are unit-testable without an Avalonia window.
/// </summary>
public static class AgentEntryNaming
{
    /// <summary>
    /// Build the auto display name for a freshly-picked type. Returns the type's base label
    /// when no entry already uses it (case-insensitive), otherwise disambiguates to
    /// "<paramref name="baseLabel"/> (2)", "(3)", ... using the lowest free index.
    /// </summary>
    /// <param name="baseLabel">The type's display label, e.g. "Codex" or "Claude Code".</param>
    /// <param name="existingNames">Display names already in the list (the new entry is not among them).</param>
    public static string AutoNameForType(string baseLabel, IEnumerable<string> existingNames)
    {
        if (baseLabel is null) throw new ArgumentNullException(nameof(baseLabel));
        if (existingNames is null) throw new ArgumentNullException(nameof(existingNames));

        var taken = new HashSet<string>(
            existingNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseLabel))
            return baseLabel;

        // Find the lowest N >= 2 whose "<base> (N)" form is free.
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseLabel} ({n})";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Decide whether selecting a type should overwrite the Display name field. We only auto-fill
    /// when the user has NOT typed a custom name: the field is blank, or it still holds an
    /// auto-generated name (the previous type's base label, with or without an " (N)" suffix).
    /// This is what keeps a customized name (AC9) from being clobbered on a later type change,
    /// while a brand-new form (blank name) or an untouched auto name still tracks the type.
    /// </summary>
    /// <param name="currentName">What is currently in the Display name box.</param>
    /// <param name="autoNameLabels">All type base labels (so we can recognize any prior auto name).</param>
    public static bool ShouldAutoFillName(string? currentName, IEnumerable<string> autoNameLabels)
    {
        if (autoNameLabels is null) throw new ArgumentNullException(nameof(autoNameLabels));

        var name = currentName?.Trim() ?? "";
        if (name.Length == 0)
            return true;

        foreach (var label in autoNameLabels)
        {
            if (string.IsNullOrWhiteSpace(label))
                continue;
            if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase))
                return true;
            if (IsNumberedVariant(name, label))
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="name"/> is "<paramref name="label"/> (N)" for an integer N &gt;= 2.</summary>
    private static bool IsNumberedVariant(string name, string label)
    {
        var prefix = label + " (";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(")", StringComparison.Ordinal))
            return false;

        var inner = name.Substring(prefix.Length, name.Length - prefix.Length - 1);
        return int.TryParse(inner, out var n) && n >= 2;
    }
}
