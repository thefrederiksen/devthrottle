using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class ToolsStep : UserControl
{
    private readonly HashSet<string> _enabledGroups;
    private readonly Action<List<string>> _onGroupsChanged;
    private readonly Dictionary<string, (TextBlock Checkbox, Border Card)> _groupRows = new();

    public ToolsStep(List<string> initialGroups, Action<List<string>> onGroupsChanged, bool isUpdate)
    {
        InitializeComponent();
        _enabledGroups = new HashSet<string>(initialGroups);
        _onGroupsChanged = onGroupsChanged;

        // Always ensure required groups are enabled
        foreach (var g in ToolGroupRegistry.AllGroups.Where(g => g.IsRequired))
            _enabledGroups.Add(g.Name);

        BuildGroupRows();
        UpdateAllVisuals();

        if (isUpdate)
        {
            TitleText.Text = "Update Tool Groups";
            DescriptionText.Text = "Your saved selections are shown below. Change groups if needed, then click Next.";
        }

        SetupLog.Write($"[ToolsStep] Created: groups={_enabledGroups.Count}, isUpdate={isUpdate}");
    }

    public List<string> GetEnabledGroups()
    {
        return _enabledGroups.ToList();
    }

    private void BuildGroupRows()
    {
        SetupLog.Write("[ToolsStep] BuildGroupRows: creating UI rows");

        var dimBrush = (SolidColorBrush)FindResource("DimText");

        foreach (var group in ToolGroupRegistry.AllGroups)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x2E)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = group.IsRequired ? Cursors.Arrow : Cursors.Hand,
            };

            var outerPanel = new StackPanel();

            // Top row: checkbox + name + tool preview + optional LOCKED badge
            var topRow = new DockPanel();

            var checkbox = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Width = 24,
            };

            var nameText = new TextBlock
            {
                Text = group.Name,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var toolPreview = new TextBlock
            {
                Text = ToolGroupRegistry.GetToolPreview(group),
                Foreground = dimBrush,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };

            DockPanel.SetDock(checkbox, Dock.Left);
            topRow.Children.Add(checkbox);

            if (group.IsRequired)
            {
                var lockedBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "LOCKED",
                        Foreground = dimBrush,
                        FontSize = 10,
                    }
                };
                DockPanel.SetDock(lockedBadge, Dock.Right);
                topRow.Children.Add(lockedBadge);
            }

            topRow.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { nameText, toolPreview }
            });

            // Description row
            var descText = new TextBlock
            {
                Text = group.Description,
                Foreground = dimBrush,
                FontSize = 11,
                Margin = new Thickness(34, 2, 0, 0),
            };

            outerPanel.Children.Add(topRow);
            outerPanel.Children.Add(descText);
            card.Child = outerPanel;

            if (!group.IsRequired)
            {
                var groupName = group.Name;
                card.MouseLeftButtonDown += (_, _) => ToggleGroup(groupName);
            }

            GroupList.Children.Add(card);
            _groupRows[group.Name] = (checkbox, card);
        }
    }

    private void ToggleGroup(string groupName)
    {
        SetupLog.Write($"[ToolsStep] ToggleGroup: {groupName}");

        if (_enabledGroups.Contains(groupName))
            _enabledGroups.Remove(groupName);
        else
            _enabledGroups.Add(groupName);

        _onGroupsChanged(_enabledGroups.ToList());
        UpdateAllVisuals();
    }

    private void UpdateAllVisuals()
    {
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var inactiveBrush = (SolidColorBrush)FindResource("StepInactive");
        var dimBrush = (SolidColorBrush)FindResource("DimText");

        foreach (var group in ToolGroupRegistry.AllGroups)
        {
            if (!_groupRows.TryGetValue(group.Name, out var row))
                continue;

            var isEnabled = _enabledGroups.Contains(group.Name);

            row.Checkbox.Text = isEnabled ? "[x]" : "[ ]";
            row.Checkbox.Foreground = isEnabled ? accentBrush : dimBrush;
            row.Card.BorderBrush = isEnabled ? accentBrush : inactiveBrush;
        }

        // Update tool count
        var count = ToolGroupRegistry.GetToolCount(_enabledGroups);
        ToolCountText.Text = $"{count} tools selected";

        // Update preset button highlights
        UpdatePresetHighlights();
    }

    private void UpdatePresetHighlights()
    {
        var allGroupNames = new HashSet<string>(ToolGroupRegistry.GetPresetGroupNames("All"));
        var allEnabled = _enabledGroups.SetEquals(allGroupNames);
        AllPreset.Content = allEnabled ? "Deselect All" : "Select All";
    }

    private void AllPreset_Click(object sender, RoutedEventArgs e)
    {
        var allGroupNames = new HashSet<string>(ToolGroupRegistry.GetPresetGroupNames("All"));
        var allEnabled = _enabledGroups.SetEquals(allGroupNames);

        _enabledGroups.Clear();

        if (allEnabled)
        {
            // Was fully enabled -- deselect everything except required groups.
            foreach (var g in ToolGroupRegistry.AllGroups.Where(g => g.IsRequired))
                _enabledGroups.Add(g.Name);
            SetupLog.Write("[ToolsStep] AllPreset_Click: deselected all (required groups retained)");
        }
        else
        {
            // Was partial -- select everything.
            foreach (var name in allGroupNames)
                _enabledGroups.Add(name);
            SetupLog.Write("[ToolsStep] AllPreset_Click: selected all groups");
        }

        _onGroupsChanged(_enabledGroups.ToList());
        UpdateAllVisuals();
    }
}
