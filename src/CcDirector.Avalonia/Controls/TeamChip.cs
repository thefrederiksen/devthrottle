using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Teams;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The team's name as a small rounded chip in the team's own colour (screen D2), drawn beside the Director's
/// name. The colour comes from <see cref="TeamColor"/>, so one team looks the same on every Director. Hidden when
/// there is no team - a Gateway without Teams shows no chip at all.
/// </summary>
public sealed class TeamChip : Border
{
    private readonly TextBlock _label = new()
    {
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public TeamChip()
    {
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(9, 1, 9, 2);
        VerticalAlignment = VerticalAlignment.Center;
        Child = _label;
        IsVisible = false;
    }

    /// <summary>The words on the chip, for tests and screen readers.</summary>
    public string Text => _label.Text ?? "";

    /// <summary>Show <paramref name="team"/>, or hide the chip when it is null.</summary>
    public void Show(DirectorTeam? team)
    {
        if (team is null)
        {
            IsVisible = false;
            _label.Text = "";
            ToolTip.SetTip(this, null);
            return;
        }

        var colour = TeamColor.For(team.TeamId);
        Background = new SolidColorBrush(Color.Parse(colour.Background));
        BorderBrush = new SolidColorBrush(Color.Parse(colour.Border));
        _label.Foreground = new SolidColorBrush(Color.Parse(colour.Foreground));
        _label.Text = team.Name;
        ToolTip.SetTip(this, team.IsPersonal
            ? "This Director works for your personal account."
            : $"This Director works for the team {team.Name}. Its sessions, skills and bill belong to that team.");
        IsVisible = true;
    }
}
