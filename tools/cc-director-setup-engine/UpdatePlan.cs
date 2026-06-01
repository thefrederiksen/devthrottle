namespace CcDirector.Setup.Engine;

/// <summary>What the planner decided to do with a single component.</summary>
public enum PlanItemKind
{
    /// <summary>Component is absent and the manifest has its asset - install it.</summary>
    Install,

    /// <summary>Component is present but the manifest asset version is newer - update it.</summary>
    Update,

    /// <summary>Component is present and at (or ahead of) the manifest version.</summary>
    UpToDate,

    /// <summary>The manifest has no asset for this component - cannot install/update.</summary>
    MissingAsset,

    /// <summary>The manifest version is pinned (rolled back from) - intentionally skipped.</summary>
    Pinned,
}

/// <summary>The planner's decision for one component.</summary>
/// <param name="ComponentId">Component id.</param>
/// <param name="Kind">The decision.</param>
/// <param name="AssetName">Release asset filename (empty when MissingAsset has no candidate).</param>
/// <param name="FromVersion">Installed version, or null if absent/unknown.</param>
/// <param name="ToVersion">Target version from the manifest, or null when MissingAsset.</param>
/// <param name="Sha256">Expected SHA-256 of the target asset (empty when MissingAsset).</param>
public sealed record PlanItem(
    string ComponentId,
    PlanItemKind Kind,
    string AssetName,
    string? FromVersion,
    string? ToVersion,
    string Sha256)
{
    /// <summary>True if this item requires downloading + applying a build.</summary>
    public bool IsActionable => Kind is PlanItemKind.Install or PlanItemKind.Update;
}

/// <summary>The full set of per-component decisions for one plan run.</summary>
public sealed class UpdatePlan
{
    public required IReadOnlyList<PlanItem> Items { get; init; }

    /// <summary>Items that require a download + apply (Install or Update).</summary>
    public IReadOnlyList<PlanItem> Actionable => Items.Where(i => i.IsActionable).ToList();

    public IReadOnlyList<PlanItem> ToInstall => Items.Where(i => i.Kind == PlanItemKind.Install).ToList();
    public IReadOnlyList<PlanItem> ToUpdate => Items.Where(i => i.Kind == PlanItemKind.Update).ToList();
    public IReadOnlyList<PlanItem> MissingAssets => Items.Where(i => i.Kind == PlanItemKind.MissingAsset).ToList();

    public bool HasWork => Actionable.Count > 0;
}
