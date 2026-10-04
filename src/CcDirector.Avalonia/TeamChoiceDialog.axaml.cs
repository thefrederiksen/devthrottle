using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Screen D1 (devthrottle_internal#2311): "Which team is this Director for?", asked once, right after sign-in,
/// when the person may run sessions in at least one team. One card per team - its name, the person's role, the
/// team's size and "you pay" for the Owner - then the personal account ("Just you"). The first card starts
/// selected. A "Name this Director" box starts as "&lt;computer&gt; - &lt;team&gt;" and follows the selection until the
/// person types in it.
///
/// Closes with the <see cref="TeamAnswer"/>, or null on Cancel. It sends nothing itself: the caller enrolls.
/// </summary>
public partial class TeamChoiceDialog : Window
{
    private readonly TeamQuestion _question;
    private readonly List<RadioButton> _cards = new();
    // The name last put in the box by the dialog. While the box still holds it, the person has not made the
    // name their own, so a new selection may replace it. (Avalonia raises TextChanged after the fact, so a
    // "did the person type" flag set from that event cannot tell the dialog's own write from a keystroke.)
    private string _lastSuggestion = "";

    public TeamChoiceDialog(TeamQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (question.Choices.Count == 0)
            throw new ArgumentException("The question has no choices.", nameof(question));
        FileLog.Write($"[TeamChoiceDialog] Constructor: {question.Choices.Count} choices");
        _question = question;
        InitializeComponent();

        foreach (var choice in question.Choices)
        {
            var card = BuildCard(choice);
            _cards.Add(card);
            OptionsPanel.Children.Add(card);
        }
        _cards[0].IsChecked = true;
        SuggestName(question.Choices[0]);
    }

    // Parameterless constructor for the XAML designer.
    public TeamChoiceDialog() : this(new TeamQuestion(TeamChoices.Build(Array.Empty<HostedTeam>()), Environment.MachineName)) { }

    /// <summary>The choice whose card is checked.</summary>
    public TeamChoice SelectedChoice => (TeamChoice)_cards.Single(c => c.IsChecked == true).Tag!;

    /// <summary>What the name box holds now.</summary>
    public string DirectorName => (NameInput.Text ?? "").Trim();

    /// <summary>Select the card for <paramref name="choice"/>, as a click would. For tests and keyboard parity.</summary>
    public void Select(TeamChoice choice) => _cards.Single(c => Equals(c.Tag, choice)).IsChecked = true;

    /// <summary>
    /// The answer the Start button would give, or null with the reason shown when the name is empty. Public so a
    /// headless test drives exactly what the button does.
    /// </summary>
    public TeamAnswer? TryAnswer()
    {
        if (string.IsNullOrWhiteSpace(DirectorName))
        {
            ErrorText.Text = "Give this Director a name.";
            ErrorText.IsVisible = true;
            return null;
        }
        ErrorText.IsVisible = false;
        return new TeamAnswer(SelectedChoice, DirectorName);
    }

    private RadioButton BuildCard(TeamChoice choice)
    {
        var text = new StackPanel { Spacing = 2, Margin = new global::Avalonia.Thickness(4, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = choice.Name, Foreground = Brush("#CCCCCC"), FontSize = 14 });
        text.Children.Add(new TextBlock { Text = choice.Detail, Foreground = Brush("#888888"), FontSize = 12 });

        var card = new RadioButton
        {
            Classes = { "teamOption" },
            GroupName = "team",
            Tag = choice,
            Content = text,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        card.IsCheckedChanged += Card_IsCheckedChanged;
        return card;
    }

    private void Card_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } card) return;
        var choice = (TeamChoice)card.Tag!;
        FileLog.Write($"[TeamChoiceDialog] selected {(choice.IsPersonal ? "the personal account" : "team " + choice.TeamId)}");
        if ((NameInput.Text ?? "").Trim() == _lastSuggestion)
            SuggestName(choice);
    }

    private void SuggestName(TeamChoice choice)
    {
        _lastSuggestion = TeamChoices.SuggestDirectorName(_question.MachineName, choice);
        NameInput.Text = _lastSuggestion;
    }

    private void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[TeamChoiceDialog] BtnStart_Click");
        var answer = TryAnswer();
        if (answer is not null)
            Close(answer);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[TeamChoiceDialog] BtnCancel_Click");
        Close(null);
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
