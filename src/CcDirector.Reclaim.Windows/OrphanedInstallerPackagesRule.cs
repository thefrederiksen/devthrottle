using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// Orphaned Windows installer packages: cached package files that no installed product and no
/// applied patch points at.
///
/// This is the largest single thing on the measured machine - 211 files and 27.9 gigabytes of a 58.8
/// gigabyte folder on 18 September 2026 - and it is the clearest example of the first proof kind.
/// Windows itself keeps the record of which cached package it needs to repair or uninstall each
/// thing it has installed. A package that record does not name cannot be asked for, and Windows will
/// not put it back.
///
/// It is also the rule that most needs to fail closed, which is why its three controls are the ones
/// the mission names. The comparison is "which of these files is in that list", and if the list
/// fails to load the answer is that every file is an orphan - a confident recommendation to delete
/// the entire package cache, which would leave the machine unable to repair or remove anything it
/// has installed. So the rule counts both sides and refuses to answer when either is empty.
/// </summary>
public sealed class OrphanedInstallerPackagesRule : IReclaimRule
{
    /// <summary>How old a package must be before this rule will offer it.</summary>
    public const int DefaultAgeGateDays = 30;

    private static readonly string[] PackageExtensions = [".msi", ".msp"];

    private readonly IInstallerRecordSource _records;
    private readonly string _packageFolderPath;

    /// <summary>
    /// Build the rule.
    /// </summary>
    /// <param name="records">Where the installer records are read from.</param>
    /// <param name="packageFolderPath">The package cache folder, normally C:\Windows\Installer.</param>
    /// <param name="ageGateDays">
    /// How old a package must be before it is offered. Thirty days by default, because one of the
    /// 211 orphans measured during the design had been written two days earlier.
    /// </param>
    public OrphanedInstallerPackagesRule(
        IInstallerRecordSource records,
        string packageFolderPath,
        int ageGateDays = DefaultAgeGateDays)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFolderPath);
        ArgumentOutOfRangeException.ThrowIfNegative(ageGateDays);

        _records = records;
        _packageFolderPath = packageFolderPath;
        AgeGateDays = ageGateDays;
    }

    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public string Id => "orphaned-windows-installer-packages";

    /// <summary>The rule's name as a person reads it.</summary>
    public string Name => "Orphaned Windows installer packages";

    /// <summary>A record the system keeps says nothing needs it.</summary>
    public ProofKind Proof => ProofKind.SystemRecord;

    /// <summary>What this rule removes.</summary>
    public string WhatItRemoves =>
        $"cached installer package files in {_packageFolderPath} that no installed product and no applied patch points at";

    /// <summary>Why removing it is safe.</summary>
    public string WhyItIsSafe =>
        "Windows records, for every installed product and every applied patch, the cached package it needs " +
        "to repair or uninstall that thing; a package no record names cannot be asked for";

    /// <summary>What is lost.</summary>
    public string WhatIsLost =>
        "nothing that is installed: these packages belong to products and patches that are no longer " +
        "installed, or to installs that left their cache behind";

    /// <summary>How to get it back.</summary>
    public string HowToGetItBack =>
        "from the holding folder until it is purged; after that only by obtaining the original installer again, " +
        "and for a package whose product is gone there is nothing left that would ask for it";

    /// <summary>How old a package must be before this rule will offer it.</summary>
    public int AgeGateDays { get; }

    /// <summary>Removing a file from the Windows package cache needs an administrator.</summary>
    public bool NeedsAdministrator => true;

    /// <summary>The command the owner runs, since this tool never raises itself to administrator.</summary>
    public string? CommandToRun =>
        "run cc-cleanup-storage from a command prompt opened with Run as administrator";

    /// <summary>The package cache folder this rule looks in.</summary>
    public string LooksIn => _packageFolderPath;

    /// <summary>Look, count, and report. It reads; it changes nothing.</summary>
    /// <param name="context">What is being asked about, and the moment the age gate is judged against.</param>
    public RuleAnswer Examine(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        FileLog.Write($"[OrphanedInstallerPackagesRule] Examine: folder={_packageFolderPath}");

        if (!Directory.Exists(_packageFolderPath))
        {
            return new RuleAnswer([], [],
                $"the package cache {_packageFolderPath} is not there, so this rule has nothing to compare against");
        }

        InstallerRecords records;
        try
        {
            records = _records.Read();
        }
        catch (PlatformNotSupportedException ex)
        {
            return new RuleAnswer([], [], ex.Message);
        }

        var referencedOnDisk = records.ReferencedPackagePaths.Count(File.Exists);
        var packages = PackageFilesIn(_packageFolderPath);

        var controls = new List<RuleControl>
        {
            // The three the mission names. Every one of them must be non-empty: a comparison with an
            // empty list on either side finds the same nothing that a clean machine finds.
            new("records-read", records.ReferencedPackagePaths.Count, MustNotBeEmpty: true),
            new("records-found-on-disk", referencedOnDisk, MustNotBeEmpty: true),
            new("candidates-examined", packages.Count, MustNotBeEmpty: true),

            // Counted and reported because it is the difference between the two readings, not
            // because the answer means nothing without it.
            new("product-records-read", records.ProductRecordsRead, MustNotBeEmpty: false),
            new("patch-records-read", records.PatchRecordsRead, MustNotBeEmpty: false)
        };

        var oldEnough = context.NowUtc.AddDays(-AgeGateDays);
        var candidates = new List<ReclaimCandidate>();
        var tooYoung = 0;

        foreach (var package in packages)
        {
            if (records.ReferencedPackagePaths.Contains(package.FullName)) continue;

            var written = new DateTimeOffset(package.LastWriteTimeUtc, TimeSpan.Zero);
            if (written > oldEnough)
            {
                tooYoung++;
                continue;
            }

            candidates.Add(new ReclaimCandidate(
                package.FullName,
                package.Length,
                written,
                "no installed product and no applied patch names this file, and it is older than the age gate"));
        }

        controls.Add(new RuleControl("orphans-too-young-to-offer", tooYoung, MustNotBeEmpty: false));

        FileLog.Write(
            $"[OrphanedInstallerPackagesRule] Examine done: candidates={candidates.Count}, " +
            $"examined={packages.Count}, referenced={records.ReferencedPackagePaths.Count}, tooYoung={tooYoung}");

        return new RuleAnswer(controls, candidates);
    }

    // Only the packages themselves, and only at the top of the folder, which is where Windows puts
    // them. The folders beneath it hold other things this rule says nothing about.
    private static List<FileInfo> PackageFilesIn(string folder) =>
        new DirectoryInfo(folder)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => PackageExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            .ToList();
}
