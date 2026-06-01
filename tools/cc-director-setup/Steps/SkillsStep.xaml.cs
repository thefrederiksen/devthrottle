using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class SkillsStep : UserControl
{
    public SkillsStep(bool isUpdate)
    {
        InitializeComponent();
        BuildSkillRows();

        if (isUpdate)
        {
            TitleText.Text = "Update Skills";
            DescriptionText.Text = "The following skills will be updated for Claude Code.";
        }

        SetupLog.Write($"[SkillsStep] Created: isUpdate={isUpdate}, skills={SkillInstaller.SkillNames.Length}");
    }

    private void BuildSkillRows()
    {
        SetupLog.Write("[SkillsStep] BuildSkillRows: creating UI rows");

        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var dimBrush = (SolidColorBrush)FindResource("DimText");

        foreach (var skillName in SkillInstaller.SkillNames)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x2E)),
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
            };

            var topRow = new DockPanel();

            var checkbox = new TextBlock
            {
                Text = "[x]",
                Foreground = accentBrush,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Width = 24,
            };

            var nameText = new TextBlock
            {
                Text = skillName,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };

            DockPanel.SetDock(checkbox, Dock.Left);
            topRow.Children.Add(checkbox);
            topRow.Children.Add(nameText);

            card.Child = topRow;
            SkillList.Children.Add(card);
        }

        SkillCountText.Text = $"{SkillInstaller.SkillNames.Length} skills will be installed";
    }
}
