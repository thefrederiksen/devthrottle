using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Terminal.Avalonia;

namespace CcDirector.DeliveryQualification;

/// <summary>The application the headless desktop terminal runs in; nothing but the toolkit itself.</summary>
public sealed class DesktopHostApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DesktopHostApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>
/// THE DESKTOP TERMINAL, HEADLESS (issue #4378). The shipped <see cref="TerminalControl"/>, in a window no one sees,
/// attached to a real session: typing goes through its own text-input and key-down handlers, and a paste through the
/// same code Ctrl+V runs once the clipboard is read. So the bytes the agent receives are the bytes the owner's desktop
/// sends - nothing here re-implements the control.
/// </summary>
public sealed class DesktopHost : IDisposable
{
    /// <summary>The gap between typed characters: a fast typist, rather than every character in one burst.</summary>
    public static readonly TimeSpan KeyGap = TimeSpan.FromMilliseconds(8);

    private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(DesktopHostApp));
    private TerminalControl? _terminal;
    private Window? _window;

    /// <summary>Show a terminal attached to <paramref name="session"/>, replacing any earlier one.</summary>
    public Task AttachAsync(Session session) => _ui.Dispatch(() =>
    {
        _window?.Close();
        _terminal = new TerminalControl();
        _window = new Window { Width = 1200, Height = 800, Content = _terminal };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        if (_terminal.HarnessCols < 10 || _terminal.HarnessRows < 3) _terminal.HarnessSetGrid(120, 40);
        _terminal.Attach(session);
        _terminal.Focus();
        Dispatcher.UIThread.RunJobs();
        return true;
    }, CancellationToken.None);

    /// <summary>Type <paramref name="text"/> one character at a time, a line break as Shift+Enter (the way a person makes
    /// a new line in a composer), a tab as the Tab key.</summary>
    public async Task TypeAsync(string text)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            await _ui.Dispatch(() =>
            {
                var t = _terminal ?? throw new InvalidOperationException("No terminal is attached.");
                switch (element)
                {
                    case "\n": t.HarnessKeyDown(Key.Enter, KeyModifiers.Shift); break;
                    case "\t": t.HarnessKeyDown(Key.Tab); break;
                    default: t.HarnessTextInput(element); break;
                }
                return true;
            }, CancellationToken.None);
            await Task.Delay(KeyGap);
        }
    }

    /// <summary>Paste <paramref name="text"/> as Ctrl+V would, and return when its last chunk has been written.</summary>
    public async Task PasteAsync(string text)
    {
        var paste = await _ui.Dispatch(() => _terminal!.HarnessPasteAsync(text), CancellationToken.None);
        await paste;
    }

    /// <summary>Press one terminal key through the shipped desktop control.</summary>
    public Task KeyAsync(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        _ui.Dispatch(() => { _terminal!.HarnessKeyDown(key, modifiers); return true; }, CancellationToken.None);

    /// <summary>Press Enter, as the owner does to submit.</summary>
    public Task EnterAsync() => KeyAsync(Key.Enter);

    public void Dispose()
    {
        try { _ui.Dispatch(() => { _window?.Close(); return true; }, CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Console.WriteLine($"[rig] desktop host close: {ex.Message}"); }
        _ui.Dispose();
    }
}
