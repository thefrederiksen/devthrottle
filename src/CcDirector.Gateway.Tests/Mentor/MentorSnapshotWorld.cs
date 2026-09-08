using System.Globalization;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Mentor;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>The tests that restore the W36 snapshot share one restore per run: they only read the world, each through
/// its own store and its own run folder.</summary>
[CollectionDefinition(Name)]
public sealed class MentorSnapshotCollection : ICollectionFixture<MentorSnapshotWorld>
{
    public const string Name = "mentor-snapshot";
}

/// <summary>
/// THE TEST WORLD of the C# mentor port (Mentor on the Gateway, Phase B, ruling R2), shared by the parity test and
/// the assemble parity test. The W36 snapshot is restored INTO the Gateway's own stores: the daily prompt-log files
/// are copied into a temporary prompt-log root under tenants/&lt;tenant id&gt;/, the three tables are inserted through
/// EF into a per-run Postgres database, and the pulled turn-log corpus is the turn-log root. A store is then built
/// from the TenantId, the GatewayPromptLog, a context factory, the corpus root, the zone, the business hours and the
/// manifest's extract times, and reads through the Gateway's readers only. Production is never touched.
///
/// The restore is LAZY and idempotent (<see cref="Restore"/>): the fixture is constructed for every run of the
/// collection, including runs where the environment variables are unset and every test skips, so nothing heavy
/// happens until a test that has checked its variables asks for the world. The temp root and the database are
/// removed when the collection ends.
/// </summary>
public sealed class MentorSnapshotWorld : IDisposable
{
    public const string PgVar = PostgresProofDatabase.ConnectionEnvVar;
    public const string SnapshotVar = "CC_MENTOR_SNAPSHOT_ROOT";
    public const string TurnLogVar = "CC_MENTOR_TURN_LOG_ROOT";

    public const string Label = "soren";
    public const string TenantValue = "9f19679f-2e19-41a7-9acf-8cae7a8a59cc";
    public const string Week = "2026-W36";
    public const string ZoneId = "America/Toronto";

    /// <summary>The reference's oracle numbers for soren / 2026-W36 (fix round 4, never 1903 / 213 / 1889 / 1887 / 99208).</summary>
    public const int OracleHumanPrompts = 1888;
    public const int OracleHumanWords = 99440;
    public const int OracleSessions = 203;
    public const int OracleTornLines = 15;

    private readonly object _gate = new();
    private bool _restored;
    private string? _connection;

    public TenantId Tenant { get; } = new(TenantValue);
    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "mentor-snapshot-" + Guid.NewGuid().ToString("N"));
    public string SnapshotRoot => Env(SnapshotVar);
    public string TurnLogRoot => Env(TurnLogVar);
    public GatewayPromptLog PromptLog { get; }
    public LocalZone Zone { get; } = LocalZone.FromIana(ZoneId);
    public SourceEnds? SourceEnds { get; private set; }
    public int CopiedFiles { get; private set; }
    public int InsertedRows { get; private set; }

    public MentorSnapshotWorld()
    {
        PromptLog = new GatewayPromptLog(TempRoot);
    }

    /// <summary>The first unset variable among the three the world needs and <paramref name="more"/>, or null when all are set.</summary>
    public static string? MissingVariable(params string[] more)
    {
        foreach (var name in new[] { PgVar, SnapshotVar, TurnLogVar }.Concat(more))
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) return name;
        return null;
    }

    public static string Env(string name)
        => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Set " + name + " to run the mentor snapshot tests.");

    /// <summary>This world's own throwaway database: the operator's template with its own suffix, so the proof classes
    /// sharing the per-process database are never touched and the drop at the end is ours alone.</summary>
    private static string OwnConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresProofDatabase.Connection);
        builder.Database = builder.Database + "_mentor";
        return builder.ConnectionString;
    }

    public GatewayDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_connection ?? throw new InvalidOperationException("Restore the world before opening a context."), npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
            })
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = Tenant.Value };
    }

    /// <summary>Restore the snapshot into the Gateway's stores, once; every later call answers immediately.</summary>
    public void Restore(ITestOutputHelper output)
    {
        lock (_gate)
        {
            if (_restored) return;
            var rawDir = Path.Combine(SnapshotRoot, "accounts", Label, "raw");
            var promptLogSource = Path.Combine(rawDir, "prompt-log");
            var dbDir = Path.Combine(rawDir, "db");
            Assert.True(Directory.Exists(promptLogSource), "snapshot prompt-log folder not found: " + promptLogSource);
            Assert.True(Directory.Exists(dbDir), "snapshot db folder not found: " + dbDir);
            Assert.True(Directory.Exists(TurnLogRoot), "turn-log corpus root not found: " + TurnLogRoot);
            Assert.Equal(ZoneId, Zone.Zone.Id);   // resolved by its IANA id; no Windows-id conversion was needed

            // 1. The prompt log: the snapshot's daily files, copied into the tenant's partition of a fresh root.
            var partition = PromptLog.DirectoryFor(Tenant);
            Directory.CreateDirectory(partition);
            foreach (var path in Directory.GetFiles(promptLogSource, "conversation-*.jsonl"))
            {
                File.Copy(path, Path.Combine(partition, Path.GetFileName(path)));
                CopiedFiles++;
            }
            output.WriteLine("prompt log: " + CopiedFiles + " daily files copied into the tenant partition");

            // 2. The three tables, through EF, into this world's own per-run Postgres database.
            _connection = OwnConnection();
            Assert.StartsWith("ccpg", new NpgsqlConnectionStringBuilder(_connection).Database, StringComparison.OrdinalIgnoreCase);
            using (var ctx = NewContext())
            {
                ctx.Database.EnsureDeleted();
                ctx.Database.Migrate();
            }
            InsertedRows = InsertTable<SessionHistoryEntity>(Path.Combine(dbDir, "session_history.jsonl"), (c, rows) => c.SessionHistory.AddRange(rows))
                + InsertTable<ActivityEventEntity>(Path.Combine(dbDir, "activity_events.jsonl"), (c, rows) => c.ActivityEvents.AddRange(rows))
                + InsertTable<DictationTranscriptEntity>(Path.Combine(dbDir, "dictation_transcripts.jsonl"), (c, rows) => c.DictationTranscripts.AddRange(rows));
            output.WriteLine("database: " + InsertedRows + " rows inserted");

            // 3. The extract times the store is bound to: the manifest's, as the reference reads them.
            SourceEnds = SourceEndsFromManifest(Path.Combine(SnapshotRoot, "manifest.jsonl"));
            _restored = true;
        }
    }

    /// <summary>A store bound to the tenant and the week over the restored world (zone America/Toronto, business hours 8-18,
    /// the manifest's extract times, label soren).</summary>
    public MentorStore NewStore()
    {
        if (!_restored) throw new InvalidOperationException("Restore the world before building a store.");
        return new MentorStore(Tenant, PromptLog, NewContext, TurnLogRoot, Zone, new BusinessHours(8, 18), SourceEnds!, Week, Label);
    }

    /// <summary>A fresh run folder under the temp root.</summary>
    public string NewRunDir(string name)
    {
        var runDir = Path.Combine(TempRoot, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(runDir);
        return runDir;
    }

    private int InsertTable<T>(string path, Action<GatewayDbContext, List<T>> add) where T : TenantScopedEntity
    {
        Assert.True(File.Exists(path), "snapshot table file not found: " + path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };
        var batch = new List<T>();
        var total = 0;
        var number = 0;
        foreach (var line in File.ReadLines(path, new UTF8Encoding(false)))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entity = JsonSerializer.Deserialize<T>(line, options)
                ?? throw new MentorDataException(Path.GetFileName(path) + ":" + number + " is not a row.");
            entity.TenantId = Tenant.Value;
            batch.Add(entity);
            if (batch.Count == 2000)
            {
                total += Flush(batch, add);
                batch.Clear();
            }
        }
        if (batch.Count > 0) total += Flush(batch, add);
        return total;
    }

    private int Flush<T>(List<T> batch, Action<GatewayDbContext, List<T>> add) where T : TenantScopedEntity
    {
        using var ctx = NewContext();
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;
        add(ctx, batch);
        return ctx.SaveChanges();
    }

    /// <summary>The reference's metrics.source_extract_times over the manifest's LAST line per source for the account: the
    /// database sources' cutoff, and for the prompt log the end of the last complete UTC day file (window.end plus one
    /// day at 00:00 UTC).</summary>
    private static SourceEnds SourceEndsFromManifest(string manifestPath)
    {
        Assert.True(File.Exists(manifestPath), "no manifest at " + manifestPath);
        var last = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(manifestPath, new UTF8Encoding(false)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = (Dictionary<string, object?>)JsonValues.Parse(line)!;
            if ((string?)record["account"] != Label) continue;
            last[(string)record["source"]!] = record;
        }
        DateTime Cutoff(string source)
        {
            Assert.True(last.ContainsKey(source), "no " + source + " manifest line for account '" + Label + "'");
            var cutoff = (string)last[source]["cutoff"]!;
            return DateTime.Parse(cutoff, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        Assert.True(last.ContainsKey("prompt-log"), "no prompt-log manifest line for account '" + Label + "'");
        var window = (Dictionary<string, object?>)last["prompt-log"]["window"]!;
        var endDay = DateTime.ParseExact((string)window["end"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new SourceEnds(endDay.AddDays(1), Cutoff("db.session_history"), Cutoff("db.activity_events"), Cutoff("db.dictation_transcripts"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); } catch (IOException) { /* the temp root is per run */ }
        if (_connection is null) return;
        using var ctx = NewContext();
        ctx.Database.EnsureDeleted();
    }
}
