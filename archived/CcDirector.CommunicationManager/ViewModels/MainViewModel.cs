using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunicationManager.Models;
using CommunicationManager.Services;
using Microsoft.Data.Sqlite;

namespace CommunicationManager.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ContentService _contentService;
    private bool _isRefreshing;
    private bool _disposed;
    private DispatcherTimer? _pollTimer;
    private Dictionary<string, int>? _lastKnownStats;

    [ObservableProperty]
    private ObservableCollection<ContentItem> _pendingItems = new();

    [ObservableProperty]
    private ObservableCollection<ContentItem> _approvedItems = new();

    [ObservableProperty]
    private ObservableCollection<ContentItem> _rejectedItems = new();

    [ObservableProperty]
    private ObservableCollection<ContentItem> _sentItems = new();

    [ObservableProperty]
    private ObservableCollection<ContentItem> _errorItems = new();

    [ObservableProperty]
    private ContentItem? _selectedItem;

    [ObservableProperty]
    private string _selectedTab = "Pending";

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editContent = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _approvedCount;

    public bool HasApprovedItems => ApprovedCount > 0;

    partial void OnApprovedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasApprovedItems));
    }

    [ObservableProperty]
    private int _rejectedCount;

    [ObservableProperty]
    private int _sentCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private bool _isPreviewMode = true;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private string _selectedPlatformFilter = "All";

    [ObservableProperty]
    private string _selectedDateFilter = "All Upcoming";

    [ObservableProperty]
    private string _selectedApprovedView = "List";

    [ObservableProperty]
    private ObservableCollection<ContentItem> _filteredItems = new();

    [RelayCommand]
    private void TogglePreviewMode()
    {
        IsPreviewMode = !IsPreviewMode;
        StatusMessage = IsPreviewMode ? "Preview mode" : "Raw mode";
    }

    [RelayCommand]
    private void SetPlatformFilter(string platform)
    {
        FileLog.Write($"[CommunicationManager.VM] SetPlatformFilter: {platform}");
        SelectedPlatformFilter = platform;
        RebuildFilteredItems();
    }

    [RelayCommand]
    private void SetDateFilter(string dateFilter)
    {
        FileLog.Write($"[CommunicationManager.VM] SetDateFilter: {dateFilter}");
        SelectedDateFilter = dateFilter;
        RebuildFilteredItems();
    }

    [RelayCommand]
    private void SetApprovedView(string view)
    {
        FileLog.Write($"[CommunicationManager.VM] SetApprovedView: {view}");
        SelectedApprovedView = view;
    }

    public MainViewModel()
    {
        FileLog.Write("[CommunicationManager.VM] Constructor");
        // Get content path - look for content folder relative to exe or use default
        var contentPath = GetContentPath();

        _contentService = new ContentService(contentPath);
    }

    private static string GetContentPath()
    {
        var path = CcStorage.ToolConfig("comm-queue");
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task InitializeAsync()
    {
        FileLog.Write("[CommunicationManager.VM] InitializeAsync");
        await _contentService.InitializeAsync();
        await RefreshAsync();
    }

    /// <summary>
    /// Start the polling timer. Call when the panel becomes visible.
    /// </summary>
    public void StartPolling()
    {
        FileLog.Write("[CommunicationManager.VM] StartPolling");
        if (_pollTimer == null)
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _pollTimer.Tick += async (_, _) => await SmartRefreshAsync();
        }
        _pollTimer.Start();
    }

    /// <summary>
    /// Stop the polling timer. Call when the panel is hidden to save resources.
    /// </summary>
    public void StopPolling()
    {
        FileLog.Write("[CommunicationManager.VM] StopPolling");
        _pollTimer?.Stop();
    }

    /// <summary>
    /// Lightweight badge-only refresh. Queries pending count from DB without loading items.
    /// Called by background timer to keep sidebar badge current.
    /// </summary>
    public async Task RefreshBadgeCountAsync()
    {
        try
        {
            var stats = await _contentService.GetStatsAsync();
            stats.TryGetValue("pending_review", out var pending);
            PendingCount = pending;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationManager.VM] RefreshBadgeCountAsync FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Smart refresh that only touches the UI when data has actually changed.
    /// Compares stats counts first; if unchanged, skips the full reload entirely.
    /// </summary>
    public async Task SmartRefreshAsync()
    {
        if (_isRefreshing) return;

        try
        {
            var stats = await _contentService.GetStatsAsync();

            if (_lastKnownStats != null && StatsEqual(_lastKnownStats, stats))
                return;

            _lastKnownStats = stats;
            FileLog.Write("[CommunicationManager.VM] SmartRefreshAsync: stats changed, merging");

            _isRefreshing = true;

            var selectedId = SelectedItem?.Id;

            var pending = await _contentService.LoadPendingItemsAsync();
            var approved = await _contentService.LoadApprovedItemsAsync();
            var rejected = await _contentService.LoadRejectedItemsAsync();
            var sent = await _contentService.LoadPostedItemsAsync();
            var error = await _contentService.LoadErrorItemsAsync();

            MergeCollection(PendingItems, pending);
            MergeCollection(ApprovedItems, approved);
            MergeCollection(RejectedItems, rejected);
            MergeCollection(SentItems, sent);
            MergeCollection(ErrorItems, error);

            PendingCount = pending.Count;
            ApprovedCount = approved.Count;
            RejectedCount = rejected.Count;
            SentCount = sent.Count;
            ErrorCount = error.Count;

            RebuildFilteredItems();

            // Restore selection
            if (selectedId != null)
            {
                var match = FilteredItems.FirstOrDefault(i => i.Id == selectedId);
                SelectedItem = match; // null if removed externally
            }
            else
            {
                AutoSelectFirstItem();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationManager.VM] SmartRefreshAsync FAILED: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static bool StatsEqual(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var val) || val != kvp.Value)
                return false;
        }
        return true;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        _lastKnownStats = null; // Force next smart poll to re-evaluate

        try
        {
            var pending = await _contentService.LoadPendingItemsAsync();
            var approved = await _contentService.LoadApprovedItemsAsync();
            var rejected = await _contentService.LoadRejectedItemsAsync();
            var sent = await _contentService.LoadPostedItemsAsync();
            var error = await _contentService.LoadErrorItemsAsync();

            // Track if we had no items before
            var hadNoItems = PendingItems.Count == 0 && ApprovedItems.Count == 0
                          && RejectedItems.Count == 0 && SentItems.Count == 0
                          && ErrorItems.Count == 0;

            // Update existing collections to preserve bindings
            UpdateCollection(PendingItems, pending);
            UpdateCollection(ApprovedItems, approved);
            UpdateCollection(RejectedItems, rejected);
            UpdateCollection(SentItems, sent);
            UpdateCollection(ErrorItems, error);

            PendingCount = pending.Count;
            ApprovedCount = approved.Count;
            RejectedCount = rejected.Count;
            SentCount = sent.Count;
            ErrorCount = error.Count;

            RebuildFilteredItems();

            // Auto-select first item if nothing selected
            if (SelectedItem == null)
            {
                AutoSelectFirstItem();
            }
            // Also auto-select if we just got items for the first time
            else if (hadNoItems && pending.Count > 0)
            {
                AutoSelectFirstItem();
            }

            StatusMessage = $"Loaded {pending.Count} pending, {approved.Count} approved, {rejected.Count} rejected, {sent.Count} sent, {error.Count} error";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationManager.VM] RefreshAsync FAILED: {ex.Message}");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void AutoSelectFirstItem()
    {
        if (FilteredItems.Count > 0)
        {
            SelectedItem = FilteredItems[0];
        }
    }

    public void OnTabChanged(string tabName)
    {
        SelectedTab = tabName;
        SelectedPlatformFilter = "All";
        SelectedDateFilter = "All Upcoming";
        RebuildFilteredItems();
        AutoSelectFirstItem();
    }

    private void RebuildFilteredItems()
    {
        var source = SelectedTab switch
        {
            "Pending" => PendingItems,
            "Approved" => ApprovedItems,
            "Rejected" => RejectedItems,
            "Sent" => SentItems,
            "Error" => ErrorItems,
            _ => PendingItems
        };

        FilteredItems.Clear();

        foreach (var item in source)
        {
            if (SelectedPlatformFilter != "All" &&
                !item.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SelectedTab == "Approved" && !PassesDateFilter(item))
            {
                continue;
            }

            FilteredItems.Add(item);
        }
    }

    private bool PassesDateFilter(ContentItem item)
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var endOfWeek = today.AddDays(7 - (int)today.DayOfWeek);

        // For ASAP items, treat them as "today"
        var effectiveDate = item.IsScheduled ? item.ScheduledFor.GetValueOrDefault().Date : today;

        return SelectedDateFilter switch
        {
            "Today" => effectiveDate == today,
            "Tomorrow" => effectiveDate == tomorrow,
            "This Week" => effectiveDate >= today && effectiveDate <= endOfWeek,
            "All Upcoming" => effectiveDate >= today || item.IsAsap || item.IsHold,
            _ => true
        };
    }

    public int GetDateFilterCount(string filter)
    {
        var source = ApprovedItems;
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var endOfWeek = today.AddDays(7 - (int)today.DayOfWeek);

        return filter switch
        {
            "Today" => source.Count(i => (SelectedPlatformFilter == "All" || i.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                && ((i.IsScheduled && i.ScheduledFor.GetValueOrDefault().Date == today) || i.IsAsap)),
            "Tomorrow" => source.Count(i => (SelectedPlatformFilter == "All" || i.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                && i.IsScheduled && i.ScheduledFor.GetValueOrDefault().Date == tomorrow),
            "This Week" => source.Count(i => (SelectedPlatformFilter == "All" || i.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                && ((i.IsScheduled && i.ScheduledFor.GetValueOrDefault().Date >= today && i.ScheduledFor.GetValueOrDefault().Date <= endOfWeek) || i.IsAsap)),
            "All Upcoming" => source.Count(i => (SelectedPlatformFilter == "All" || i.Platform.Equals(SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                && ((i.IsScheduled && i.ScheduledFor.GetValueOrDefault().Date >= today) || i.IsAsap || i.IsHold)),
            _ => source.Count
        };
    }

    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Approving...";

        if (await _contentService.ApproveItemAsync(item))
        {
            StatusMessage = $"Approved: {item.DisplayTitle}";
            await RefreshAsync();
            SelectNextItem();
        }
        else
        {
            StatusMessage = "Failed to approve item";
        }
    }

    [RelayCommand]
    private async Task ApproveWithScheduleAsync((string Timing, DateTime? ScheduledFor) schedule)
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Approving with schedule...";

        if (await _contentService.ApproveWithScheduleAsync(item, schedule.Timing, schedule.ScheduledFor))
        {
            var desc = schedule.Timing == "hold" ? "on hold"
                : schedule.ScheduledFor.HasValue ? $"scheduled for {schedule.ScheduledFor:MMM d, h:mm tt}"
                : "ASAP";
            StatusMessage = $"Approved ({desc}): {item.DisplayTitle}";
            await RefreshAsync();
            SelectNextItem();
        }
        else
        {
            StatusMessage = "Failed to approve item";
        }
    }

    [RelayCommand]
    private async Task RescheduleAsync((string Timing, DateTime? ScheduledFor) schedule)
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Rescheduling...";

        if (await _contentService.UpdateScheduleAsync(item, schedule.Timing, schedule.ScheduledFor))
        {
            var desc = schedule.Timing == "hold" ? "on hold"
                : schedule.ScheduledFor.HasValue ? $"scheduled for {schedule.ScheduledFor:MMM d, h:mm tt}"
                : "ASAP";
            StatusMessage = $"Rescheduled ({desc}): {item.DisplayTitle}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = "Failed to reschedule item";
        }
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Rejecting...";

        if (await _contentService.RejectItemAsync(item))
        {
            StatusMessage = $"Rejected: {item.DisplayTitle}";
            await RefreshAsync();
            SelectNextItem();
        }
        else
        {
            StatusMessage = "Failed to reject item";
        }
    }

    [RelayCommand]
    private async Task RejectWithReasonAsync(string reason)
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Rejecting...";

        if (await _contentService.RejectItemAsync(item, reason))
        {
            StatusMessage = $"Rejected: {item.DisplayTitle}";
            await RefreshAsync();
            SelectNextItem();
        }
        else
        {
            StatusMessage = "Failed to reject item";
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedItem == null) return;

        var result = MessageBox.Show(
            Application.Current.MainWindow,
            $"Are you sure you want to permanently delete this item?\n\n{SelectedItem.DisplayTitle}",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var item = SelectedItem;
        StatusMessage = "Deleting...";

        if (await _contentService.DeleteItemAsync(item))
        {
            StatusMessage = $"Deleted: {item.DisplayTitle}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = "Failed to delete item";
        }
    }

    [RelayCommand]
    private void StartEdit()
    {
        if (SelectedItem == null) return;
        EditContent = SelectedItem.Content;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditContent = string.Empty;
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (SelectedItem == null) return;

        SelectedItem.Content = EditContent;

        if (await _contentService.SaveItemAsync(SelectedItem))
        {
            StatusMessage = "Changes saved";
            IsEditing = false;
            OnPropertyChanged(nameof(SelectedItem));
        }
        else
        {
            StatusMessage = "Failed to save changes";
        }
    }

    [RelayCommand]
    private void OpenContextUrl()
    {
        if (SelectedItem?.ContextUrl == null) return;
        OpenUrl(SelectedItem.ContextUrl);
    }

    [RelayCommand]
    private void OpenDestinationUrl()
    {
        if (SelectedItem?.DestinationUrl == null) return;
        OpenUrl(SelectedItem.DestinationUrl);
    }

    [RelayCommand]
    private void CopyDestinationUrl()
    {
        if (SelectedItem?.DestinationUrl == null) return;
        Clipboard.SetText(SelectedItem.DestinationUrl);
        StatusMessage = "Destination URL copied to clipboard";
    }

    [RelayCommand]
    private void OpenRecipientUrl()
    {
        if (SelectedItem?.Recipient?.ProfileUrl == null) return;
        OpenUrl(SelectedItem.Recipient.ProfileUrl);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, $"Failed to open URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Skip()
    {
        SelectNextItem();
    }

    [RelayCommand]
    private async Task RetryErrorAsync()
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Retrying...";

        if (await _contentService.RetryErrorItemAsync(item))
        {
            StatusMessage = $"Moved to approved: {item.DisplayTitle}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = "Failed to retry item";
        }
    }

    [RelayCommand]
    private async Task MoveToReviewAsync()
    {
        if (SelectedItem == null) return;

        var item = SelectedItem;
        StatusMessage = "Moving to review...";

        if (await _contentService.MoveToReviewAsync(item))
        {
            StatusMessage = $"Moved to review: {item.DisplayTitle}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = "Failed to move item";
        }
    }

    [RelayCommand]
    private async Task PostToLinkedInAsync()
    {
        if (SelectedItem == null) return;
        if (!SelectedItem.IsLinkedIn)
        {
            StatusMessage = "This item is not a LinkedIn post";
            return;
        }

        var item = SelectedItem;
        StatusMessage = "Posting to LinkedIn...";

        try
        {
            // Get first image temp path if available
            string? imagePath = null;
            if (item.Media != null && item.Media.Count > 0)
            {
                var firstImage = item.Media.FirstOrDefault(m => m.IsImage);
                if (firstImage != null && firstImage.HasTempFile)
                {
                    imagePath = firstImage.TempPath;
                }
                else if (firstImage != null)
                {
                    // Extract to temp if not already done
                    imagePath = _contentService.ExtractMediaToTemp(firstImage.Id);
                }
            }

            // Build command arguments
            var args = new List<string> { "create" };

            // Escape content for command line
            var escapedContent = item.Content.Replace("\"", "\\\"");
            args.Add($"\"{escapedContent}\"");

            if (!string.IsNullOrEmpty(imagePath))
            {
                args.Add("--image");
                args.Add($"\"{imagePath}\"");
            }

            // Run cc-browser with LinkedIn connection to create post
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(CcStorage.Bin(), "cc-browser.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--connection");
            startInfo.ArgumentList.Add("linkedin");
            startInfo.ArgumentList.Add("navigate");
            startInfo.ArgumentList.Add("--url");
            startInfo.ArgumentList.Add("https://www.linkedin.com/feed/");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                StatusMessage = "Failed to start cc-browser";
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                // Mark as posted
                if (await _contentService.MarkAsPostedAsync(item))
                {
                    StatusMessage = $"Posted to LinkedIn: {item.DisplayTitle}";
                    await RefreshAsync();
                }
                else
                {
                    StatusMessage = "Posted but failed to update status";
                }
            }
            else
            {
                StatusMessage = $"LinkedIn posting failed: {error}";
                MessageBox.Show(Application.Current.MainWindow, $"LinkedIn posting failed:\n\n{error}\n\n{output}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show(Application.Current.MainWindow, $"Failed to post to LinkedIn: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private enum DispatchResult { Sent, Failed, Skipped }

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, SendFromConfig> _sendFromConfigs = LoadSendFromConfigs();

    private record SendFromConfig(string Tool, string? ToolAccount);

    private static Dictionary<string, SendFromConfig> LoadSendFromConfigs()
    {
        var configPath = CcStorage.ConfigJson();
        if (!File.Exists(configPath))
            return new Dictionary<string, SendFromConfig>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("comm_manager", out var cm) ||
            !cm.TryGetProperty("send_from_accounts", out var accounts))
            return new Dictionary<string, SendFromConfig>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, SendFromConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var acct in accounts.EnumerateObject())
        {
            var tool = "";
            string? toolAccount = null;

            if (acct.Value.TryGetProperty("tool", out var toolProp))
                tool = toolProp.GetString() ?? "";
            if (acct.Value.TryGetProperty("tool_account", out var toolAcctProp))
                toolAccount = toolAcctProp.GetString();

            var config = new SendFromConfig(tool, toolAccount);
            result[acct.Name] = config;

            // Also index by email address so lookups work whether send_from is
            // an account name ("consulting") or an email ("user@example.com")
            if (acct.Value.TryGetProperty("email", out var emailProp))
            {
                var email = emailProp.GetString();
                if (!string.IsNullOrEmpty(email))
                    result.TryAdd(email, config);
            }
        }

        FileLog.Write($"[CommunicationManager.VM] LoadSendFromConfigs: {string.Join(", ", result.Select(kv => $"{kv.Key}={kv.Value.Tool}/{kv.Value.ToolAccount}"))}");
        return result;
    }

    [RelayCommand]
    private async Task SendAllAsync()
    {
        FileLog.Write("[CommunicationManager.VM] SendAllAsync: starting manual dispatch");
        IsSending = true;
        StatusMessage = "Sending approved items...";

        var sent = 0;
        var failed = 0;
        var skipped = 0;
        Views.SendProgressDialog? dialog = null;

        try
        {
            var dbPath = CcStorage.CommQueueDb();
            if (!File.Exists(dbPath))
            {
                StatusMessage = "No communications database found";
                return;
            }

            var items = await GetApprovedItemsAsync(dbPath);
            FileLog.Write($"[CommunicationManager.VM] SendAllAsync: found {items.Count} approved items");

            if (items.Count == 0)
            {
                StatusMessage = "No approved items to send";
                return;
            }

            dialog = new Views.SendProgressDialog(items.Count)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.Show();

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var description = $"[{item.Platform}] ticket #{item.TicketNumber}";
                StatusMessage = $"Dispatching {i + 1}/{items.Count}: {description}";
                dialog.ReportProgress(i + 1, description, sent, failed, skipped);

                var result = await DispatchItemAsync(item, dbPath);
                switch (result)
                {
                    case DispatchResult.Sent: sent++; break;
                    case DispatchResult.Failed: failed++; break;
                    case DispatchResult.Skipped: skipped++; break;
                }
            }

            var resultMsg = $"Dispatch complete: {sent} sent";
            if (failed > 0)
                resultMsg += $", {failed} failed";
            if (skipped > 0)
                resultMsg += $", {skipped} skipped";

            StatusMessage = resultMsg;
            FileLog.Write($"[CommunicationManager.VM] SendAllAsync: {resultMsg}");
            dialog.ReportComplete(sent, failed, skipped);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationManager.VM] SendAllAsync FAILED: {ex.Message}");
            StatusMessage = $"Send error: {ex.Message}";
            dialog?.Close();
        }
        finally
        {
            IsSending = false;
        }
    }

    private static async Task<List<QueuedItem>> GetApprovedItemsAsync(string dbPath)
    {
        FileLog.Write($"[CommunicationManager.VM] GetApprovedItemsAsync: reading from {dbPath}");
        var items = new List<QueuedItem>();
        var connectionString = $"Data Source={dbPath};Mode=ReadOnly";

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ticket_number, platform, type, content, persona, send_from,
                   email_specific, linkedin_specific, reddit_specific,
                   destination_url, context_url
            FROM communications
            WHERE status = 'approved'
            AND (send_timing IS NULL OR send_timing != 'hold')
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new QueuedItem
            {
                Id = reader.GetString(0),
                TicketNumber = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                Platform = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Type = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Content = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Persona = reader.IsDBNull(5) ? "personal" : reader.GetString(5),
                SendFrom = reader.IsDBNull(6) ? null : reader.GetString(6),
                EmailSpecificJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                LinkedInSpecificJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                RedditSpecificJson = reader.IsDBNull(9) ? null : reader.GetString(9),
                DestinationUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
                ContextUrl = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }

        FileLog.Write($"[CommunicationManager.VM] GetApprovedItemsAsync: found {items.Count} items");
        return items;
    }

    private static async Task<DispatchResult> DispatchItemAsync(QueuedItem item, string dbPath)
    {
        FileLog.Write($"[CommunicationManager.VM] DispatchItemAsync: ticket #{item.TicketNumber}, platform={item.Platform}");

        switch (item.Platform.ToLowerInvariant())
        {
            case "email":
                var success = await DispatchEmailItemAsync(item, dbPath);
                return success ? DispatchResult.Sent : DispatchResult.Failed;

            default:
                FileLog.Write($"[CommunicationManager.VM] DispatchItemAsync: platform '{item.Platform}' not yet supported, skipping ticket #{item.TicketNumber}");
                return DispatchResult.Skipped;
        }
    }

    private static async Task<bool> DispatchEmailItemAsync(QueuedItem item, string dbPath)
    {
        FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: ticket #{item.TicketNumber}");

        if (string.IsNullOrEmpty(item.EmailSpecificJson))
        {
            FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: no email_specific data for ticket #{item.TicketNumber}");
            return false;
        }

        EmailSpecificDto? spec;
        try
        {
            spec = JsonSerializer.Deserialize<EmailSpecificDto>(item.EmailSpecificJson, _jsonOptions);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: failed to parse email_specific for ticket #{item.TicketNumber}: {ex.Message}");
            return false;
        }

        if (spec?.To == null || spec.To.Count == 0)
        {
            FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: no recipients for ticket #{item.TicketNumber}");
            return false;
        }

        var sendFrom = item.SendFrom ?? item.Persona;
        _sendFromConfigs.TryGetValue(sendFrom, out var accountConfig);

        if (accountConfig == null)
        {
            var knownKeys = string.Join(", ", _sendFromConfigs.Keys);
            FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync FAILED: no send_from config for '{sendFrom}'. Known: [{knownKeys}]");
            return false;
        }

        var useGmail = accountConfig.Tool.Contains("gmail", StringComparison.OrdinalIgnoreCase);
        var toolName = useGmail ? "cc-gmail" : "cc-outlook";
        var toolPath = Path.Combine(CcStorage.Bin(), useGmail ? "cc-gmail.exe" : "cc-outlook.exe");

        var to = string.Join(",", spec.To);
        var subject = spec.Subject ?? "(no subject)";

        FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: ticket #{item.TicketNumber} to {to} via {toolName} (account={accountConfig?.ToolAccount ?? "default"})");

        var args = new List<string>();

        // Pass account to cc-gmail/cc-outlook if configured
        if (accountConfig?.ToolAccount != null)
        {
            args.Add("--account");
            args.Add(accountConfig.ToolAccount);
        }

        // Convert plain text newlines to HTML before sending with --html flag.
        // Without this, email clients ignore \n and recipients see a wall of text.
        var htmlBody = HtmlFormatter.ConvertPlainTextToHtml(item.Content);

        if (!string.IsNullOrEmpty(spec.ReplyToMessageId))
        {
            // Thread as a reply to the original message
            FileLog.Write($"[CommunicationManager.VM] DispatchEmailItemAsync: ticket #{item.TicketNumber} is a reply to message {spec.ReplyToMessageId}");
            args.AddRange(new[] { "reply", spec.ReplyToMessageId, "-b", htmlBody, "--all", "--send", "--html" });
        }
        else
        {
            args.AddRange(new[] { "send", "-t", to, "-s", subject, "-b", htmlBody, "--html" });

            if (spec.Cc != null && spec.Cc.Count > 0)
            {
                args.Add("--cc");
                args.Add(string.Join(",", spec.Cc));
            }
            if (spec.Bcc != null && spec.Bcc.Count > 0)
            {
                args.Add("--bcc");
                args.Add(string.Join(",", spec.Bcc));
            }
        }

        var attachFlag = useGmail ? "--attach" : "-a";
        if (spec.Attachments != null)
        {
            foreach (var attachment in spec.Attachments)
            {
                if (File.Exists(attachment))
                {
                    args.Add(attachFlag);
                    args.Add(attachment);
                }
            }
        }

        return await RunToolAndMarkPostedAsync(toolPath, args, item, dbPath, toolName);
    }

    private static async Task<bool> RunToolAndMarkPostedAsync(
        string toolPath, List<string> args, QueuedItem item, string dbPath, string toolName)
    {
        FileLog.Write($"[CommunicationManager.VM] RunToolAndMarkPostedAsync: ticket #{item.TicketNumber} via {toolName}");

        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null)
        {
            FileLog.Write($"[CommunicationManager.VM] RunToolAndMarkPostedAsync: failed to start {toolPath}");
            return false;
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await stderrTask;
        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            MarkPosted(item.Id, dbPath);
            FileLog.Write($"[CommunicationManager.VM] RunToolAndMarkPostedAsync: ticket #{item.TicketNumber} sent OK via {toolName}");
            LogToVaultAsync(item.TicketNumber);
            return true;
        }

        var error = string.IsNullOrEmpty(stderr) ? stdout : stderr;
        FileLog.Write($"[CommunicationManager.VM] RunToolAndMarkPostedAsync: ticket #{item.TicketNumber} FAILED via {toolName}: {error}");
        return false;
    }

    private static void MarkPosted(string id, string dbPath)
    {
        FileLog.Write($"[CommunicationManager.VM] MarkPosted: id={id}");
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE communications
            SET status = 'posted', posted_at = @now, posted_by = 'cc-director-manual'
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static async void LogToVaultAsync(int ticketNumber)
    {
        FileLog.Write($"[CommunicationManager.VM] LogToVaultAsync: ticket #{ticketNumber}");
        try
        {
            var binDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cc-director", "bin");
            var toolPath = Path.Combine(binDir, "cc-comm-queue.exe");

            if (!File.Exists(toolPath))
            {
                FileLog.Write($"[CommunicationManager.VM] LogToVaultAsync: cc-comm-queue.exe not found at {toolPath}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = toolPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("log-to-vault");
            psi.ArgumentList.Add(ticketNumber.ToString());

            using var process = Process.Start(psi);
            if (process == null)
            {
                FileLog.Write("[CommunicationManager.VM] LogToVaultAsync: failed to start cc-comm-queue");
                return;
            }

            await process.WaitForExitAsync();
            FileLog.Write($"[CommunicationManager.VM] LogToVaultAsync: ticket #{ticketNumber} exit code {process.ExitCode}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommunicationManager.VM] LogToVaultAsync FAILED: {ex.Message}");
        }
    }

    private class QueuedItem
    {
        public string Id { get; set; } = "";
        public int TicketNumber { get; set; }
        public string Platform { get; set; } = "";
        public string Type { get; set; } = "";
        public string Content { get; set; } = "";
        public string Persona { get; set; } = "personal";
        public string? SendFrom { get; set; }
        public string? EmailSpecificJson { get; set; }
        public string? LinkedInSpecificJson { get; set; }
        public string? RedditSpecificJson { get; set; }
        public string? DestinationUrl { get; set; }
        public string? ContextUrl { get; set; }
    }

    private class EmailSpecificDto
    {
        public List<string>? To { get; set; }
        public List<string>? Cc { get; set; }
        public List<string>? Bcc { get; set; }
        public string? Subject { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("reply_to_message_id")]
        public string? ReplyToMessageId { get; set; }
        public List<string>? Attachments { get; set; }
    }

    private void SelectNextItem()
    {
        if (FilteredItems.Count == 0)
        {
            SelectedItem = null;
            return;
        }

        var currentIndex = FilteredItems.IndexOf(SelectedItem ?? FilteredItems[0]);
        if (currentIndex < FilteredItems.Count - 1)
        {
            SelectedItem = FilteredItems[currentIndex + 1];
        }
        else if (FilteredItems.Count > 0)
        {
            SelectedItem = FilteredItems[0];
        }
    }

    /// <summary>
    /// Diff-merge fresh items into an existing ObservableCollection.
    /// Only fires collection-change notifications for actual differences,
    /// so the UI repaints only what changed (no full-list flash).
    /// </summary>
    private static void MergeCollection(ObservableCollection<ContentItem> existing, List<ContentItem> fresh)
    {
        var freshIds = new HashSet<string>(fresh.Select(i => i.Id));

        // Remove items no longer present (reverse iterate for stable indices)
        for (var i = existing.Count - 1; i >= 0; i--)
        {
            if (!freshIds.Contains(existing[i].Id))
                existing.RemoveAt(i);
        }

        // Insert or reposition items to match fresh order
        for (var i = 0; i < fresh.Count; i++)
        {
            if (i < existing.Count && existing[i].Id == fresh[i].Id)
                continue; // already in correct position

            var existingIndex = -1;
            for (var j = i + 1; j < existing.Count; j++)
            {
                if (existing[j].Id == fresh[i].Id)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                existing.RemoveAt(existingIndex);
                existing.Insert(i, fresh[i]);
            }
            else
            {
                existing.Insert(i, fresh[i]);
            }
        }
    }

    /// <summary>
    /// Full clear-and-replace for the manual Refresh button path.
    /// </summary>
    private static void UpdateCollection(ObservableCollection<ContentItem> collection, List<ContentItem> newItems)
    {
        collection.Clear();
        foreach (var item in newItems)
        {
            collection.Add(item);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Stop();
        _contentService.Dispose();
    }
}
