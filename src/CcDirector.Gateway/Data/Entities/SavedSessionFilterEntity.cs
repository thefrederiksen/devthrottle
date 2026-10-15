namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One saved session filter on an account (issue #4009): a name, a colour, and the repos and machines it shows,
/// in the <c>saved_session_filters</c> table. The Cockpit lists these at the top of its filter panel and applies
/// one by name from the address bar (<c>?filter=&lt;name&gt;</c>), so a filter saved on one machine is there on
/// every machine and on the phone.
///
/// The primary key is (tenant_id, Id), with Id a generated identifier, so renaming a filter keeps its row. The name
/// is unique within an account by <see cref="NameKey"/> - the name trimmed and lower-cased - because the address bar
/// names a filter by its name and two filters that differ only by case would be one name to a person.
///
/// The repos and machines are stored as JSON arrays of strings. They are read and written whole, never queried, so
/// a child table would buy nothing.
/// </summary>
public sealed class SavedSessionFilterEntity : TenantScopedEntity
{
    /// <summary>The generated identifier (32 lower-case hexadecimal characters). Part of the primary key.</summary>
    public string Id { get; set; } = "";

    /// <summary>The name as the person typed it, trimmed.</summary>
    public string Name { get; set; } = "";

    /// <summary>The name trimmed and lower-cased: what makes a name unique within an account.</summary>
    public string NameKey { get; set; } = "";

    /// <summary>One of the colour keys in <c>SavedSessionFilterStore.Colours</c>.</summary>
    public string Colour { get; set; } = "";

    /// <summary>The repos the filter shows, as a JSON array of repo names. An empty array means every repo.</summary>
    public string ReposJson { get; set; } = "[]";

    /// <summary>The machines the filter shows, as a JSON array of machine names. An empty array means every machine.</summary>
    public string MachinesJson { get; set; } = "[]";

    /// <summary>When the filter was first saved (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the filter was last changed (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
