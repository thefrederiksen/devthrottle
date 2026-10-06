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

    /// <summary>Whether a process (a Director, or a command one started) is still running, by this database's probe.</summary>
    public OwnerLiveness ProbeOwner(EngineRunOwner process) => _probeOwner(process);

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

    private static readonly (string Name, string Type)[] RunClaimColumns =
    [
        // The Director process that claimed the run.
        ("owner_director", "TEXT"),
        ("owner_machine", "TEXT"),
        ("owner_pid", "INTEGER"),
        ("owner_process_started", "TEXT"),
        // The timeout the run was claimed with; the job's own timeout may be edited while it runs.
        ("run_timeout_seconds", "INTEGER"),
        // The command process the run started, which must be proven gone before the claim is released.
        ("child_pid", "INTEGER"),
        ("child_process_started", "TEXT"),
        // Written just BEFORE the command is started. With no child recorded, it separates a run whose
        // command never started (no mark: nothing to wait for) from one whose command identity was lost.
        ("command_starting_at", "TEXT"),
        // A result reported by an owner whose claim had already been released; kept, never applied.
        ("late_completion", "TEXT"),
    ];

    /// <summary>
    /// Forward migration: adds the run claim columns to a runs table made before they existed.
    /// Taken under a write lock, so two Directors opening one old file at once cannot both add them.
    /// Old rows keep null owners and are judged only by the abandoned-run rule, against the timeout
    /// their job had when the database was migrated (the best record there is of what they ran with).
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
        foreach (var (name, type) in RunClaimColumns)
        {
            if (existing.Contains(name)) continue;

            using var alter = conn.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = $"ALTER TABLE runs ADD COLUMN {name} {type}";
            alter.ExecuteNonQuery();
            added.Add(name);
        }

        if (added.Contains("run_timeout_seconds"))
        {
            using var backfill = conn.CreateCommand();
            backfill.Transaction = tx;
            backfill.CommandText = """
                UPDATE runs SET run_timeout_seconds = (SELECT timeout_seconds FROM jobs WHERE jobs.id = runs.job_id)
                WHERE run_timeout_seconds IS NULL
                """;
            backfill.ExecuteNonQuery();
        }

        if (added.Contains("command_starting_at"))
        {
            // A run still open from before the starting mark existed may well have started its command;
            // the database cannot say it did not. Mark it as possibly started, so it is held to its
            // deadline unless a recorded command proves it gone - never released as "never started".
            using var mark = conn.CreateCommand();
            mark.Transaction = tx;
            mark.CommandText = "UPDATE runs SET command_starting_at = started_at WHERE ended_at IS NULL AND command_starting_at IS NULL";
            mark.ExecuteNonQuery();
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

        string jobName;
        int timeoutSeconds;
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = """
                SELECT name, timeout_seconds FROM jobs
                WHERE id = @id
                  AND enabled = 1
                  AND next_run IS NOT NULL
                  AND next_run <= @now
                  AND NOT EXISTS (SELECT 1 FROM runs WHERE runs.job_id = jobs.id AND runs.ended_at IS NULL)
                """;
            check.Parameters.AddWithValue("@id", jobId);
            check.Parameters.AddWithValue("@now", nowUtc.ToString("o"));
            using var reader = check.ExecuteReader();
            if (!reader.Read())
            {
                reader.Close();
                tx.Rollback();
                return null;
            }
            jobName = reader.GetString(0);
            timeoutSeconds = reader.GetInt32(1);
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

        FileLog.Write($"[EngineDatabase] TryClaimRun: claimed job={jobName}, run={runId}, pid={_owner.ProcessId}, timeout={timeoutSeconds}s");
        return new RunRecord
        {
            Id = runId, JobId = jobId, JobName = jobName, StartedAt = nowUtc, Owner = _owner, TimeoutSeconds = timeoutSeconds
        };
    }

    /// <summary>
    /// Marks that this run's command is about to start. Written BEFORE the process starts: a run without
    /// it provably never started a command; a run with it and no recorded command has lost the identity.
    /// </summary>
    public void MarkCommandStarting(int runId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE runs SET command_starting_at = @now WHERE id = @id AND ended_at IS NULL";
        cmd.Parameters.AddWithValue("@id", runId);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Run {runId} is no longer open; its command must not start");
    }

    /// <summary>Records the command process a run started, which must be proven gone before its claim is released.</summary>
    public void RecordRunChild(int runId, int processId, DateTime processStartedAtUtc)
    {
        FileLog.Write($"[EngineDatabase] RecordRunChild: run={runId}, childPid={processId}");

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE runs SET child_pid = @pid, child_process_started = @started WHERE id = @id AND ended_at IS NULL";
        cmd.Parameters.AddWithValue("@id", runId);
        cmd.Parameters.AddWithValue("@pid", processId);
        cmd.Parameters.AddWithValue("@started", processStartedAtUtc.ToString("o"));
        if (cmd.ExecuteNonQuery() != 1)
        {
            // The run was ended under us (its claim released): the command it started must not run on.
            throw new InvalidOperationException($"Run {runId} is no longer open; its command must be stopped");
        }
    }

    /// <summary>
    /// Records a run's result and the job's next due time in one transaction. They must land
    /// together: between them the job would have no unfinished run and a past next_run, and another
    /// Director would claim the occurrence that just ran.
    /// Fenced: it applies only while the run is still this Director's open claim. When the claim was
    /// released meanwhile (another Director proved this one gone), the result is kept as a late
    /// completion and neither the run's verdict nor the schedule changes. Returns whether it applied.
    /// </summary>
    public bool CompleteRun(RunRecord run, DateTime? nextRun) => EndOwnRun(run, moveSchedule: true, nextRun);

    /// <summary>
    /// Ends this Director's run without moving the schedule, so the occurrence is due again (a
    /// cancelled run whose command was proven stopped). Fenced like <see cref="CompleteRun"/>.
    /// </summary>
    public bool EndRunKeepingSchedule(RunRecord run) => EndOwnRun(run, moveSchedule: false, nextRun: null);

    private bool EndOwnRun(RunRecord run, bool moveSchedule, DateTime? nextRun)
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction(deferred: false);

        int applied;
        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = UpdateRunSql + """
                  AND ended_at IS NULL
                  AND owner_machine = @ownerMachine
                  AND owner_pid = @ownerPid
                  AND owner_process_started = @ownerStarted
                """;
            AddUpdateRunParameters(update, run);
            update.Parameters.AddWithValue("@ownerMachine", _owner.Machine);
            update.Parameters.AddWithValue("@ownerPid", _owner.ProcessId);
            update.Parameters.AddWithValue("@ownerStarted", _owner.ProcessStartedAtUtc.ToString("o"));
            applied = update.ExecuteNonQuery();
        }

        if (applied == 0)
        {
            using var late = conn.CreateCommand();
            late.Transaction = tx;
            late.CommandText = "UPDATE runs SET late_completion = @late WHERE id = @id";
            late.Parameters.AddWithValue("@id", run.Id);
            late.Parameters.AddWithValue("@late",
                $"{DateTime.UtcNow:o} exit={run.ExitCode?.ToString() ?? "none"} timedOut={run.TimedOut} {run.Stderr}".Trim());
            late.ExecuteNonQuery();
            tx.Commit();

            FileLog.Write($"[EngineDatabase] EndOwnRun: run={run.Id} was no longer this Director's open claim; result kept as late, schedule unchanged");
            return false;
        }

        if (moveSchedule)
        {
            using var next = conn.CreateCommand();
            next.Transaction = tx;
            next.CommandText = "UPDATE jobs SET next_run = @nextRun WHERE id = @id";
            next.Parameters.AddWithValue("@id", run.JobId);
            next.Parameters.AddWithValue("@nextRun", nextRun.HasValue ? nextRun.Value.ToString("o") : DBNull.Value);
            next.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    private const string InsertRunSql = """
        INSERT INTO runs (job_id, job_name, started_at, owner_director, owner_machine, owner_pid, owner_process_started, run_timeout_seconds)
        VALUES (@jobId, @jobName, @startedAt, @ownerDirector, @ownerMachine, @ownerPid, @ownerStarted,
                (SELECT timeout_seconds FROM jobs WHERE id = @jobId));
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

    /// <summary>Cleanup by a caller that is running nothing itself: this process's own runs are not judged.</summary>
    public int CleanupOrphanedRuns() => CleanupOrphanedRuns(DateTime.UtcNow, jobInFlightHere: null);

    public int CleanupOrphanedRuns(DateTime nowUtc) => CleanupOrphanedRuns(nowUtc, jobInFlightHere: null);

    /// <summary>
    /// Ends the unfinished runs that provably nobody is running, decided ONLY from what the database
    /// records, so nothing is lost with an executor, an engine restart or a dead Director. For each
    /// unfinished run, by its owner (the Director process that claimed it):
    /// <list type="bullet">
    /// <item>This process: kept while its job is being executed here (<paramref name="jobInFlightHere"/>);
    /// otherwise - a killed command that was not seen to exit, or a run left by an engine that was
    /// restarted in this process - judged by its command, below. When <paramref name="jobInFlightHere"/>
    /// is null the caller runs nothing and this process's runs are kept.</item>
    /// <item>Another owner the probe shows RUNNING: kept. A live owner is never aged out by another
    /// Director.</item>
    /// <item>An owner proven GONE: judged by its command, below.</item>
    /// <item>An owner that cannot be decided (another machine, an uninspectable process, a run from
    /// before owners were kept): ended once past its deadline.</item>
    /// </list>
    /// Judged by its command: a recorded command proven gone - ended at once; a recorded command still
    /// running - kept; no command-starting mark - the command never started, ended at once; anything
    /// that cannot be proven gone (the identity was never recorded, or the probe cannot decide) - ended
    /// once past its deadline, never earlier. The deadline is the timeout STORED ON THE RUN plus
    /// <see cref="AbandonedRunGrace"/>, never the job's current, editable timeout. Ending a run here
    /// never moves next_run, so the occurrence runs again.
    /// </summary>
    public int CleanupOrphanedRuns(DateTime nowUtc, Func<int, bool>? jobInFlightHere)
    {
        var open = new List<OpenRun>();
        using (var conn = CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, job_id, started_at, run_timeout_seconds, child_pid, child_process_started, command_starting_at,
                       owner_director, owner_machine, owner_pid, owner_process_started
                FROM runs
                WHERE ended_at IS NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                open.Add(new OpenRun(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    ReadOwner(reader, 7)));
            }
        }

        AfterOpenRunsRead?.Invoke();

        if (open.Count == 0)
            return 0;

        var count = 0;
        foreach (var run in open)
        {
            var reason = DecideOrphan(run, nowUtc, jobInFlightHere);
            if (reason is null) continue;

            using var conn = CreateConnection();
            using var update = conn.CreateCommand();
            // Conditional on EXACTLY the state it was decided from: still open, the same starting mark,
            // the same recorded command and the same owner. If the row moved on (a mark written, a
            // command recorded, the owner finished it), nothing changes and it is decided again next tick.
            update.CommandText = """
                UPDATE runs SET
                    ended_at = @endedAt,
                    exit_code = -1,
                    stderr = @reason,
                    duration_seconds = 0
                WHERE id = @id
                  AND ended_at IS NULL
                  AND command_starting_at IS @mark
                  AND child_pid IS @childPid
                  AND child_process_started IS @childStarted
                  AND owner_pid IS @ownerPid
                  AND owner_process_started IS @ownerStarted
                """;
            update.Parameters.AddWithValue("@id", run.Id);
            update.Parameters.AddWithValue("@endedAt", nowUtc.ToString("o"));
            update.Parameters.AddWithValue("@reason", reason);
            update.Parameters.AddWithValue("@mark", (object?)run.CommandStartingRaw ?? DBNull.Value);
            update.Parameters.AddWithValue("@childPid", (object?)run.ChildPid ?? DBNull.Value);
            update.Parameters.AddWithValue("@childStarted", (object?)run.ChildStartedRaw ?? DBNull.Value);
            update.Parameters.AddWithValue("@ownerPid", (object?)run.OwnerPidRaw ?? DBNull.Value);
            update.Parameters.AddWithValue("@ownerStarted", (object?)run.OwnerStartedRaw ?? DBNull.Value);
            var ended = update.ExecuteNonQuery();
            if (ended == 0)
                FileLog.Write($"[EngineDatabase] CleanupOrphanedRuns: run={run.Id} changed since it was read, not ended; decided again next tick");
            count += ended;
        }

        if (count > 0)
            FileLog.Write($"[EngineDatabase] CleanupOrphanedRuns: open={open.Count}, ended={count}, kept={open.Count - count}");
        return count;
    }

    /// <summary>Test seam: runs between reading the open runs and ending any of them.</summary>
    internal Action? AfterOpenRunsRead { get; set; }

    /// <summary>An open run exactly as read: the raw column values are what the conditional end compares against.</summary>
    private sealed record OpenRun(
        int Id, int JobId, DateTime StartedAt, int? TimeoutSeconds, int? ChildPid, string? ChildStartedRaw,
        string? CommandStartingRaw, int? OwnerPidRaw, string? OwnerStartedRaw, EngineRunOwner? Owner)
    {
        public bool CommandStarting => CommandStartingRaw is not null;

        public DateTime? ChildStartedAt => ChildStartedRaw is null
            ? null
            : DateTime.Parse(ChildStartedRaw, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>
    /// The reason to end this unfinished run, or null to keep it. See <see cref="CleanupOrphanedRuns(DateTime, Func{int, bool})"/>.
    /// A recorded command is asked FIRST, whatever the owner's state: proven running keeps the claim;
    /// proven gone releases it unless a live owner is still there to end it itself; only an undecidable
    /// command falls to the owner rules and, at most, the deadline.
    /// </summary>
    private string? DecideOrphan(OpenRun run, DateTime nowUtc, Func<int, bool>? jobInFlightHere)
    {
        var ours = run.Owner is not null && IsThisProcess(run.Owner);
        if (ours && (jobInFlightHere is null || jobInFlightHere(run.JobId)))
            return null;

        var owner = ours ? OwnerLiveness.Gone // ours and not executed here: nobody will end it but us
            : run.Owner is null ? OwnerLiveness.Unknown
            : _probeOwner(run.Owner);

        if (run.ChildPid is not null && run.ChildStartedAt is not null)
        {
            // The command ran on the owner's machine; it is judged by the same id-plus-start proof.
            var command = (run.Owner ?? _owner) with { ProcessId = run.ChildPid.Value, ProcessStartedAtUtc = run.ChildStartedAt.Value };
            switch (_probeOwner(command))
            {
                case OwnerLiveness.Running:
                    return null;
                case OwnerLiveness.Gone:
                    // A live owner records its own result; never end another live Director's run.
                    return owner == OwnerLiveness.Running ? null : InterruptedRunMessage;
            }
        }

        return owner switch
        {
            OwnerLiveness.Running => null,
            OwnerLiveness.Gone => DecideWithoutAProvenCommand(run, nowUtc),
            _ => PastDeadline(run, nowUtc) ? AbandonedRunMessage : null,
        };
    }

    /// <summary>Nobody is executing this run and no recorded command proves it alive or gone.</summary>
    private static string? DecideWithoutAProvenCommand(OpenRun run, DateTime nowUtc)
    {
        // No starting mark, read in the same state the conditional end will require: no command was
        // ever started for this run.
        if (run.ChildPid is null && !run.CommandStarting)
            return InterruptedRunMessage;

        // A command may be running and cannot be proven gone: held until the deadline - never released
        // earlier, never held forever.
        return PastDeadline(run, nowUtc) ? AbandonedRunMessage : null;
    }

    private static bool PastDeadline(OpenRun run, DateTime nowUtc) =>
        run.TimeoutSeconds is { } timeout
        && nowUtc - run.StartedAt > TimeSpan.FromSeconds(timeout) + AbandonedRunGrace;

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
            Owner = ReadOwner(reader, reader.GetOrdinal("owner_director")),
            TimeoutSeconds = reader.IsDBNull(reader.GetOrdinal("run_timeout_seconds")) ? null : reader.GetInt32(reader.GetOrdinal("run_timeout_seconds")),
            ChildProcessId = reader.IsDBNull(reader.GetOrdinal("child_pid")) ? null : reader.GetInt32(reader.GetOrdinal("child_pid")),
            LateCompletion = reader.IsDBNull(reader.GetOrdinal("late_completion")) ? null : reader.GetString(reader.GetOrdinal("late_completion"))
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
