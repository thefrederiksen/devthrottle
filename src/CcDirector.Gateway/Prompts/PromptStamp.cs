using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// THE GATEWAY'S STAMP ON A PUSHED PROMPT RECORD (devthrottle_internal#2305). In a team's tenant every record must say
/// whose it is, so the Mentor reads a person's own prompts and nobody else's: the person comes from the CALLING KEY,
/// through the team caller resolver (devthrottle_internal#2311), and each record gets an id the Mentor can quote it by.
/// In a personal tenant nothing is stamped - the tenant already names the one person - and both fields are cleared, so
/// what a personal account stores is exactly what it stored before. Whatever a client sent in either field is never
/// kept, in either kind of tenant.
/// </summary>
public static class PromptStamp
{
    /// <summary>Stamp every record with <paramref name="personSubject"/> and a fresh Gateway id.</summary>
    public static IReadOnlyList<PromptRecord> ForTeam(IReadOnlyList<PromptRecord> records, string personSubject, Func<string>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(personSubject);
        var mint = newId ?? (() => Guid.NewGuid().ToString("N"));
        return records.Select(r => r with { PersonSubject = personSubject, PromptId = mint() }).ToList();
    }

    /// <summary>Clear both Gateway-owned fields: a personal tenant's records carry neither.</summary>
    public static IReadOnlyList<PromptRecord> ForPersonal(IReadOnlyList<PromptRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.Select(r => r.PersonSubject is null && r.PromptId is null ? r : r with { PersonSubject = null, PromptId = null }).ToList();
    }
}
