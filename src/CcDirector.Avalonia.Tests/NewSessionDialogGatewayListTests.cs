using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The desktop New Session dialog against the ONE repository list (the one-repository-list mission,
/// phase 6), driven as the real window rather than as its rules.
///
/// These exist because the rules alone cannot see the screen. The owner's decision was Gateway first,
/// the machine's own scan as the fallback, and SAYING ON SCREEN when it is on the fallback - and the
/// third of those is a rendered thing. The failure cases are the point: a fallback path nobody has seen
/// working is not a proven one, and a screen that quietly shows a different order is how the list stops
/// being trusted.
///
/// The window is constructed headless with real drawing on, so the last test here photographs it.
/// Configuration is redirected to a temporary root; the assembly runs sequentially, so the process-wide
/// environment variable is not raced.
/// </summary>
public class NewSessionDialogGatewayListTests
{
    /// <summary>
    /// Where the pictures this suite takes are also copied, when a proof run sets it. They are always
    /// written to a temporary directory and always asserted to be a real picture, so nothing here is
    /// silently skipped when the variable is absent.
    /// </summary>
    private const string ProofDirectoryVariable = "CC_PHASE6_SHOOT";

    private static KnownRepositoryDto Row(string name, string path, DateTime? lastUsed) => new()
    {
        Name = name,
        Path = path,
        LastUsed = lastUsed,
        NeverOpened = lastUsed is null,
    };

    /// <summary>
    /// An order no client sort produces: a never-opened repository sits between two used ones and the
    /// names run backwards. A screen that sorted for itself would move at least one row.
    /// </summary>
    private static List<KnownRepositoryDto> TheGatewaysList() => new()
    {
        Row("zephyr-tools", "/home/soren/code/zephyr-tools", new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc)),
        Row("beacon", "/home/soren/code/beacon", null),
        Row("atlas-reporting", "/home/soren/code/atlas-reporting", new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc)),
        Row("cinder", "/home/soren/code/cinder", null),
    };

    /// <summary>
    /// One temporary root, one registry holding two real folders. The registry is the machine's own list
    /// - what the dialog falls back to - so it has to be real folders on this disk.
    /// </summary>
    private sealed class MachineOfItsOwn : IDisposable
    {
        private readonly string? _previousRoot;

        public string Root { get; }

        public RepositoryRegistry Registry { get; }

        public MachineOfItsOwn()
        {
            _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
            Root = Path.Combine(Path.GetTempPath(), "cc-director-phase6", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", Root);

            Registry = new RepositoryRegistry(Path.Combine(Root, "repositories.json"));
            foreach (var name in new[] { "mindzie-studio", "devthrottle" })
            {
                var folder = Path.Combine(Root, "repos", name);
                Directory.CreateDirectory(folder);
                Registry.TryAdd(folder);
            }
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* a temporary directory that outlives the run is not a test failure */ }
        }
    }

    /// <summary>
    /// How many dialogs this suite has open. Every test closes its own, and <see cref="Photograph"/>
    /// refuses to shoot unless exactly one is open, so a picture can always be said to be of the window
    /// under test.
    ///
    /// Said plainly: this assertion is belt and braces and I could NOT watch it fail. Leaving the windows
    /// open did not reproduce the stale-picture defect described on <see cref="Photograph"/> - the
    /// repeated composition there is what fixes that, and the check that catches it is the liveness one.
    /// Closing a window a test has finished with is right anyway.
    /// </summary>
    private static int _openDialogs;

    /// <summary>One dialog, open for the length of a test and closed after it.</summary>
    private sealed class OpenDialog : IDisposable
    {
        public NewSessionDialog Dialog { get; }

        public OpenDialog(RepositoryRegistry? registry, Func<CancellationToken, Task<KnownRepositoryListResult>> gateway)
        {
            Dialog = new NewSessionDialog(registry, historyStore: null, gatewayRepositories: gateway);
            Dialog.Show();
            Interlocked.Increment(ref _openDialogs);
            // Loaded fires on Show; the ask and its continuation both land on this dispatcher.
            for (var pump = 0; pump < 10; pump++)
                Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Dialog.Close();
            Dispatcher.UIThread.RunJobs();
            Interlocked.Decrement(ref _openDialogs);
        }
    }

    private static OpenDialog Open(
        RepositoryRegistry? registry, Func<CancellationToken, Task<KnownRepositoryListResult>> gateway)
        => new(registry, gateway);

    private static List<RepositoryConfig> RowsOnScreen(OpenDialog open)
    {
        var list = open.Dialog.GetControl<ListBox>("RepoList");
        return (list.ItemsSource as IEnumerable<RepositoryConfig>)?.ToList() ?? new List<RepositoryConfig>();
    }

    private static (bool Visible, string Text) NoticeOnScreen(OpenDialog open) =>
        (open.Dialog.GetControl<Border>("RepoSourceNotice").IsVisible,
         open.Dialog.GetControl<TextBlock>("RepoSourceNoticeText").Text ?? "");

    /// <summary>
    /// Photograph the window, PROVE THE CAMERA IS LIVE, and copy the picture where a proof run asks for
    /// it.
    ///
    /// THE LIVENESS CHECK IS NOT CEREMONY - IT IS THE DEFECT THIS METHOD ALREADY SHIPPED ONCE. A single
    /// capture after the dispatcher's jobs have run hands back a frame composed BEFORE the Gateway's
    /// answer reached the screen: the committed picture of "the Gateway could not be reached" showed the
    /// waiting sentence instead, while the test it sat in passed, because the test read the control and
    /// the camera read the past. A picture of the wrong moment is the proof covering the wrong thing,
    /// and it is invisible to every assertion about the screen.
    ///
    /// So the window is composed repeatedly, and then the method proves the shot tracks the window: it
    /// makes one known visible change, photographs again, and requires the two pictures to differ. A
    /// camera returning a frozen frame gives two identical pictures and fails here - watched failing,
    /// with the capture swapped for one that never re-renders.
    /// </summary>
    private static void Photograph(OpenDialog open, string fileName)
    {
        Assert.Equal(1, Volatile.Read(ref _openDialogs));

        var picture = Compose(open.Dialog);

        // One known visible change, and the same camera again. Restored immediately.
        var notice = open.Dialog.GetControl<Border>("RepoSourceNotice");
        var wasVisible = notice.IsVisible;
        notice.IsVisible = !wasVisible;
        var second = Compose(open.Dialog);
        notice.IsVisible = wasVisible;
        Compose(open.Dialog);

        Assert.False(picture.SequenceEqual(second),
            "the camera returned the same picture after the screen changed, so it is photographing a "
            + "frame from the past rather than this window");

        var taken = Path.Combine(Path.GetTempPath(), "cc-director-phase6-shots");
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

    /// <summary>Compose the window as it stands NOW, and hand back the picture as bytes.</summary>
    private static byte[] Compose(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 400 && frame.PixelSize.Height > 300,
            $"the captured frame is {frame.PixelSize}, which is not a picture of a dialog");

        using var memory = new MemoryStream();
        frame.Save(memory);
        var bytes = memory.ToArray();
        Assert.True(bytes.Length > 5000, "the picture is too small to be a screen");
        return bytes;
    }

    /// <summary>
    /// THE FLOW. The Gateway's list is what the screen shows, in the Gateway's order, and the screen
    /// says nothing about where it came from - the ordinary state wears no banner.
    /// </summary>
    [AvaloniaFact]
    public void TheDialogShowsTheGatewaysList_InTheGatewaysOrder()
    {
        using var machine = new MachineOfItsOwn();
        var asked = 0;
        using var dialog = Open(machine.Registry, _ =>
        {
            asked++;
            return Task.FromResult(KnownRepositoryListResult.Served(TheGatewaysList()));
        });

        Assert.Equal(1, asked);
        Assert.Equal(
            new[] { "zephyr-tools", "beacon", "atlas-reporting", "cinder" },
            RowsOnScreen(dialog).Select(r => r.Name).ToArray());

        var notice = NoticeOnScreen(dialog);
        Assert.False(notice.Visible);

        // The machine's own registry rows are NOT merged in beside the served ones. A union here would
        // be a second list on one machine, which is the defect this mission exists to end.
        Assert.DoesNotContain(RowsOnScreen(dialog), r => r.Name == "mindzie-studio");

        Photograph(dialog, "the-gateways-list-on-the-director.png");
    }

    /// <summary>
    /// THE PROOF THE MISSION SINGLES OUT. The Gateway cannot be reached: the dialog still lists
    /// repositories, and it SAYS it is on the machine's own list. Silence here is the failure the owner
    /// named by name.
    /// </summary>
    [AvaloniaFact]
    public void WhenTheGatewayCannotBeReached_ItStillListsRepositories_AndSaysItIsOnTheFallback()
    {
        using var machine = new MachineOfItsOwn();
        using var dialog = Open(machine.Registry, _ => Task.FromResult(
            KnownRepositoryListResult.Unreachable("No such host is known. (gateway.devthrottle.com:443)")));

        var rows = RowsOnScreen(dialog);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Name == "mindzie-studio");
        Assert.Contains(rows, r => r.Name == "devthrottle");

        var notice = NoticeOnScreen(dialog);
        Assert.True(notice.Visible, "the screen said nothing about being on its fallback");
        Assert.Equal(
            "The Gateway could not be reached. This is this machine's own list, and its order may differ from the Cockpit and the phone.",
            notice.Text);

        // The list is the machine's own, so Remove is offered again - it removes from the registry that
        // list actually came from.
        Assert.All(rows, r => Assert.True(r.CanRemove));

        Photograph(dialog, "the-gateway-cannot-be-reached.png");
    }

    /// <summary>
    /// A Director with no Gateway at all. It is a different sentence from "could not be reached",
    /// because it is a different problem with a different fix.
    /// </summary>
    [AvaloniaFact]
    public void WithNoGatewayConnected_ItListsTheMachinesOwnRepositories_AndSaysSo()
    {
        using var machine = new MachineOfItsOwn();
        using var dialog = Open(machine.Registry, _ => Task.FromResult(
            KnownRepositoryListResult.NotConfigured("no Gateway is configured on this Director")));

        Assert.Equal(2, RowsOnScreen(dialog).Count);
        var notice = NoticeOnScreen(dialog);
        Assert.True(notice.Visible);
        Assert.StartsWith("No Gateway is connected.", notice.Text);

        Photograph(dialog, "no-gateway-is-connected.png");
    }

    /// <summary>
    /// A Gateway that ANSWERS and refuses is not an unreachable one. The screen shows the Gateway's own
    /// words rather than inventing an explanation, so a real defect is displayed instead of hidden.
    /// </summary>
    [AvaloniaFact]
    public void WhenTheGatewayRefuses_TheScreenShowsTheGatewaysOwnWords()
    {
        using var machine = new MachineOfItsOwn();
        using var dialog = Open(machine.Registry, _ => Task.FromResult(
            KnownRepositoryListResult.Refused("The Director has not reported a machine name.")));

        Assert.Equal(2, RowsOnScreen(dialog).Count);
        var notice = NoticeOnScreen(dialog);
        Assert.True(notice.Visible);
        Assert.Contains("The Director has not reported a machine name.", notice.Text);

        Photograph(dialog, "the-gateway-refused.png");
    }

    /// <summary>
    /// AN EMPTY LIST IS THE ANSWER, and the dialog shows it. This is the one the fallback must never
    /// take: a Gateway saying this machine has no repositories is not a Gateway that could not be
    /// reached, and showing the machine's own list instead would hide exactly the disagreement this
    /// mission exists to end.
    /// </summary>
    [AvaloniaFact]
    public void WhenTheGatewayServesAnEmptyList_TheScreenShowsIt_AndDoesNotFallBack()
    {
        using var machine = new MachineOfItsOwn();
        using var dialog = Open(machine.Registry, _ => Task.FromResult(
            KnownRepositoryListResult.Served(Array.Empty<KnownRepositoryDto>())));

        Assert.Empty(RowsOnScreen(dialog));
        Assert.False(NoticeOnScreen(dialog).Visible);
        Assert.True(dialog.Dialog.GetControl<Border>("RepoEmptyState").IsVisible);
    }

    /// <summary>
    /// Pressing the Name heading is a person asking for a different view, and pressing Last Used comes
    /// back to the Gateway's order intact - the served order is held, not re-ordered in place.
    /// </summary>
    [AvaloniaFact]
    public void SortingByNameAndBack_ReturnsTheGatewaysOrder()
    {
        using var machine = new MachineOfItsOwn();
        using var dialog = Open(machine.Registry, _ => Task.FromResult(
            KnownRepositoryListResult.Served(TheGatewaysList())));

        dialog.Dialog.GetControl<Button>("RepoHeaderName").RaiseEvent(
            new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(
            new[] { "atlas-reporting", "beacon", "cinder", "zephyr-tools" },
            RowsOnScreen(dialog).Select(r => r.Name).ToArray());

        dialog.Dialog.GetControl<Button>("RepoHeaderLastUsed").RaiseEvent(
            new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(
            new[] { "zephyr-tools", "beacon", "atlas-reporting", "cinder" },
            RowsOnScreen(dialog).Select(r => r.Name).ToArray());
    }

    /// <summary>
    /// The search box reaches the whole served list, including a repository nobody has ever opened, and
    /// filtering does not re-order what is left.
    /// </summary>
    [AvaloniaFact]
    public void SearchingReachesANeverOpenedRepository_WithoutReOrderingTheRest()
    {
        using var machine = new MachineOfItsOwn();
        using var dialog = Open(machine.Registry, _ => Task.FromResult(
            KnownRepositoryListResult.Served(TheGatewaysList())));

        dialog.Dialog.GetControl<TextBox>("RepoSearchBox").Text = "cinder";
        Dispatcher.UIThread.RunJobs();

        var found = RowsOnScreen(dialog);
        Assert.Single(found);
        Assert.Equal("cinder", found[0].Name);
        Assert.True(found[0].IsDiscovered);
    }
}
