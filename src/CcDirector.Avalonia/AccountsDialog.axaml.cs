// Scratch edit for the path-filter proof. Never merged.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class AccountsDialog : Window
{
    private readonly ClaudeAccountStore _store;
    private readonly string _credentialsPath;

    public AccountsDialog()
    {
        InitializeComponent();
        _store = null!;
        _credentialsPath = string.Empty;
    }

    public AccountsDialog(ClaudeAccountStore store)
    {
        InitializeComponent();
        _store = store;
        _credentialsPath = ClaudeAccountStore.GetDefaultCredentialsPath();
        CredPathLink.Content = _credentialsPath;
        RefreshList();
        RefreshCredentialStatus();
    }

    private void RefreshCredentialStatus()
    {
        FileLog.Write("[AccountsDialog] RefreshCredentialStatus");
        var (matched, creds) = _store.ReadCredentialStatus();

        if (creds == null)
        {
            CredentialStatusText.Text = "No credentials file found. Click 'Login New Account' to get started.";
            CredentialStatusText.Foreground = BrushFromHex("#D97706");
            BtnCaptureUnknown.IsVisible = false;
            return;
        }

        var tierLabel = FormatTier(creds.SubscriptionType, creds.RateLimitTier);

        if (matched != null)
        {
            CredentialStatusText.Text = $"Current login: {matched.Label} ({tierLabel})";
            CredentialStatusText.Foreground = BrushFromHex("#CCCCCC");
            BtnCaptureUnknown.IsVisible = false;
        }
        else
        {
            CredentialStatusText.Text = $"Current login: Unknown account ({tierLabel})";
            CredentialStatusText.Foreground = BrushFromHex("#D97706");
            BtnCaptureUnknown.IsVisible = true;
        }
    }

    private async void BtnLogin_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnLogin_Click");

        BtnLogin.IsEnabled = false;
        BtnBrowse.IsEnabled = false;
        LoginProgressBar.IsVisible = true;

        try
        {
            var (_, beforeCreds) = _store.ReadCredentialStatus();
            var beforeToken = beforeCreds?.AccessToken ?? "";

            var exitCode = await Task.Run(() =>
            {
                FileLog.Write("[AccountsDialog] BtnLogin_Click: starting claude login process");
                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "login",
                    UseShellExecute = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    FileLog.Write("[AccountsDialog] BtnLogin_Click: failed to start process");
                    return -1;
                }

                process.WaitForExit();
                FileLog.Write($"[AccountsDialog] BtnLogin_Click: process exited with code {process.ExitCode}");
                return process.ExitCode;
            });

            if (exitCode != 0)
            {
                // TODO: Replace with proper Avalonia dialog
                FileLog.Write($"[AccountsDialog] BtnLogin_Click: claude login exited with code {exitCode}");
                return;
            }

            var (matched, creds) = _store.ReadCredentialStatus();
            if (creds == null)
            {
                // TODO: Replace with proper Avalonia dialog
                FileLog.Write("[AccountsDialog] BtnLogin_Click: login completed but no credentials file found");
                return;
            }

            if (creds.AccessToken == beforeToken)
            {
                // TODO: Replace with proper Avalonia dialog
                FileLog.Write("[AccountsDialog] BtnLogin_Click: login completed but credentials did not change");
                RefreshCredentialStatus();
                return;
            }

            if (matched != null)
            {
                // TODO: Replace with proper Avalonia dialog
                FileLog.Write($"[AccountsDialog] BtnLogin_Click: updated tokens for existing account '{matched.Label}'");
                RefreshList();
                RefreshCredentialStatus();
                return;
            }

            var account = _store.CaptureFromCredentials();
            if (account == null)
            {
                // TODO: Replace with proper Avalonia dialog
                FileLog.Write("[AccountsDialog] BtnLogin_Click: could not capture new credentials");
                return;
            }

            if (string.IsNullOrEmpty(account.Label))
            {
                string? autoLabel = null;
                try
                {
                    autoLabel = await ClaudeAccountStore.TryFetchProfileLabelAsync(account.AccessToken);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[AccountsDialog] BtnLogin_Click: auto-label fetch failed: {ex.Message}");
                }

                var label = await PromptForLabel("Enter a label for this account (e.g. 'Personal', 'Work'):", autoLabel ?? "");
                if (string.IsNullOrWhiteSpace(label))
                {
                    _store.Remove(account.Id);
                    return;
                }
                _store.UpdateLabel(account.Id, label.Trim());
            }

            RefreshList();
            RefreshCredentialStatus();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnLogin_Click FAILED: {ex.Message}");
            // TODO: Replace with proper Avalonia dialog
        }
        finally
        {
            BtnLogin.IsEnabled = true;
            BtnBrowse.IsEnabled = true;
            LoginProgressBar.IsVisible = false;
        }
    }

    private async void BtnCapture_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnCapture_Click");
        try
        {
            var account = _store.CaptureFromCredentials();
            if (account == null)
            {
                // TODO: Replace with proper Avalonia dialog
                FileLog.Write("[AccountsDialog] BtnCapture_Click: could not read credentials");
                return;
            }

            if (string.IsNullOrEmpty(account.Label))
            {
                string? autoLabel = null;
                try
                {
                    autoLabel = await ClaudeAccountStore.TryFetchProfileLabelAsync(account.AccessToken);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[AccountsDialog] BtnCapture_Click: auto-label fetch failed: {ex.Message}");
                }

                var label = await PromptForLabel("Enter a label for this account (e.g. 'Personal', 'Work'):", autoLabel ?? "");
                if (string.IsNullOrWhiteSpace(label))
                {
                    _store.Remove(account.Id);
                    return;
                }
                _store.UpdateLabel(account.Id, label.Trim());
            }
            else
            {
                // TODO: Replace with proper Avalonia dialog
                FileLog.Write($"[AccountsDialog] BtnCapture_Click: token updated for existing account '{account.Label}'");
            }

            RefreshList();
            RefreshCredentialStatus();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnCapture_Click FAILED: {ex.Message}");
            // TODO: Replace with proper Avalonia dialog
        }
    }

    private async void BtnBrowseJson_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnBrowseJson_Click");
        try
        {
            var claudeDir = Path.GetDirectoryName(_credentialsPath) ?? "";
            var startDir = Directory.Exists(claudeDir)
                ? claudeDir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var storageProvider = GetTopLevel(this)?.StorageProvider;
            if (storageProvider == null)
            {
                FileLog.Write("[AccountsDialog] BtnBrowseJson_Click: StorageProvider is null");
                return;
            }

            var startFolder = await storageProvider.TryGetFolderFromPathAsync(startDir);
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select credentials file",
                SuggestedStartLocation = startFolder,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } },
                },
                AllowMultiple = false,
            });

            if (files.Count == 0) return;

            var filePath = files[0].Path.LocalPath;
            FileLog.Write($"[AccountsDialog] BtnBrowseJson_Click: selected={filePath}");
            var json = File.ReadAllText(filePath);
            await AddAccountFromJson(json);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnBrowseJson_Click FAILED: {ex.Message}");
            // TODO: Replace with proper Avalonia dialog
        }
    }

    private async void BtnAddFromJson_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnAddFromJson_Click");
        try
        {
            var json = await PromptForJson();
            if (string.IsNullOrWhiteSpace(json)) return;
            await AddAccountFromJson(json);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnAddFromJson_Click FAILED: {ex.Message}");
            // TODO: Replace with proper Avalonia dialog
        }
    }

    private async Task AddAccountFromJson(string json)
    {
        var creds = ClaudeAccountStore.ParseCredentialsJson(json);
        if (creds == null)
        {
            // TODO: Replace with proper Avalonia dialog
            FileLog.Write("[AccountsDialog] AddAccountFromJson: could not parse credentials JSON");
            return;
        }

        var existing = _store.GetAll().FirstOrDefault(a => a.AccessToken == creds.AccessToken);
        if (existing != null)
        {
            existing.RefreshToken = creds.RefreshToken;
            existing.ExpiresAt = creds.ExpiresAt;
            existing.SubscriptionType = creds.SubscriptionType;
            existing.RateLimitTier = creds.RateLimitTier;
            _store.UpdateToken(existing.Id, creds.AccessToken, creds.RefreshToken, creds.ExpiresAt);
            // TODO: Replace with proper Avalonia dialog
            FileLog.Write($"[AccountsDialog] AddAccountFromJson: token updated for existing account '{existing.Label}'");
            RefreshList();
            RefreshCredentialStatus();
            return;
        }

        string? autoLabel = null;
        try
        {
            autoLabel = await ClaudeAccountStore.TryFetchProfileLabelAsync(creds.AccessToken);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] AddAccountFromJson: auto-label fetch failed: {ex.Message}");
        }

        var label = await PromptForLabel("Enter a label for this account (e.g. 'Personal', 'Work'):", autoLabel ?? "");
        if (string.IsNullOrWhiteSpace(label)) return;

        var account = new ClaudeAccount
        {
            Label = label.Trim(),
            AccessToken = creds.AccessToken,
            RefreshToken = creds.RefreshToken,
            ExpiresAt = creds.ExpiresAt,
            SubscriptionType = creds.SubscriptionType,
            RateLimitTier = creds.RateLimitTier,
            IsActive = false,
        };
        _store.Add(account);

        FileLog.Write($"[AccountsDialog] AddAccountFromJson: added account '{label}' ({creds.SubscriptionType})");
        RefreshList();
        RefreshCredentialStatus();
    }

    private void BtnSetActive_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        FileLog.Write($"[AccountsDialog] BtnSetActive_Click: id={id}");
        _store.SetActiveAccount(id);
        RefreshList();
        RefreshCredentialStatus();
    }

    private async void BtnCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnCopyPath_Click");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(_credentialsPath);
        }
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnOpenFolder_Click");
        var dir = Path.GetDirectoryName(_credentialsPath);
        if (dir != null && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true,
            });
        }
    }

    private async Task<string?> PromptForJson()
    {
        var dlg = new Window
        {
            Title = "Add Account from JSON",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = BrushFromHex("#252526"),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };

        var instructions = new TextBlock
        {
            Text = "Paste the contents of ~/.claude/.credentials.json below.\n\nTo get this: open a terminal where you are logged in as the desired account, then run:\n  type %USERPROFILE%\\.claude\\.credentials.json",
            Foreground = BrushFromHex("#CCCCCC"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(instructions);

        var input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 120,
            Background = BrushFromHex("#1E1E1E"),
            Foreground = BrushFromHex("#CCCCCC"),
            CaretBrush = BrushFromHex("#CCCCCC"),
            BorderBrush = BrushFromHex("#3C3C3C"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New"),
        };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        bool? dialogResult = null;

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80, Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Background = BrushFromHex("#3C3C3C"),
            Foreground = BrushFromHex("#CCCCCC"),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        cancelBtn.Click += (_, _) => { dialogResult = false; dlg.Close(); };
        buttons.Children.Add(cancelBtn);

        var addBtn = new Button
        {
            Content = "Add Account",
            Width = 100, Height = 30,
            Background = BrushFromHex("#007ACC"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        addBtn.Click += (_, _) => { dialogResult = true; dlg.Close(); };
        buttons.Children.Add(addBtn);

        panel.Children.Add(buttons);
        dlg.Content = panel;

        dlg.Opened += (_, _) => input.Focus();
        await dlg.ShowDialog<bool?>(this);

        return dialogResult == true ? input.Text : null;
    }

    private async void BtnRename_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        FileLog.Write($"[AccountsDialog] BtnRename_Click: id={id}");
        var account = _store.GetAll().FirstOrDefault(a => a.Id == id);
        if (account == null) return;

        var newLabel = await PromptForLabel($"Rename '{account.Label}' to:", account.Label);
        if (string.IsNullOrWhiteSpace(newLabel)) return;

        _store.UpdateLabel(id, newLabel.Trim());
        RefreshList();
        RefreshCredentialStatus();
    }

    private void BtnRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        FileLog.Write($"[AccountsDialog] BtnRemove_Click: id={id}");
        var account = _store.GetAll().FirstOrDefault(a => a.Id == id);
        if (account == null) return;

        // TODO: Replace with proper Avalonia confirmation dialog
        FileLog.Write($"[AccountsDialog] BtnRemove_Click: removing account '{account.Label}'");
        _store.Remove(id);
        RefreshList();
        RefreshCredentialStatus();
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshList()
    {
        var accounts = _store.GetAll();
        var viewModels = accounts.Select(a => new AccountViewModel(a)).ToList();
        AccountList.ItemsSource = viewModels;
        EmptyMessage.IsVisible = viewModels.Count == 0;
    }

    private async Task<string?> PromptForLabel(string prompt, string defaultValue = "")
    {
        var dlg = new Window
        {
            Title = "Account Label",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = BrushFromHex("#252526"),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };

        var label = new TextBlock
        {
            Text = prompt,
            Foreground = BrushFromHex("#CCCCCC"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(label);

        var input = new TextBox
        {
            Text = defaultValue,
            Background = BrushFromHex("#1E1E1E"),
            Foreground = BrushFromHex("#CCCCCC"),
            CaretBrush = BrushFromHex("#CCCCCC"),
            BorderBrush = BrushFromHex("#3C3C3C"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
        };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        bool? dialogResult = null;

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80, Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Background = BrushFromHex("#3C3C3C"),
            Foreground = BrushFromHex("#CCCCCC"),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        cancelBtn.Click += (_, _) => { dialogResult = false; dlg.Close(); };
        buttons.Children.Add(cancelBtn);

        var okBtn = new Button
        {
            Content = "OK",
            Width = 80, Height = 30,
            Background = BrushFromHex("#007ACC"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        okBtn.Click += (_, _) => { dialogResult = true; dlg.Close(); };
        buttons.Children.Add(okBtn);

        panel.Children.Add(buttons);
        dlg.Content = panel;

        dlg.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        await dlg.ShowDialog<bool?>(this);
        return dialogResult == true ? input.Text : null;
    }

    private static string FormatTier(string? subscriptionType, string? rateLimitTier)
    {
        var sub = (subscriptionType ?? "").ToUpperInvariant();
        var tier = rateLimitTier ?? "";
        if (tier.Contains("20x", StringComparison.OrdinalIgnoreCase))
            return $"{sub} 20x";
        if (tier.Contains("5x", StringComparison.OrdinalIgnoreCase))
            return $"{sub} 5x";
        return sub;
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var color = Color.Parse(hex);
        return new SolidColorBrush(color);
    }

    internal sealed class AccountViewModel
    {
        public string Id { get; }
        public string Label { get; }
        public string TierDisplay { get; }
        public string ExpiryDisplay { get; }
        public bool IsActive { get; }
        public bool IsNotActive { get; }

        public AccountViewModel(ClaudeAccount account)
        {
            Id = account.Id;
            Label = string.IsNullOrEmpty(account.Label) ? "(unnamed)" : account.Label;
            IsActive = account.IsActive;
            IsNotActive = !account.IsActive;
            TierDisplay = FormatTier(account.SubscriptionType, account.RateLimitTier);

            if (account.ExpiresAt > 0)
            {
                var expiry = DateTimeOffset.FromUnixTimeMilliseconds(account.ExpiresAt);
                ExpiryDisplay = $"Token expires: {expiry:MMM d, yyyy h:mm tt}";
            }
            else
            {
                ExpiryDisplay = "Token expiry: unknown";
            }
        }
    }
}
