using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Engine.Storage;

public sealed class EngineDatabase
{
    /// <summary>
    /// How long past its job's timeout an unfinished run is kept before anyone may fail it. Every
    /// executor kills a job at its timeout, so a run still open after timeout plus this margin is not
    /// in progress anywhere - whoever started it, and on whatever machine.
    /// </summary>
    public static readonly TimeSpan AbandonedRunGrace = TimeSpan.FromMinutes(5);

    public const string InterruptedRunMessage = "Interrupted by shutdown";
    public const string AbandonedRunMessage = "Abandoned: still unfinished long after the job timeout";

    private readonly string _connectionString;
    private readonly EngineRunOwner _owner;
    private readonly Func<EngineRunOwner, OwnerLiveness> _probeOwner;

    public EngineDatabase(string databasePath)
        : this(databasePath, EngineRunOwner.ForCurrentProcess(CcStorage.Root()), ProcessOwnerLiveness.Probe)
    {
    }

    /// <param name="owner">The Director process this instance runs in; stamped on every run it starts.</param>
    /// <param name="probeOwner">Decides whether another run's owner is still running.</param>
    public EngineDatabase(string databasePath, EngineRunOwner owner, Func<EngineRunOwner, OwnerLiveness> probeOwner)
    {
        FileLog.Write($"[EngineDatabase] Creating: path={databasePath}, owner pid={owner.ProcessId} machine={owner.Machine}");

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={databasePath}";
        _owner = owner;
        _probeOwner = probeOwner;
        InitializeSchema();
        MigrateRunOwnerColumns();
    }

    /// <summary>The Director process this instance stamps on the runs it starts.</summary>
    public EngineRunOwner Owner => _owner;

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var pragma = conn.CreateCommand();
        // busy_timeout: several Directors can share one engine.db, so a writer waits for the lock
        // with SQLite's own short back-off instead of failing or sleeping in coarse steps.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    private void InitializeSchema()
    {
        FileLog.Write("[EngineDatabase] InitializeSchema: creating tables if needed");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT UNIQUE NOT NULL,
                cron            TEXT NOT NULL,
                command         TEXT NOT NULL,
                working_dir     TEXT,
                enabled         INTEGER DEFAULT 1,
                timeout_seconds INTEGER DEFAULT 300,
                tags            TEXT,
                created_at      TEXT DEFAULT (datetime('now')),
                updated_at      TEXT DEFAULT (datetime('now')),
                next_run        TEXT
            );

            CREATE TABLE IF NOT EXISTS runs (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id           INTEGER NOT NULL,
                job_name         TEXT NOT NULL,
                started_at       TEXT NOT NULL,
                ended_at         TEXT,
                exit_code        INTEGER,
                stdout           TEXT,
                stderr           TEXT,
                timed_out        INTEGER DEFAULT 0,
                duration_seconds REAL,
                FOREIGN KEY (job_id) REFERENCES jobs(id)
            );

            CREATE TABLE IF NOT EXISTS communications (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                ticket_number    TEXT UNIQUE NOT NULL,
                platform         TEXT NOT NULL,
                type             TEXT,
                status           TEXT NOT NULL DEFAULT 'pending_review',
                subject          TEXT,
                body             TEXT,
                send_from        TEXT,
                persona          TEXT,
                recipient        TEXT,
                email_specific   TEXT,
                linkedin_specific TEXT,
                send_timing      TEXT DEFAULT 'immediate',
                scheduled_for    TEXT,
                created_at       TEXT DEFAULT (datetime('now')),
                approved_at      TEXT,
                posted_at        TEXT,
                posted_by        TEXT,
                tags             TEXT
            );

            CREATE TABLE IF NOT EXISTS media (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                communication_id INTEGER NOT NULL,
                type             TEXT,
                filename         TEXT,
                alt_text         TEXT,
                file_size        INTEGER,
                mime_type        TEXT,
                data             BLOB,
                FOREIGN KEY (communication_id) REFERENCES communications(id)
            );

            CREATE INDEX IF NOT EXISTS idx_runs_job_id ON runs(job_id);
            CREATE INDEX IF NOT EXISTS idx_runs_started_at ON runs(started_at);
            CREATE INDEX IF NOT EXISTS idx_jobs_next_run ON jobs(next_run);
            CREATE INDEX IF NOT EXISTS idx_jobs_enabled ON jobs(enabled);
            CREATE INDEX IF NOT EXISTS idx_comms_status ON communications(status);
            CREATE INDEX IF NOT EXISTS idx_comms_timing ON communications(send_timing);
            """;
        cmd.ExecuteNonQuery();

        FileLog.Write("[EngineDatabase] InitializeSchema: complete");
    }

    private static readonly (string Name, string Type)[] RunOwnerColumns =
    [
        ("owner_director", "TEXT"),
        ("owner_machine", "TEXT"),
        ("owner_pid", "INTEGER"),
        ("owner_process_started", "TEXT"),
    ];

    /// <summary>
    /// Forward migration: adds the run owner columns to a runs table made before owners were kept.
    /// Taken under a write lock, so two Directors opening one old file at once cannot both add them.
    /// Old rows keep null owners and are judged only by the abandoned-run rule.
    /// </summary>
    private void MigrateRunOwnerColumns()
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction(deferred: false);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.Transaction = tx;
            info.CommandText = "PRAGMA table_info(runs)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        var added = new List<string>();
        foreach (var (name, type) in RunOwnerColumns)
        {
            if (existing.Contains(name)) continue;

            using var alter = conn.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = $"ALTER TABLE runs ADD COLUMN {name} {type}";
            alter.ExecuteNonQuery();
            added.Add(name);
        }

        tx.Commit();

        if (added.Count > 0)
            FileLog.Write($"[EngineDatabase] MigrateRunOwnerColumns: added {string.Join(", ", added)}");
    }

    // -- Jobs --

    public int AddJob(JobRecord job)
    {
        FileLog.Write($"[EngineDatabase] AddJob: name={job.Name}, cron={job.Cron}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (name, cron, command, working_dir, enabled, timeout_seconds, tags, next_run)
            VALUES (@name, @cron, @command, @workingDir, @enabled, @timeout, @tags, @nextRun);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@name", job.Name);
        cmd.Parameters.AddWithValue("@cron", job.Cron);
        cmd.Parameters.AddWithValue("@command", job.Command);
        cmd.Parameters.AddWithValue("@workingDir", (object?)job.WorkingDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", job.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@timeout", job.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@tags", (object?)job.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nextRun", job.NextRun.HasValue ? job.NextRun.Value.ToString("o") : DBNull.Value);

        var id = Convert.ToInt32(cmd.ExecuteScalar());
        FileLog.Write($"[EngineDatabase] AddJob: id={id}");
        return id;
    }

    public JobRecord? GetJob(string name)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJobRecord(reader) : null;
    }

    public JobRecord? GetJobById(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJobRecord(reader) : null;
    }

    public List<JobRecord> ListJobs(bool includeDisabled = false, string? tag = null)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!includeDisabled)
            where.Add("enabled = 1");
        if (tag != null)
        {
            where.Add("tags LIKE @tag");
            cmd.Parameters.AddWithValue("@tag", $"%{tag}%");
        }

        cmd.CommandText = "SELECT * FROM jobs"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY name";

        using var reader = cmd.ExecuteReader();
        var jobs = new List<JobRecord>();
        while (reader.Read())
            jobs.Add(ReadJobRecord(reader));
        return jobs;
    }

    public void UpdateJob(JobRecord job)
    {
        FileLog.Write($"[EngineDatabase] UpdateJob: id={job.Id}, name={job.Name}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE jobs SET
                name = @name,
                cron = @cron,
                command = @command,
                working_dir = @workingDir,
                enabled = @enabled,
                timeout_seconds = @timeout,
                tags = @tags,
                next_run = @nextRun,
                updated_at = datetime('now')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", job.Id);
        cmd.Parameters.AddWithValue("@name", job.Name);
        cmd.Parameters.AddWithValue("@cron", job.Cron);
        cmd.Parameters.AddWithValue("@command", job.Command);
        cmd.Parameters.AddWithValue("@workingDir", (object?)job.WorkingDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", job.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@timeout", job.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@tags", (object?)job.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nextRun", job.NextRun.HasValue ? job.NextRun.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteJob(string name)
    {
        FileLog.Write($"[EngineDatabase] DeleteJob: name={name}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetJobEnabled(string name, bool enabled)
    {
        FileLog.Write($"[EngineDatabase] SetJobEnabled: name={name}, enabled={enabled}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET enabled = @enabled, updated_at = datetime('now') WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void UpdateNextRun(int jobId, DateTime? nextRun)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET next_run = @nextRun WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", jobId);
        cmd.Parameters.AddWithValue("@nextRun", nextRun.HasValue ? nextRun.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<JobRecord> GetDueJobs()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM jobs
            WHERE enabled = 1
              AND next_run IS NOT NULL
              AND next_run <= @now
            """;
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));

        using var reader = cmd.ExecuteReader();
        var jobs = new List<JobRecord>();
        while (reader.Read())
            jobs.Add(ReadJobRecord(reader));
        return jobs;
    }

    // -- Runs --

    /// <summary>Records a run started by this Director. The scheduler starts its runs with <see cref="TryClaimRun"/>.</summary>
    public int CreateRun(RunRecord run)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = InsertRunSql;
        AddInsertRunParameters(cmd, run.JobId, run.JobName, run.StartedAt);

        var id = Convert.ToInt32(cmd.ExecuteScalar());
        run.Owner = _owner;
        return id;
    }

    /// <summary>
    /// Claims a due job and starts its run in one write transaction, so of several Directors on one
    /// engine.db exactly one runs each due occurrence. The claim succeeds only while the job is
    /// enabled, due at <paramref name="nowUtc"/>, and has no unfinished run: the unfinished run IS the
    /// claim, and <see cref="CompleteRun"/> releases it and moves next_run in one transaction.
    /// Returns the started run, or null when the job is not due or another Director holds it.
    /// </summary>
    public RunRecord? TryClaimRun(int jobId, DateTime nowUtc)
    {
        using var conn = CreateConnection();
        // Not deferred = BEGIN IMMEDIATE: the write lock is taken before the check is read, so no
        // other connection can claim between our check and our insert.
        using var tx = conn.BeginTransaction(deferred: false);

        string? jobName;
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = """
                SELECT name FROM jobs
                WHERE id = @id
                  AND enabled = 1
                  AND next_run IS NOT NULL
                  AND next_run <= @now
                  AND NOT EXISTS (SELECT 1 FROM runs WHERE runs.job_id = jobs.id AND runs.ended_at IS NULL)
                """;
            check.Parameters.AddWithValue("@id", jobId);
            check.Parameters.AddWithValue("@now", nowUtc.ToString("o"));
            jobName = check.ExecuteScalar() as string;
        }

        if (jobName is null)
        {
            tx.Rollback();
            return null;
        }

        int runId;
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = InsertRunSql;
            AddInsertRunParameters(insert, jobId, jobName, nowUtc);
            runId = Convert.ToInt32(insert.ExecuteScalar());
        }

        tx.Commit();

        FileLog.Write($"[EngineDatabase] TryClaimRun: claimed job={jobName}, run={runId}, pid={_owner.ProcessId}");
        return new RunRecord { Id = runId, JobId = jobId, JobName = jobName, StartedAt = nowUtc, Owner = _owner };
    }

    /// <summary>
    /// Records a run's result and the job's next due time in one transaction. They must land
    /// together: between them the job would have no unfinished run and a past next_run, and another
    /// Director would claim the occurrence that just ran.
    /// </summary>
    public void CompleteRun(RunRecord run, DateTime? nextRun)
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction(deferred: false);

        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = UpdateRunSql;
            AddUpdateRunParameters(update, run);
            update.ExecuteNonQuery();
        }

        using (var next = conn.CreateCommand())
        {
            next.Transaction = tx;
            next.CommandText = "UPDATE jobs SET next_run = @nextRun WHERE id = @id";
            next.Parameters.AddWithValue("@id", run.JobId);
            next.Parameters.AddWithValue("@nextRun", nextRun.HasValue ? nextRun.Value.ToString("o") : DBNull.Value);
            next.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private const string InsertRunSql = """
        INSERT INTO runs (job_id, job_name, started_at, owner_director, owner_machine, owner_pid, owner_process_started)
        VALUES (@jobId, @jobName, @startedAt, @ownerDirector, @ownerMachine, @ownerPid, @ownerStarted);
        SELECT last_insert_rowid();
        """;

    private void AddInsertRunParameters(SqliteCommand cmd, int jobId, string jobName, DateTime startedAt)
    {
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.Parameters.AddWithValue("@jobName", jobName);
        cmd.Parameters.AddWithValue("@startedAt", startedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@ownerDirector", _owner.Director);
        cmd.Parameters.AddWithValue("@ownerMachine", _owner.Machine);
        cmd.Parameters.AddWithValue("@ownerPid", _owner.ProcessId);
        cmd.Parameters.AddWithValue("@ownerStarted", _owner.ProcessStartedAtUtc.ToString("o"));
    }

    public void UpdateRun(RunRecord run)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = UpdateRunSql;
        AddUpdateRunParameters(cmd, run);
        cmd.ExecuteNonQuery();
    }

    private const string UpdateRunSql = """
        UPDATE runs SET
            ended_at = @endedAt,
            exit_code = @exitCode,
            stdout = @stdout,
            stderr = @stderr,
            timed_out = @timedOut,
            duration_seconds = @duration
        WHERE id = @id
        """;

    private static void AddUpdateRunParameters(SqliteCommand cmd, RunRecord run)
    {
        cmd.Parameters.AddWithValue("@id", run.Id);
        cmd.Parameters.AddWithValue("@endedAt", run.EndedAt.HasValue ? run.EndedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("@exitCode", (object?)run.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stdout", (object?)run.Stdout ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stderr", (object?)run.Stderr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timedOut", run.TimedOut ? 1 : 0);
        cmd.Parameters.AddWithValue("@duration", (object?)run.DurationSeconds ?? DBNull.Value);
    }

    public RunRecord? GetRun(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM runs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRunRecord(reader) : null;
    }

    public List<RunRecord> ListRuns(string? jobName = null, int limit = 50, bool failedOnly = false)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (jobName != null)
        {
            where.Add("job_name = @jobName");
            cmd.Parameters.AddWithValue("@jobName", jobName);
        }
        if (failedOnly)
            where.Add("(exit_code IS NOT NULL AND exit_code != 0) OR timed_out = 1");

        cmd.CommandText = "SELECT * FROM runs"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY started_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        var runs = new List<RunRecord>();
        while (reader.Read())
            runs.Add(ReadRunRecord(reader));
        return runs;
    }

    public int CleanupOrphanedRuns() => CleanupOrphanedRuns(DateTime.UtcNow);

    /// <summary>
    /// Fails the unfinished runs that provably nobody is running, and leaves every other one alone -
    /// another live Director's run in progress above all. A run is failed when either:
    /// its owner is a process on this machine that is gone (no process has that id, or the one that
    /// does started at another time) - which is how this Director's own runs from a previous launch
    /// go; or it has been open longer than its job's timeout plus <see cref="AbandonedRunGrace"/>,
    /// which no executor allows. A run whose owner is running or undecidable (another machine, a
    /// process we may not inspect, a run recorded before owners were kept) is kept until it ages out.
    /// </summary>
    public int CleanupOrphanedRuns(DateTime nowUtc)
    {
        var open = new List<(int Id, DateTime StartedAt, int TimeoutSeconds, EngineRunOwner? Owner)>();
        using (var conn = CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT runs.id, runs.started_at, jobs.timeout_seconds,
                       runs.owner_director, runs.owner_machine, runs.owner_pid, runs.owner_process_started
                FROM runs JOIN jobs ON jobs.id = runs.job_id
                WHERE runs.ended_at IS NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                open.Add((
                    reader.GetInt32(0),
                    DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                    reader.GetInt32(2),
                    ReadOwner(reader, 3)));
            }
        }

        if (open.Count == 0)
            return 0;

        var count = 0;
        foreach (var run in open)
        {
            string? reason = null;
            if (run.Owner is not null && !IsThisProcess(run.Owner) && _probeOwner(run.Owner) == OwnerLiveness.Gone)
                reason = InterruptedRunMessage;
            else if (nowUtc - run.StartedAt > TimeSpan.FromSeconds(run.TimeoutSeconds) + AbandonedRunGrace)
                reason = AbandonedRunMessage;

            if (reason is null) continue;

            using var conn = CreateConnection();
            using var update = conn.CreateCommand();
            // Conditional on still being open: its owner may have finished it since we read it.
            update.CommandText = """
                UPDATE runs SET
                    ended_at = @endedAt,
                    exit_code = -1,
                    stderr = @reason,
                    duration_seconds = 0
                WHERE id = @id AND ended_at IS NULL
                """;
            update.Parameters.AddWithValue("@id", run.Id);
            update.Parameters.AddWithValue("@endedAt", nowUtc.ToString("o"));
            update.Parameters.AddWithValue("@reason", reason);
            count += update.ExecuteNonQuery();
        }

        FileLog.Write($"[EngineDatabase] CleanupOrphanedRuns: open={open.Count}, failed={count}, kept={open.Count - count}");
        return count;
    }

    private bool IsThisProcess(EngineRunOwner owner) =>
        owner.ProcessId == _owner.ProcessId
        && string.Equals(owner.Machine, _owner.Machine, StringComparison.OrdinalIgnoreCase)
        && Math.Abs((owner.ProcessStartedAtUtc - _owner.ProcessStartedAtUtc).TotalSeconds) < 1;

    public int CleanupOldRuns(int retentionDays)
    {
        FileLog.Write($"[EngineDatabase] CleanupOldRuns: purging runs older than {retentionDays} days");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM runs WHERE started_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-retentionDays).ToString("o"));
        var count = cmd.ExecuteNonQuery();

        FileLog.Write($"[EngineDatabase] CleanupOldRuns: purged {count} runs");
        return count;
    }

    // -- Readers --

    private static JobRecord ReadJobRecord(SqliteDataReader reader)
    {
        return new JobRecord
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Cron = reader.GetString(reader.GetOrdinal("cron")),
            Command = reader.GetString(reader.GetOrdinal("command")),
            WorkingDir = reader.IsDBNull(reader.GetOrdinal("working_dir")) ? null : reader.GetString(reader.GetOrdinal("working_dir")),
            Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
            TimeoutSeconds = reader.GetInt32(reader.GetOrdinal("timeout_seconds")),
            Tags = reader.IsDBNull(reader.GetOrdinal("tags")) ? null : reader.GetString(reader.GetOrdinal("tags")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            NextRun = reader.IsDBNull(reader.GetOrdinal("next_run")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("next_run")))
        };
    }

    private static RunRecord ReadRunRecord(SqliteDataReader reader)
    {
        return new RunRecord
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            JobId = reader.GetInt32(reader.GetOrdinal("job_id")),
            JobName = reader.GetString(reader.GetOrdinal("job_name")),
            StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
            EndedAt = reader.IsDBNull(reader.GetOrdinal("ended_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("ended_at"))),
            ExitCode = reader.IsDBNull(reader.GetOrdinal("exit_code")) ? null : reader.GetInt32(reader.GetOrdinal("exit_code")),
            Stdout = reader.IsDBNull(reader.GetOrdinal("stdout")) ? null : reader.GetString(reader.GetOrdinal("stdout")),
            Stderr = reader.IsDBNull(reader.GetOrdinal("stderr")) ? null : reader.GetString(reader.GetOrdinal("stderr")),
            TimedOut = reader.GetInt32(reader.GetOrdinal("timed_out")) == 1,
            DurationSeconds = reader.IsDBNull(reader.GetOrdinal("duration_seconds")) ? null : reader.GetDouble(reader.GetOrdinal("duration_seconds")),
            Owner = ReadOwner(reader, reader.GetOrdinal("owner_director"))
        };
    }

    /// <summary>
    /// Reads the four owner columns starting at ordinal <paramref name="first"/> (director, machine,
    /// pid, process start). Null for a run recorded before owners were kept.
    /// </summary>
    private static EngineRunOwner? ReadOwner(SqliteDataReader reader, int first)
    {
        if (reader.IsDBNull(first + 2))
            return null;

        return new EngineRunOwner(
            reader.GetString(first),
            reader.GetString(first + 1),
            reader.GetInt32(first + 2),
            DateTime.Parse(reader.GetString(first + 3), null, System.Globalization.DateTimeStyles.AdjustToUniversal));
    }
}
