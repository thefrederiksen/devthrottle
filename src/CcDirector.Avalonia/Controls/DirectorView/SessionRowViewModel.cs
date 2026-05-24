using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Controls.DirectorView;

/// <summary>One row in the Director session list.</summary>
public sealed class SessionRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionDto Dto { get; private set; }

    public SessionRowViewModel(SessionDto dto)
    {
        Dto = dto;
    }

    /// <summary>Refresh this row from a newer DTO, raising property-changed only for things that moved.</summary>
    public void Update(SessionDto dto)
    {
        var stateChanged = !string.Equals(Dto.ActivityState, dto.ActivityState, StringComparison.Ordinal);
        var directorChanged = !string.Equals(Dto.DirectorId, dto.DirectorId, StringComparison.Ordinal);
        Dto = dto;
        if (stateChanged)
        {
            OnPropertyChanged(nameof(ActivityState));
            OnPropertyChanged(nameof(StateBrush));
        }
        if (directorChanged)
        {
            OnPropertyChanged(nameof(DirectorShort));
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string SessionId => Dto.SessionId;
    public string DirectorId => Dto.DirectorId;
    public string Agent => Dto.Agent;
    public string ActivityState => Dto.ActivityState;
    public string RepoLabel => string.IsNullOrEmpty(Dto.RepoPath)
        ? Dto.SessionId.Substring(0, Math.Min(8, Dto.SessionId.Length))
        : System.IO.Path.GetFileName(Dto.RepoPath.TrimEnd('\\', '/'));
    public string DirectorShort => string.IsNullOrEmpty(Dto.DirectorId)
        ? ""
        : "dir:" + Dto.DirectorId.Substring(0, Math.Min(8, Dto.DirectorId.Length));
    public string ElapsedMsLabel => "";

    public IBrush StateBrush => Dto.ActivityState switch
    {
        "Idle" => new SolidColorBrush(Color.Parse("#4EC9B0")),
        "Working" => new SolidColorBrush(Color.Parse("#569CD6")),
        "WaitingForInput" => new SolidColorBrush(Color.Parse("#DCDCAA")),
        "WaitingForPerm" => new SolidColorBrush(Color.Parse("#CE9178")),
        "Starting" => new SolidColorBrush(Color.Parse("#9CDCFE")),
        "Exited" or "Failed" => new SolidColorBrush(Color.Parse("#F44747")),
        _ => new SolidColorBrush(Color.Parse("#888888")),
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
