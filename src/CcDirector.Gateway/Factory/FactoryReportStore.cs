using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// The reports kept on the Factory Agents Reports tab: saved filters, one list per account, held in the account's
/// settings under <see cref="TenantSettingKeys.FactoryReports"/>. "Make a report from this" saves the filter; opening
/// a saved report reopens the same view. Nothing here runs on a schedule - that is not this build.
/// </summary>
public sealed class FactoryReportStore
{
    public const int MaxReports = 100;
    public const int MaxNameChars = 120;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly TenantSettingsStore _settings;

    public FactoryReportStore(TenantSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>The account's saved reports. A stored value that cannot be read is an error, never an empty list.</summary>
    public IReadOnlyList<SavedFactoryReport> List(TenantId tenant)
    {
        var raw = _settings.Get(tenant, TenantSettingKeys.FactoryReports);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<SavedFactoryReport>();
        return JsonSerializer.Deserialize<List<SavedFactoryReport>>(raw, Json)
               ?? throw new InvalidOperationException("The saved factory reports setting holds no list.");
    }

    public SavedFactoryReport? Find(TenantId tenant, string id) =>
        List(tenant).FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Keep a filter as a report. The filter is validated the same way the views validate it, so a saved report
    /// always reopens. Throws <see cref="FactoryViewValidationException"/> when refused.
    /// </summary>
    public SavedFactoryReport Save(TenantId tenant, SaveFactoryReportRequest request, string savedBy, DateTime nowUtc, TimeZoneInfo zone)
    {
        FileLog.Write($"[FactoryReportStore] Save: name={request?.Name}, factory={request?.Factory}, agent={request?.Agent}, outcome={request?.Outcome}, window={request?.Window}");
        if (request is null) throw new FactoryViewValidationException("A report body is required.");

        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) throw new FactoryViewValidationException("A report needs a name.");
        if (name.Length > MaxNameChars) throw new FactoryViewValidationException($"A report name is at most {MaxNameChars} characters.");

        var filter = FactoryAgentsFold.NormaliseFilter(request.Factory, request.Agent, request.Outcome);
        var window = FactoryAgentsFold.ResolveWindow(request.Window, request.FromUtc, request.ToUtc, nowUtc, FactoryAgentsFold.WindowLast7d, zone);
        var custom = window.Key == FactoryAgentsFold.WindowCustom;

        lock (_gate)
        {
            var list = List(tenant).ToList();
            if (list.Count >= MaxReports)
                throw new FactoryViewValidationException($"An account keeps at most {MaxReports} reports.");
            var report = new SavedFactoryReport(
                Guid.NewGuid().ToString("N")[..12], name, filter.Factory, filter.Agent, filter.Outcome,
                window.Key, custom ? window.FromUtc : null, custom ? window.ToUtc : null, savedBy, nowUtc);
            list.Add(report);
            _settings.Set(tenant, TenantSettingKeys.FactoryReports, JsonSerializer.Serialize(list, Json), nowUtc);
            FileLog.Write($"[FactoryReportStore] Save: id={report.Id}, count={list.Count}");
            return report;
        }
    }
}
