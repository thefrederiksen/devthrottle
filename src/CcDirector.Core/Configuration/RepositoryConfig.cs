using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Configuration;

public class RepositoryConfig : INotifyPropertyChanged
{
    private int _uncommittedCount;

    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime? LastUsed { get; set; }

    /// <summary>
    /// True when this entry was not chosen by the user but FOUND under one of the folders DevThrottle
    /// watches. It is not persisted - it is set when the New Session list is built.
    /// </summary>
    public bool IsDiscovered { get; set; }

    /// <summary>
    /// Whether the list should offer to remove this entry. A discovered repository cannot be removed
    /// from the list: it comes from a watched folder, so it would simply be found again on the next
    /// rebuild. Offering a Remove that instantly undoes itself is worse than not offering one - to
    /// stop seeing it, stop watching its folder.
    /// </summary>
    /// <summary>
    /// True when this entry came off the ONE repository list the Gateway serves for this machine (the
    /// one-repository-list mission, phase 6) rather than out of this Director's own files. Not persisted -
    /// it is set when the New Session list is built, like <see cref="IsDiscovered"/>.
    ///
    /// It exists to switch Remove off. Remove takes a repository out of THIS Director's local registry,
    /// and the Gateway's catalogue is not that registry: a row that has been used is never removed from
    /// the catalogue by anything a Director sends, so the row would still be there on the next read. That
    /// is the same "a button that undoes itself reads as broken" rule <see cref="IsDiscovered"/> already
    /// pays, for the same reason. Removing a repository from the one catalogue is not a thing the product
    /// can do yet, and pretending otherwise on one screen would be the lie.
    /// </summary>
    [JsonIgnore]
    public bool IsFromTheGatewayList { get; set; }

    public bool CanRemove => !IsDiscovered && !IsFromTheGatewayList;

    [JsonIgnore]
    public string LastUsedDisplay => LastUsed.HasValue
        ? FormatTimeAgo(LastUsed.Value)
        : string.Empty;

    [JsonIgnore]
    public int UncommittedCount
    {
        get => _uncommittedCount;
        set
        {
            if (_uncommittedCount == value) return;
            _uncommittedCount = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyLastUsedChanged()
    {
        OnPropertyChanged(nameof(LastUsed));
        OnPropertyChanged(nameof(LastUsedDisplay));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string FormatTimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt.ToUniversalTime();

        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }
}
