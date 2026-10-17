using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The desktop New Session dialog's Connection row (API mode mission #4070, phase 5): the rules, and the real window
/// driven headless at both positions of the one switch. The dialog is handed the switch's position so both can be read
/// without editing it; production reads <see cref="ConnectionModes.ApiModeOffered"/>. The window is drawn for real, so
/// the last test photographs it for the phase's proof.
/// </summary>
public class NewSessionConnectionTests
{
    /// <summary>Where the pictures are also copied when a proof run sets it. Always written and checked either way.</summary>
    private const string ProofDirectoryVariable = "CC_API_MODE_PHASE5_SHOOT";

    // ---- the rules ------------------------------------------------------------------------------------------------

    [Fact]
    public void OfferedFor_PiWhileApiModeIsOffered_IsTerminalThenApi()
    {
        // Act
        var offered = NewSessionConnection.OfferedFor(AgentKind.Pi, apiModeOffered: true);

        // Assert
        Assert.Equal(new[] { ConnectionModes.Terminal, ConnectionModes.Api }, offered);
    }

    [Theory]
    [InlineData(AgentKind.Pi, false)]
    [InlineData(AgentKind.ClaudeCode, true)]
    [InlineData(AgentKind.Codex, true)]
    public void ShowsChoice_AnAgentNotOfferedApiMode_IsFalse(AgentKind kind, bool apiModeOffered)
    {
        // Act
        var shows = NewSessionConnection.ShowsChoice(kind, apiModeOffered);

        // Assert
        Assert.False(shows);
    }

    [Fact]
    public void ShowsChoice_PiWhileApiModeIsOffered_IsTrue()
    {
        // Act
        var shows = NewSessionConnection.ShowsChoice(AgentKind.Pi, apiModeOffered: true);

        // Assert
        Assert.True(shows);
    }

    [Theory]
    [InlineData(AgentKind.Pi, true, true, SessionConnectionMode.Api)]
    [InlineData(AgentKind.Pi, false, true, SessionConnectionMode.Terminal)]
    [InlineData(AgentKind.Pi, true, false, SessionConnectionMode.Terminal)]
    [InlineData(AgentKind.ClaudeCode, true, true, SessionConnectionMode.Terminal)]
    public void ModeToStart_ChoiceAgentAndSwitch_IsApiOnlyWhenChosenAndOffered(
        AgentKind kind, bool apiChosen, bool apiModeOffered, SessionConnectionMode expected)
    {
        // Act
        var mode = NewSessionConnection.ModeToStart(kind, apiChosen, apiModeOffered);

        // Assert
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(AgentKind.Pi, true, true, true)]
    [InlineData(AgentKind.Pi, false, true, false)]
    [InlineData(AgentKind.Pi, true, false, false)]
    [InlineData(AgentKind.ClaudeCode, true, true, false)]
    public void DescriptionFor_ChoiceAgentAndSwitch_IsTheSentenceOnlyWhileApiModeIsChosenAndOffered(
        AgentKind kind, bool apiChosen, bool apiModeOffered, bool described)
    {
        // Act
        var description = NewSessionConnection.DescriptionFor(kind, "Pi", apiChosen, apiModeOffered);

        // Assert
        Assert.Equal(described ? ConnectionModes.DescriptionOf(ConnectionModes.Api, "Pi") : null, description);
    }

    // ---- the real window ------------------------------------------------------------------------------------------

    /// <summary>A temporary Director root whose configured agents are Pi and Claude Code, both enabled, Pi first.</summary>
    private sealed class TwoAgents : IDisposable
    {
        private readonly string? _previousRoot;
        private readonly string _root;

        public TwoAgents()
        {
            _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
            _root = Path.Combine(Path.GetTempPath(), "cc-director-api-mode-phase5", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
            AgentEntryStore.SaveEntries(new[]
            {
                new AgentEntry { DisplayName = "Pi", Type = AgentKind.Pi, Enabled = true },
                new AgentEntry { DisplayName = "Claude Code", Type = AgentKind.ClaudeCode, Enabled = true },
            });
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temporary directory that outlives the run is not a test failure */ }
        }
    }

    private static NewSessionDialog Open(bool apiModeOffered)
    {
        var dialog = new NewSessionDialog(registry: null, historyStore: null,
            gatewayRepositories: _ => Task.FromResult(KnownRepositoryListResult.Served(new List<KnownRepositoryDto>
            {
                new() { Name = "devthrottle", Path = "/home/soren/code/devthrottle", LastUsed = new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc) },
            })),
            apiModeOffered: apiModeOffered);
        dialog.Show();
        for (var pump = 0; pump < 10; pump++)
            Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    private static void ChooseAgent(NewSessionDialog dialog, string displayName)
    {
        var radio = dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => (r.Content as string) == displayName);
        radio.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dialog_WhileApiModeIsNotOffered_ShowsNoConnectionRowForPi()
    {
        using var agents = new TwoAgents();
        var dialog = Open(apiModeOffered: false);
        try
        {
            ChooseAgent(dialog, "Pi");

            Assert.False(dialog.GetControl<StackPanel>("ConnectionPanel").IsVisible);
            Assert.Equal(SessionConnectionMode.Terminal, dialog.SelectedConnectionMode);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Dialog_WhileApiModeIsOffered_OffersPiTerminalOrApiMode_TerminalFirst()
    {
        using var agents = new TwoAgents();
        var dialog = Open(apiModeOffered: true);
        try
        {
            ChooseAgent(dialog, "Pi");

            Assert.True(dialog.GetControl<StackPanel>("ConnectionPanel").IsVisible);
            Assert.Equal("Terminal", dialog.GetControl<RadioButton>("ConnectionTerminalRadio").Content);
            Assert.Equal("API mode", dialog.GetControl<RadioButton>("ConnectionApiRadio").Content);
            Assert.True(dialog.GetControl<RadioButton>("ConnectionTerminalRadio").IsChecked);
            Assert.Equal(SessionConnectionMode.Terminal, dialog.SelectedConnectionMode);
            Assert.False(dialog.GetControl<TextBlock>("ConnectionDescriptionText").IsVisible);

            dialog.GetControl<RadioButton>("ConnectionApiRadio").IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SessionConnectionMode.Api, dialog.SelectedConnectionMode);
            var description = dialog.GetControl<TextBlock>("ConnectionDescriptionText");
            Assert.True(description.IsVisible);
            Assert.Equal(ConnectionModes.DescriptionOf(ConnectionModes.Api, "Pi"), description.Text);

            dialog.GetControl<RadioButton>("ConnectionTerminalRadio").IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.False(description.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Dialog_ApiModeChosenForPiThenClaudeCodeChosen_HidesTheRowAndStartsInTheTerminal()
    {
        using var agents = new TwoAgents();
        var dialog = Open(apiModeOffered: true);
        try
        {
            ChooseAgent(dialog, "Pi");
            dialog.GetControl<RadioButton>("ConnectionApiRadio").IsChecked = true;

            ChooseAgent(dialog, "Claude Code");

            Assert.False(dialog.GetControl<StackPanel>("ConnectionPanel").IsVisible);
            Assert.False(dialog.GetControl<TextBlock>("ConnectionDescriptionText").IsVisible);
            Assert.True(dialog.GetControl<RadioButton>("ConnectionTerminalRadio").IsChecked);
            Assert.Equal(SessionConnectionMode.Terminal, dialog.SelectedConnectionMode);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Dialog_PiInApiMode_IsPhotographedForTheProof()
    {
        using var agents = new TwoAgents();
        var dialog = Open(apiModeOffered: true);
        try
        {
            ChooseAgent(dialog, "Pi");
            var hidden = Compose(dialog);
            dialog.GetControl<RadioButton>("ConnectionApiRadio").IsChecked = true;

            var picture = Compose(dialog);

            // The camera is live: choosing API mode changed the picture.
            Assert.False(hidden.SequenceEqual(picture), "the picture did not change when API mode was chosen");
            Save(picture, "desktop-new-session-pi-api-mode.png");
        }
        finally
        {
            dialog.Close();
        }
    }

    private static byte[] Compose(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var memory = new MemoryStream();
        frame!.Save(memory);
        var bytes = memory.ToArray();
        Assert.True(bytes.Length > 5000, "the picture is too small to be a screen");
        return bytes;
    }

    private static void Save(byte[] picture, string fileName)
    {
        var taken = Path.Combine(Path.GetTempPath(), "cc-director-api-mode-phase5-shots");
        Directory.CreateDirectory(taken);
        var file = Path.Combine(taken, fileName);
        File.WriteAllBytes(file, picture);
        var proofDirectory = Environment.GetEnvironmentVariable(ProofDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(proofDirectory))
        {
            Directory.CreateDirectory(proofDirectory);
            File.Copy(file, Path.Combine(proofDirectory, fileName), overwrite: true);
        }
    }
}
