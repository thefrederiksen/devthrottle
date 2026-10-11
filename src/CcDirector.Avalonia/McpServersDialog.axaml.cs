using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class McpServersDialog : Window
{
    private readonly McpConfigManager _configManager;
    private readonly string? _projectDir;
    private readonly ObservableCollection<string> _serverNames = new();
    private List<McpServerConfig> _servers = new();
    private string? _selectedOriginalName;
    private bool _suppressSelectionChanged;

    private static readonly string[] TransportTypes = ["stdio", "sse"];

    public McpServersDialog(McpConfigManager configManager, string? projectDir = null)
    {
        InitializeComponent();

        _configManager = configManager;
        _projectDir = projectDir;

        TransportCombo.ItemsSource = TransportTypes;
        TransportCombo.SelectedIndex = 0;
        ServerList.ItemsSource = _serverNames;

        var scopes = new List<string> { "Global" };
        if (!string.IsNullOrEmpty(_projectDir))
            scopes.Add($"Project: {_projectDir}");
        ScopeCombo.ItemsSource = scopes;

        EditPanel.IsEnabled = false;

        Loaded += McpServersDialog_Loaded;
    }

    private void McpServersDialog_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[McpServersDialog] Loaded");
            ScopeCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] Loaded FAILED: {ex.Message}");
        }
    }

    private string GetCurrentConfigPath()
    {
        var scope = ScopeCombo.SelectedItem as string ?? "Global";
        if (scope.StartsWith("Project:") && _projectDir != null)
            return Path.Combine(_projectDir, ".mcp.json");
        return McpConfigManager.GlobalPath;
    }

    private void ScopeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            FileLog.Write($"[McpServersDialog] ScopeCombo_SelectionChanged: {ScopeCombo.SelectedItem}");
            LoadServerList();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] ScopeCombo_SelectionChanged FAILED: {ex.Message}");
        }
    }

    private void LoadServerList()
    {
        FileLog.Write("[McpServersDialog] LoadServerList");

        var configPath = GetCurrentConfigPath();
        _servers = _configManager.LoadServers(configPath);

        _suppressSelectionChanged = true;
        _serverNames.Clear();
        foreach (var server in _servers)
            _serverNames.Add(server.Name);
        _suppressSelectionChanged = false;

        if (_serverNames.Count > 0)
            ServerList.SelectedIndex = 0;
        else
            ClearEditForm();

        StatusText.Text = "";
    }

    private void ServerList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;

        try
        {
            var index = ServerList.SelectedIndex;
            if (index < 0 || index >= _servers.Count)
            {
                ClearEditForm();
                return;
            }

            var server = _servers[index];
            _selectedOriginalName = server.Name;
            FileLog.Write($"[McpServersDialog] ServerList_SelectionChanged: {server.Name}");

            EditPanel.IsEnabled = true;
            NameInput.Text = server.Name;
            CommandInput.Text = server.Command;
            UrlInput.Text = server.Url ?? "";

            TransportCombo.SelectedItem = server.TransportType;
            UpdateTransportVisibility(server.TransportType);

            ArgsInput.Text = string.Join(Environment.NewLine, server.Args);

            var envLines = server.Env.Select(kvp => $"{kvp.Key}={kvp.Value}");
            EnvInput.Text = string.Join(Environment.NewLine, envLines);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] ServerList_SelectionChanged FAILED: {ex.Message}");
        }
    }

    private void ClearEditForm()
    {
        EditPanel.IsEnabled = false;
        _selectedOriginalName = null;
        NameInput.Text = "";
        CommandInput.Text = "";
        UrlInput.Text = "";
        TransportCombo.SelectedIndex = 0;
        ArgsInput.Text = "";
        EnvInput.Text = "";
        UpdateTransportVisibility("stdio");
    }

    private void TransportCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var transport = TransportCombo.SelectedItem as string ?? "stdio";
        UpdateTransportVisibility(transport);
    }

    private void UpdateTransportVisibility(string transport)
    {
        if (StdioPanel == null || SsePanel == null) return;

        StdioPanel.IsVisible = transport != "sse";
        SsePanel.IsVisible = transport == "sse";
    }

    private void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[McpServersDialog] BtnAdd_Click");

            var baseName = "new-server";
            var name = baseName;
            var counter = 1;
            while (_servers.Any(s => s.Name == name))
            {
                name = $"{baseName}-{counter}";
                counter++;
            }

            var entry = new McpServerConfig
            {
                Name = name,
                Command = "npx",
                TransportType = "stdio"
            };

            _servers.Add(entry);
            _serverNames.Add(entry.Name);
            ServerList.SelectedIndex = _serverNames.Count - 1;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] BtnAdd_Click FAILED: {ex.Message}");
        }
    }

    private void BtnRemove_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var index = ServerList.SelectedIndex;
            if (index < 0 || index >= _servers.Count) return;

            var name = _servers[index].Name;
            FileLog.Write($"[McpServersDialog] BtnRemove_Click: {name}");

            _servers.RemoveAt(index);
            _serverNames.RemoveAt(index);

            if (_serverNames.Count > 0)
                ServerList.SelectedIndex = Math.Min(index, _serverNames.Count - 1);
            else
                ClearEditForm();

            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] BtnRemove_Click FAILED: {ex.Message}");
        }
    }

    private void BtnImportDesktop_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[McpServersDialog] BtnImportDesktop_Click");

            if (!File.Exists(McpConfigManager.DesktopPath))
            {
                FileLog.Write($"[McpServersDialog] Desktop config not found: {McpConfigManager.DesktopPath}");
                return;
            }

            var imported = _configManager.ImportFromClaudeDesktop();
            if (imported.Count == 0)
            {
                FileLog.Write("[McpServersDialog] No servers found in Claude Desktop config");
                return;
            }

            var addedCount = 0;
            foreach (var server in imported)
            {
                if (_servers.Any(s => s.Name == server.Name))
                {
                    FileLog.Write($"[McpServersDialog] Import: skipping duplicate '{server.Name}'");
                    continue;
                }

                _servers.Add(server);
                _serverNames.Add(server.Name);
                addedCount++;
            }

            if (addedCount > 0 && ServerList.SelectedIndex < 0)
                ServerList.SelectedIndex = 0;

            FileLog.Write($"[McpServersDialog] Imported {addedCount} of {imported.Count} servers");
            StatusText.Text = addedCount > 0 ? $"Imported {addedCount} server(s)" : "All servers already exist";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] BtnImportDesktop_Click FAILED: {ex.Message}");
        }
    }

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[McpServersDialog] BtnSave_Click");

            ApplyFormToSelectedServer();

            var names = _servers.Select(s => s.Name).ToList();
            var duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                FileLog.Write($"[McpServersDialog] Duplicate names: {string.Join(", ", duplicates)}");
                StatusText.Text = $"Duplicate names: {string.Join(", ", duplicates)}";
                return;
            }

            if (_servers.Any(s => string.IsNullOrWhiteSpace(s.Name)))
            {
                FileLog.Write("[McpServersDialog] Empty server name");
                // shown-error-exempt (user input): the user left a server's name empty
                StatusText.Text = "Server name cannot be empty";
                return;
            }

            var configPath = GetCurrentConfigPath();
            _configManager.SaveServers(configPath, _servers);

            StatusText.Text = "Saved";
            FileLog.Write("[McpServersDialog] BtnSave_Click: complete");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[McpServersDialog] BtnSave_Click FAILED: {ex.Message}");
        }
    }

    private void ApplyFormToSelectedServer()
    {
        var index = ServerList.SelectedIndex;
        if (index < 0 || index >= _servers.Count) return;

        var server = _servers[index];
        var newName = NameInput.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(newName)) return;

        server.Name = newName;
        server.TransportType = TransportCombo.SelectedItem as string ?? "stdio";
        server.Command = CommandInput.Text?.Trim() ?? "";
        server.Url = string.IsNullOrWhiteSpace(UrlInput.Text) ? null : UrlInput.Text.Trim();

        server.Args = (ArgsInput.Text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .ToList();

        server.Env.Clear();
        var envLines = (EnvInput.Text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        foreach (var line in envLines)
        {
            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0) continue;
            var key = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..].Trim();
            if (key.Length > 0)
                server.Env[key] = value;
        }

        _suppressSelectionChanged = true;
        _serverNames[index] = newName;
        _suppressSelectionChanged = false;

        FileLog.Write($"[McpServersDialog] ApplyFormToSelectedServer: {server.Name}");
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[McpServersDialog] BtnClose_Click");
        Close();
    }
}
