using System.ComponentModel;

namespace CcDirectorSetup.Models;

public class PrerequisiteInfo : INotifyPropertyChanged
{
    private string _status = "Checking...";
    private string _version = "";
    private bool _isFound;

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsRequired { get; init; }
    public required string InstallUrl { get; init; }

    /// <summary>True when Setup can install this prerequisite itself (via winget).</summary>
    public bool CanAutoInstall { get; init; }

    /// <summary>The winget package id used by <see cref="Services.RuntimeInstaller"/> when <see cref="CanAutoInstall"/> is true.</summary>
    public string? WingetId { get; init; }

    /// <summary>Shows the "Install automatically" action only when auto-installable and not yet present.</summary>
    public bool ShowAutoInstall => CanAutoInstall && !IsFound;

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(StatusColor)); }
    }

    public string Version
    {
        get => _version;
        set { _version = value; OnPropertyChanged(nameof(Version)); }
    }

    public bool IsFound
    {
        get => _isFound;
        set { _isFound = value; OnPropertyChanged(nameof(IsFound)); OnPropertyChanged(nameof(StatusColor)); OnPropertyChanged(nameof(ShowAutoInstall)); }
    }

    public string StatusColor => IsFound ? "#22C55E" : (IsRequired ? "#CC4444" : "#888888");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
