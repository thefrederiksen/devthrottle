using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Indexing;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Background;

/// <summary>
/// The recommendations the background scan made, exactly as they are written to disk.
///
/// It holds the finished sentences and the headline numbers, and not the working behind them. The
/// rules look at the live machine - the registry, the caches - so the Director cannot make these
/// again without doing the looking itself, and it must not: it renders what was saved. The sentences
/// are the engine's and a screen prints them as they stand, critical rule 7 in CLAUDE.md.
/// </summary>
public sealed record SavedRecommendation
{
    /// <summary>What this file is.</summary>
    public const string FormatName = "cc-cleanup-storage-recommendation";

    /// <summary>The version this library writes and the only one it reads.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The format name, always <see cref="FormatName"/>.</summary>
    public required string Format { get; init; }

    /// <summary>The format version.</summary>
    public required int FormatVersion { get; init; }

    /// <summary>When the file was written.</summary>
    public required DateTimeOffset WrittenUtc { get; init; }

    /// <summary>The folder the recommendations are about.</summary>
    public required string RootPath { get; init; }

    /// <summary>The saved scan they were made against.</summary>
    public required string IndexPath { get; init; }

    /// <summary>"ok" or "broken": whether the recommendations can be believed at all.</summary>
    public required string Verdict { get; init; }

    /// <summary>Why they are a broken instrument, or null when they are not.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>How many rules ran.</summary>
    public required int RulesRun { get; init; }

    /// <summary>How many of those could not do their work.</summary>
    public required int RulesBroken { get; init; }

    /// <summary>How many items are offered across every rule.</summary>
    public required long ItemsOffered { get; init; }

    /// <summary>The bytes every working rule proved disposable, added together.</summary>
    public required long ReclaimableBytes { get; init; }

    /// <summary>The report itself: finished sentences, printed as they stand.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>Reads and writes the saved recommendations.</summary>
public static class SavedRecommendationStore
{
    private static readonly JsonSerializerOptions JsonFormat = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>The file one folder's recommendations are written to, named as its saved scan is.</summary>
    /// <param name="storeDirectory">The store folder.</param>
    /// <param name="rootPath">The folder the recommendations are about.</param>
    public static string PathFor(string storeDirectory, string rootPath) =>
        ScanIndexStore.PathFor(BackgroundScanStatusStore.RecommendationDirectory(storeDirectory), rootPath);

    /// <summary>Write the recommendations, whole or not at all.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="report">The recommendations.</param>
    /// <param name="indexPath">The saved scan they were made against.</param>
    /// <param name="writtenUtc">When the file is being written.</param>
    public static void Save(string path, RecommendationReport report, string indexPath, DateTimeOffset writtenUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        FileLog.Write($"[SavedRecommendationStore] Save: path={path}, root={report.Scan.Scan.RootPath}");

        var saved = new SavedRecommendation
        {
            Format = SavedRecommendation.FormatName,
            FormatVersion = SavedRecommendation.CurrentFormatVersion,
            WrittenUtc = writtenUtc,
            RootPath = report.Scan.Scan.RootPath,
            IndexPath = indexPath,
            Verdict = report.Verdict == ReportVerdict.Ok ? "ok" : "broken",
            BrokenReason = report.BrokenReason,
            RulesRun = report.Findings.Count,
            RulesBroken = report.BrokenRules.Count,
            ItemsOffered = report.ItemsOffered,
            ReclaimableBytes = report.ReclaimableBytes,
            Lines = report.Lines
        };

        WholeFileWriter.Write(path, JsonSerializer.Serialize(saved, JsonFormat));
        FileLog.Write($"[SavedRecommendationStore] Save done: path={path}, verdict={saved.Verdict}");
    }

    /// <summary>Read saved recommendations back.</summary>
    /// <param name="path">The file to read.</param>
    /// <exception cref="FileNotFoundException">There are no saved recommendations at that path.</exception>
    /// <exception cref="InvalidDataException">The file is not one this version can read.</exception>
    public static SavedRecommendation Load(string path)
    {
        FileLog.Write($"[SavedRecommendationStore] Load: path={path}");

        if (!File.Exists(path))
        {
            FileLog.Write($"[SavedRecommendationStore] Load FAILED: path={path}, reason=nothing is saved there");
            throw new FileNotFoundException(
                $"There are no saved recommendations at {path}. The background scan has not finished one.", path);
        }

        SavedRecommendation? saved;
        try
        {
            saved = JsonSerializer.Deserialize<SavedRecommendation>(File.ReadAllText(path), JsonFormat);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[SavedRecommendationStore] Load FAILED: path={path}, reason=not saved recommendations");
            throw new InvalidDataException($"The file {path} is not saved recommendations.", ex);
        }

        if (saved is null ||
            !string.Equals(saved.Format, SavedRecommendation.FormatName, StringComparison.Ordinal) ||
            saved.FormatVersion != SavedRecommendation.CurrentFormatVersion)
        {
            FileLog.Write($"[SavedRecommendationStore] Load FAILED: path={path}, reason=wrong format or version");
            throw new InvalidDataException(
                $"The file {path} is not {SavedRecommendation.FormatName} version " +
                $"{SavedRecommendation.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        FileLog.Write($"[SavedRecommendationStore] Load done: path={path}, root={saved.RootPath}");
        return saved;
    }
}
