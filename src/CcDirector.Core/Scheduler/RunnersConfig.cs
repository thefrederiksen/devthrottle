using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Scheduler;

/// <summary>
/// On-disk config for the scheduler. Lives at
/// <c>%LOCALAPPDATA%\cc-director\config\director\runners.json</c>.
///
/// Adding a new runner is "edit JSON and restart Director" -- no rebuild
/// required. Schedules supported: <c>daily</c>, <c>weekdays</c>,
/// <c>everyMinutes</c>.
///
/// Args may use either absolute paths or repo-relative paths. Relative paths
/// are resolved by walking up from <see cref="AppContext.BaseDirectory"/>
/// looking for the file; this makes the default seed fresh-clone runnable
/// without hardcoding any specific clone location.
/// </summary>
public static class RunnersConfig
{
    public const string FileName = "runners.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Default config path: %LOCALAPPDATA%\cc-director\config\director\runners.json</summary>
    public static string DefaultPath() => Path.Combine(CcStorage.ToolConfig("director"), FileName);

    /// <summary>
    /// Load runners from <paramref name="path"/>. If the file is missing,
    /// write the default seed first. Disabled or unresolvable entries are
    /// logged and skipped.
    /// </summary>
    /// <param name="path">JSON file path. Defaults to <see cref="DefaultPath"/>.</param>
    /// <param name="repoSearchStart">Where to start the walk-up for relative
    /// arg paths. Defaults to <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="log">Optional logger sink. Defaults to <see cref="FileLog.Write"/>.</param>
    public static IReadOnlyList<RunnerRegistration> LoadOrSeed(
        string? path = null,
        string? repoSearchStart = null,
        Action<string>? log = null)
    {
        path ??= DefaultPath();
        repoSearchStart ??= AppContext.BaseDirectory;
        log ??= FileLog.Write;

        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DefaultSeedJson());
                log($"[RunnersConfig] Seeded default runners config at {path}");
            }
            catch (Exception ex)
            {
                log($"[RunnersConfig] Failed to seed default config at {path}: {ex.Message}");
                return Array.Empty<RunnerRegistration>();
            }
        }

        RunnersFile? file;
        try
        {
            var json = File.ReadAllText(path);
            file = JsonSerializer.Deserialize<RunnersFile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            log($"[RunnersConfig] Failed to parse {path}: {ex.Message}");
            return Array.Empty<RunnerRegistration>();
        }

        if (file?.Runners == null || file.Runners.Count == 0)
        {
            log($"[RunnersConfig] No runners declared in {path}");
            return Array.Empty<RunnerRegistration>();
        }

        var result = new List<RunnerRegistration>();
        foreach (var entry in file.Runners)
        {
            if (entry.Enabled == false)
            {
                log($"[RunnersConfig] Skipping disabled runner '{entry.Name}'");
                continue;
            }

            var reg = TryBuildRegistration(entry, repoSearchStart, log);
            if (reg != null) result.Add(reg);
        }
        return result;
    }

    private static RunnerRegistration? TryBuildRegistration(
        RunnerEntry entry,
        string repoSearchStart,
        Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            log("[RunnersConfig] Skipping runner with empty name");
            return null;
        }
        if (string.IsNullOrWhiteSpace(entry.QueueFilter))
        {
            log($"[RunnersConfig] Skipping '{entry.Name}': queueFilter missing");
            return null;
        }
        if (string.IsNullOrWhiteSpace(entry.Command))
        {
            log($"[RunnersConfig] Skipping '{entry.Name}': command missing");
            return null;
        }
        if (entry.Schedule == null)
        {
            log($"[RunnersConfig] Skipping '{entry.Name}': schedule missing");
            return null;
        }

        Schedule schedule;
        try
        {
            schedule = BuildSchedule(entry.Schedule);
        }
        catch (Exception ex)
        {
            log($"[RunnersConfig] Skipping '{entry.Name}': invalid schedule ({ex.Message})");
            return null;
        }

        var resolvedArgs = new List<string>();
        foreach (var raw in entry.Args ?? Array.Empty<string>())
        {
            var resolved = ResolveArg(raw, repoSearchStart);
            if (resolved == null)
            {
                log($"[RunnersConfig] Skipping '{entry.Name}': arg '{raw}' looks like a relative file path but was not found near {repoSearchStart}");
                return null;
            }
            resolvedArgs.Add(resolved);
        }

        return new RunnerRegistration
        {
            Name = entry.Name,
            QueueFilter = entry.QueueFilter,
            Command = entry.Command,
            Args = resolvedArgs.ToArray(),
            Schedule = schedule,
            RespectHumanCadence = entry.RespectHumanCadence ?? false,
            MinIntervalBetweenFires = TimeSpan.FromMinutes(entry.MinIntervalBetweenFiresMinutes ?? 30),
        };
    }

    private static Schedule BuildSchedule(ScheduleEntry entry)
    {
        var kind = (entry.Kind ?? "").Trim().ToLowerInvariant();
        return kind switch
        {
            "daily"        => Cron.Daily(entry.TimeOfDay ?? throw new InvalidOperationException("daily schedule requires timeOfDay")),
            "weekdays"     => Cron.Weekdays(entry.TimeOfDay ?? throw new InvalidOperationException("weekdays schedule requires timeOfDay")),
            "everyminutes" => Cron.EveryMinutes(entry.Minutes ?? throw new InvalidOperationException("everyMinutes schedule requires minutes")),
            _ => throw new InvalidOperationException($"unknown schedule kind '{entry.Kind}'"),
        };
    }

    /// <summary>
    /// Resolve an arg. If the value looks like a relative file path
    /// (contains '/' or '\' or ends with .py/.exe/.ps1 and is not absolute),
    /// walk up from <paramref name="searchStart"/> looking for it and return
    /// the resolved absolute path. Otherwise return the arg unchanged.
    /// Returns null if a relative file-looking arg cannot be resolved.
    /// </summary>
    internal static string? ResolveArg(string arg, string searchStart)
    {
        if (string.IsNullOrEmpty(arg)) return arg;

        if (Path.IsPathRooted(arg))
            return arg;

        bool looksLikeFilePath =
            arg.Contains('/') ||
            arg.Contains('\\') ||
            arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
            arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            arg.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeFilePath) return arg;

        var dir = new DirectoryInfo(searchStart);
        while (dir != null)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir.FullName, arg));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string DefaultSeedJson()
    {
        // New installs start with no runners. A runner's Args point at a script on the
        // user's own machine; seeding a personal example would reference a script that
        // does not exist after install (see issue #164). Users add their own runners.
        var seed = new RunnersFile
        {
            Runners = new List<RunnerEntry>(),
        };
        return JsonSerializer.Serialize(seed, JsonOptions);
    }

    public sealed class RunnersFile
    {
        public List<RunnerEntry>? Runners { get; set; }
    }

    public sealed class RunnerEntry
    {
        public string? Name { get; set; }
        public string? QueueFilter { get; set; }
        public string? Command { get; set; }
        public string[]? Args { get; set; }
        public ScheduleEntry? Schedule { get; set; }
        public bool? RespectHumanCadence { get; set; }
        public int? MinIntervalBetweenFiresMinutes { get; set; }
        public bool? Enabled { get; set; }
    }

    public sealed class ScheduleEntry
    {
        /// <summary>"daily" | "weekdays" | "everyMinutes"</summary>
        public string? Kind { get; set; }

        /// <summary>HH:mm -- used by daily/weekdays.</summary>
        public string? TimeOfDay { get; set; }

        /// <summary>Used by everyMinutes.</summary>
        public int? Minutes { get; set; }
    }
}
