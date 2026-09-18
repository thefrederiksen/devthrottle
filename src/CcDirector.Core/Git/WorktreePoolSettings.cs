using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Whether sessions opened in one repository run in a pooled worktree, and how many slots that pool
/// may have.
/// </summary>
/// <param name="Enabled">
/// False is the default and means today's behaviour, byte for byte: the session runs in the checkout
/// it was opened in and no pool is consulted.
/// </param>
/// <param name="PoolSize">
/// The most slots the pool may hold for this repository. The default is
/// <see cref="WorktreePoolSettings.DefaultPoolSize"/>.
/// </param>
public sealed record WorktreePoolSetting(bool Enabled, int PoolSize);

/// <summary>
/// Reads and writes the per-repository pooled-worktree setting in <c>config.json</c>, under
/// <c>worktreePool.repoDefaults[&lt;repoKey&gt;]</c>.
///
/// It follows <see cref="Browsers.BrowserDefaultStore"/> exactly - same file, same
/// <see cref="CcDirectorConfigService.MergePatch"/> writes so no other section is disturbed, same
/// repository-key normalization - rather than inventing a store of its own. There is one deliberate
/// difference: there is no application-wide level to fall back to. Handing every repository on the
/// machine a pool because one switch was left on is not a default anybody would choose on purpose,
/// and the whole point of the setting is that a repository opts in.
/// </summary>
public static class WorktreePoolSettings
{
    /// <summary>
    /// The pool size a repository gets when it turns the setting on without naming one.
    ///
    /// FOUR, not the twelve the research suggested (Architect ruling): a slot is a full checkout on
    /// disk, and this repository's own worktrees measured 153 GB across 49 of them. Four is the
    /// default; the setting is there for anyone who wants more.
    /// </summary>
    public const int DefaultPoolSize = 4;

    /// <summary>The setting a repository that has never been configured has: off, and four slots if turned on.</summary>
    public static readonly WorktreePoolSetting Default = new(Enabled: false, PoolSize: DefaultPoolSize);

    /// <summary>
    /// The setting for <paramref name="repoPath"/>. A repository that has never been configured - and
    /// a blank path, and a stored value that cannot be read - comes back as <see cref="Default"/>,
    /// which is OFF. Nothing about this setting fails open.
    /// </summary>
    public static WorktreePoolSetting For(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            return Default;

        var key = NormalizeRepoKey(repoPath);
        JsonNode? stored;
        try
        {
            stored = CcDirectorConfigService.ReadRaw()["worktreePool"]?["repoDefaults"]?[key];
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreePoolSettings] For: could not read config for repo={key}: {ex.Message}; using the default (off)");
            return Default;
        }

        if (stored is not JsonObject setting)
            return Default;

        var enabled = ReadBool(setting["enabled"]);
        var poolSize = ReadPoolSize(setting["poolSize"]);
        FileLog.Write($"[WorktreePoolSettings] For: repo={key}, enabled={enabled}, poolSize={poolSize}");
        return new WorktreePoolSetting(enabled, poolSize);
    }

    /// <summary>
    /// Persists <paramref name="setting"/> for <paramref name="repoPath"/>. Other repositories'
    /// settings and every other section of <c>config.json</c> are untouched.
    /// </summary>
    public static void Save(string repoPath, WorktreePoolSetting setting)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("Repository path is required", nameof(repoPath));
        if (setting is null)
            throw new ArgumentNullException(nameof(setting));
        if (setting.PoolSize < 1)
            throw new ArgumentOutOfRangeException(nameof(setting), setting.PoolSize, "The pool size must be at least 1");

        var key = NormalizeRepoKey(repoPath);
        FileLog.Write($"[WorktreePoolSettings] Save: repo={key}, enabled={setting.Enabled}, poolSize={setting.PoolSize}");
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["worktreePool"] = new JsonObject
            {
                ["repoDefaults"] = new JsonObject
                {
                    [key] = new JsonObject
                    {
                        ["enabled"] = setting.Enabled,
                        ["poolSize"] = setting.PoolSize,
                    }
                }
            }
        });
    }

    /// <summary>Forgets <paramref name="repoPath"/>'s setting, so it is back to <see cref="Default"/> (off).</summary>
    public static void Clear(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("Repository path is required", nameof(repoPath));

        var key = NormalizeRepoKey(repoPath);
        FileLog.Write($"[WorktreePoolSettings] Clear: repo={key}");
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["worktreePool"] = new JsonObject
            {
                ["repoDefaults"] = new JsonObject { [key] = null }
            }
        });
    }

    /// <summary>A value that is not a boolean is not a "yes". Missing, null, a string, a number: all off.</summary>
    private static bool ReadBool(JsonNode? node)
    {
        try
        {
            return node?.GetValue<bool>() ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A pool size that is missing, not a number, or below one is the default. A stored zero would
    /// mean "a pool that can never hand anything out", which nobody means to say and which would read
    /// as the tool being broken.
    /// </summary>
    private static int ReadPoolSize(JsonNode? node)
    {
        try
        {
            var value = node?.GetValue<int>();
            return value is int size && size >= 1 ? size : DefaultPoolSize;
        }
        catch (Exception)
        {
            return DefaultPoolSize;
        }
    }

    /// <summary>
    /// The same normalization <see cref="Browsers.BrowserDefaultStore"/> uses, so one repository maps
    /// to one stored entry however its path was typed.
    /// </summary>
    private static string NormalizeRepoKey(string repoPath)
    {
        var normalized = repoPath.Trim().Replace('/', '\\').TrimEnd('\\');
        return OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized;
    }
}
