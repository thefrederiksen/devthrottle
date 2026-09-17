using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Secrets;

/// <summary>
/// Reads one entry's value from the cc-secrets store (tools/cc-secrets), the only place the owner's
/// credentials live. The store is a plain JSON file private to this user:
/// <c>{ "version": 1, "entries": [ { "name", "kind", "secret", "envName", ... } ] }</c>.
///
/// The Director and the Gateway are programs, not a model, so reading the file directly is correct - an
/// agent goes through <c>cc-secrets run</c> instead and never sees a value. A value read here is never
/// logged: every log line names the entry and the store, nothing more.
/// </summary>
public static class SecretStoreReader
{
    /// <summary>The store format this reader understands; tools/cc-secrets/src/store.py STORE_VERSION.</summary>
    public const int StoreVersion = 1;

    /// <summary>Read the value of <paramref name="entryName"/> from this user's store.</summary>
    /// <exception cref="SecretNotAvailableException">The store, the entry, or its value is missing, or the store is unreadable.</exception>
    public static string ReadSecret(string entryName) => ReadSecret(CcStorage.SecretsStore(), entryName);

    /// <summary>Read the value of <paramref name="entryName"/> from the store file at <paramref name="storePath"/>.</summary>
    /// <exception cref="SecretNotAvailableException">The store, the entry, or its value is missing, or the store is unreadable.</exception>
    public static string ReadSecret(string storePath, string entryName)
    {
        FileLog.Write($"[SecretStoreReader] ReadSecret: entry={entryName}, store={storePath}");

        if (!File.Exists(storePath))
            throw Fail(entryName,
                $"The cc-secrets store was not found at {storePath}. Add the entry with: cc-secrets add {entryName} " +
                "(or import your existing key file with: cc-secrets import <file>).");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(storePath));
        }
        catch (JsonException ex)
        {
            throw Fail(entryName, $"The cc-secrets store at {storePath} is not valid JSON ({ex.Message}).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != StoreVersion)
                throw Fail(entryName,
                    $"The cc-secrets store at {storePath} is not in format version {StoreVersion}, which is the one this program reads.");

            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("name", out var name)
                        || name.ValueKind != JsonValueKind.String
                        || !string.Equals(name.GetString(), entryName, StringComparison.Ordinal))
                        continue;

                    var value = entry.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.String
                        ? secret.GetString()
                        : null;
                    if (string.IsNullOrEmpty(value))
                        throw Fail(entryName,
                            $"The cc-secrets entry '{entryName}' in {storePath} has no value. Replace it with: cc-secrets add {entryName} --replace");

                    FileLog.Write($"[SecretStoreReader] ReadSecret: entry={entryName} found");
                    return value;
                }
            }
        }

        throw Fail(entryName,
            $"The cc-secrets store at {storePath} has no entry named '{entryName}'. Add it with: cc-secrets add {entryName}");
    }

    private static SecretNotAvailableException Fail(string entryName, string message)
    {
        FileLog.Write($"[SecretStoreReader] ReadSecret FAILED: entry={entryName}: {message}");
        return new SecretNotAvailableException(message);
    }
}

/// <summary>An entry could not be read from the cc-secrets store. The message says how to fix it.</summary>
public sealed class SecretNotAvailableException : InvalidOperationException
{
    public SecretNotAvailableException(string message) : base(message) { }
}
