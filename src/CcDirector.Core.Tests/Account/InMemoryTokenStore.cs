using CcDirector.Core.Account;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// In-memory <see cref="IProtectedTokenStore"/> for unit tests. Lets the service logic
/// (logged-in check, refresh, logout, event recording) be tested cross-platform without touching
/// the Windows-only Data Protection store. The DPAPI store itself is tested separately in
/// <see cref="WindowsProtectedTokenStoreTests"/>.
/// </summary>
internal sealed class InMemoryTokenStore : IProtectedTokenStore
{
    private DevThrottleTokens? _tokens;

    public int SaveCount { get; private set; }

    public bool HasTokens => _tokens is not null;

    public void Save(DevThrottleTokens tokens)
    {
        _tokens = tokens;
        SaveCount++;
    }

    public DevThrottleTokens? Load() => _tokens;

    public void Clear() => _tokens = null;
}
