namespace CcDirector.Gateway.Pairing;

/// <summary>
/// ONE NUMBER THAT MOVES EVERY TIME ANYTHING A DEVICE KEY CHECK READS IS WRITTEN (issue #4409). <see cref="DeviceRegistry"/>
/// remembers a key it judged active, and trusts that memory only while this number is still the one it read under.
///
/// A device key check reads three tables: the key's own row (<c>device_credentials</c>), whether its account owns the
/// account space it is bound to (<c>tenants</c>), and, for a team key, whether its person is still a member allowed to run
/// sessions (<c>team_members</c>). A revoke, a re-enrollment, an ownership change and a removal from a team each write one
/// of them, through the registry, through other stores, and through bulk statements that never pass SaveChanges. So the
/// number is moved where every one of those ends: the database command itself (<see cref="Data.CredentialTableWriteInterceptor"/>),
/// and again when a transaction that wrote one of them commits or rolls back.
///
/// One number for every key, not one per key: these tables are written a few times an hour and read several times a
/// second, so forgetting every remembered key on any write costs a handful of reads and can never miss a dependency.
/// Process-wide, so it cannot see a write made by ANOTHER Gateway process - which is what
/// <see cref="DeviceRegistry.RememberedKeyMaxAge"/> bounds.
/// </summary>
internal static class DeviceCredentialGeneration
{
    private static long _generation;

    /// <summary>The current number. Read it BEFORE reading the database, so a write that lands during the read moves
    /// the number past what is remembered.</summary>
    internal static long Current => Interlocked.Read(ref _generation);

    /// <summary>Move the number, so every key remembered under the old one is checked against the database again.</summary>
    internal static void Bump() => Interlocked.Increment(ref _generation);
}
