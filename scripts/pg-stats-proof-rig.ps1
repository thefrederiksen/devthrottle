<#
.SYNOPSIS
    Stands up a PRIVATE throwaway PostgreSQL instance whose login role mirrors the hosted Gateway's
    measured database grants, so a caller can prove what that role can and cannot do (Step 2 of the
    "no SQLite on the hosted Gateway" mission).

.DESCRIPTION
    The hosted Gateway connects to Supabase as a role called gateway_app. That role was interrogated
    READ-ONLY against the live database and measured to be:

        not superuser, no CREATEDB, no CREATEROLE, no BYPASSRLS, inherits
        member of no other role
        has_database_privilege(CREATE) on the database  = true
        can CREATE in and USE the existing "gateway" schema
        can USE but CANNOT CREATE in "public"
        search_path = gateway, "$user", public

    This script builds a LOCAL role holding exactly those grants and no more, and hands out two
    connection strings:

        CC_GATEWAY_TEST_PG_CONNECTION        superuser, the general-purpose database - the existing
                                             gated-test variable, unchanged in meaning
        CC_GATEWAY_TEST_PG_STATS_CONNECTION  the restricted role, the statistics database - the
                                             subject of the schema-creation proof

    ONE INSTANCE PER CALLER. -Instance and -Port are both MANDATORY and there is no shared default,
    because every name the rig uses is derived from -Instance. Two agents running this script land on
    two separate servers, never one. That is not tidiness: this script REVOKES and GRANTS a privilege
    and TEARS DOWN a container, and either of those landing inside somebody else's running test
    produces a confidently wrong result - a revoke during their green run reads as a real finding, and
    a revoke during their deliberate-red run makes a test that does NOT detect look like one that
    does. A wrong proof is worse than no proof, so the isolation is enforced by the parameter contract
    rather than by everyone remembering.

    TWO DATABASES PER INSTANCE, also on purpose. The existing Postgres proof suite calls
    EnsureDeleted() on whatever CC_GATEWAY_TEST_PG_CONNECTION points at, so if both suites shared one
    database a from-nothing migrate in one would drop the database out from under the other mid-run.
    The suites run in parallel, so that is not theoretical. The proof database is the droppable one;
    the statistics database holds the restricted role's work and nothing drops it.

    The restricted role is deliberately no more privileged than the measured hosted role: CREATE on
    schema public is revoked from PUBLIC and from the role explicitly, so a proof cannot pass by
    borrowing a privilege gateway_app does not have. GatewayStatsSchemaPrivilegeProofTests asserts
    that mirror from the catalog before it proves anything, so a drifted rig fails loud rather than
    producing a worthless green.

    NOTHING HERE EVER TOUCHES THE HOSTED DATABASE. Staging shares production's database, so a failed
    creation experiment there would land on the live database. This rig is local Docker only.

.PARAMETER Instance
    MANDATORY. Your own short name for this rig (lower-case letters, digits, hyphens). Every container,
    database and role name is derived from it, so two callers with different names cannot collide.

.PARAMETER Port
    MANDATORY. The host port to publish. Pick one nobody else is using; the script refuses to start if
    another container already holds it.

.PARAMETER Verb
    up                      start the container (if needed) and provision the restricted role
    down                    remove this instance's container and its volume
    status                  report the container state and the role's measured grants
    print-env               print the two connection strings as environment-variable assignments
    revoke-database-create  REVOKE CREATE ON DATABASE from the restricted role (the deliberate red)
    grant-database-create   restore that grant (the green again)
    reset-stats-schema      DROP SCHEMA gateway_stats CASCADE, so a migrate runs from nothing

.PARAMETER Emit
    powershell (default) or bash - which shell's syntax "print-env" writes.

.EXAMPLE
    powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance w1 -Port 55433 -Verb up
    powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance w1 -Port 55433 -Verb print-env -Emit bash
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,19}$')]
    [string] $Instance,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1024, 65535)]
    [int] $Port,

    [Parameter(Mandatory = $true)]
    [ValidateSet('up', 'down', 'status', 'print-env', 'revoke-database-create', 'grant-database-create', 'reset-stats-schema')]
    [string] $Verb,

    [ValidateSet('powershell', 'bash')]
    [string] $Emit = 'powershell',

    [string] $SuperuserPassword = 'proof',

    [string] $RestrictedPassword = 'proof',

    # An EXTRA docker label to stamp on the container, so an automated caller can find and clean up the
    # rigs it created without having to remember their instance names. scripts	est-local.ps1 passes its
    # own label and sweeps on it after a killed run (issue #2834). Empty for a hand-driven rig, which is
    # owned by the person who started it and is never swept.
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$|^$')]
    [string] $Label = ''
)

$ErrorActionPreference = 'Stop'

# Every name is derived from -Instance, so there is no shared default two callers can land on. SQL
# identifiers cannot carry a hyphen unquoted, so they take the underscore form of the same name. The
# database names keep the "ccpg" prefix the gated tests demand before they will drop anything.
$identifierSuffix = $Instance -replace '-', '_'
$ContainerName = "cc-pg-stats-proof-$Instance"
$InstanceLabel = 'cc-pg-stats-proof-instance'
$ProofDatabase = "ccpgproof_$identifierSuffix"
$StatsDatabase = "ccpgstats_$identifierSuffix"
$RestrictedRole = "gateway_app_$identifierSuffix"

function Write-Step([string] $message) {
    Write-Host "[pg-stats-proof-rig/$Instance] $message"
}

<#
    Run a native command, capture BOTH streams and the exit code, and hand them back for the caller to
    judge. Necessary because this script runs with $ErrorActionPreference = 'Stop', under which
    PowerShell treats anything a native executable writes to stderr as a terminating error - and psql
    writes ordinary NOTICE lines there on a completely successful run. The exit code is the truth about
    success or failure, so the exit code is what every caller checks, explicitly.
#>
function Invoke-NativeCapture {
    param([Parameter(Mandatory = $true)][scriptblock] $Command)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = (& $Command 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{ Output = $output; ExitCode = $exitCode }
}

function Invoke-Docker {
    param([string[]] $DockerArgs)

    $result = Invoke-NativeCapture -Command { & docker @DockerArgs }
    if ($result.ExitCode -ne 0) {
        throw "docker $($DockerArgs -join ' ') FAILED (exit $($result.ExitCode)): $($result.Output)"
    }
    return $result.Output
}

<#
    Run SQL inside this instance's container as the superuser, against one named database.
    ON_ERROR_STOP=1 means a failing statement fails the whole call instead of psql shrugging and
    carrying on with a non-zero-but-ignored statement.
#>
function Invoke-Sql {
    param(
        [Parameter(Mandatory = $true)][string] $Sql,
        [Parameter(Mandatory = $true)][string] $Database,
        [switch] $Quiet)

    $psqlArgs = @('exec', '-i', $ContainerName, 'psql', '-h', '127.0.0.1', '-U', 'postgres', '-d', $Database, '-v', 'ON_ERROR_STOP=1')
    if ($Quiet) { $psqlArgs += @('-t', '-A') }

    $result = Invoke-NativeCapture -Command { $Sql | & docker @psqlArgs }
    if ($result.ExitCode -ne 0) {
        throw "psql against '$Database' FAILED (exit $($result.ExitCode)):`n$($result.Output)`n--- SQL ---`n$Sql"
    }
    return $result.Output
}

function Test-ContainerExists {
    $names = Invoke-Docker -DockerArgs @('ps', '-a', '--format', '{{.Names}}')
    return ($names -split "`n" | ForEach-Object { $_.Trim() }) -contains $ContainerName
}

function Test-ContainerRunning {
    if (-not (Test-ContainerExists)) { return $false }
    $state = (Invoke-Docker -DockerArgs @('inspect', '-f', '{{.State.Running}}', $ContainerName)).ToString().Trim()
    return $state -eq 'true'
}

<#
    Refuse to touch a container of our name that this script did not create. Adopting a stranger's
    container would be exactly the cross-caller accident the instance parameter exists to prevent.
#>
function Assert-OwnedByThisInstance {
    # Asked as a docker filter rather than an inspect format string: a Go template carrying a quoted
    # label key does not survive PowerShell's native-argument quoting, and a template that silently
    # loses its quotes fails with a parser error rather than a wrong answer only by luck.
    $labelled = Invoke-Docker -DockerArgs @('ps', '-a', '--filter', "label=$InstanceLabel=$Instance", '--format', '{{.Names}}')
    $names = ($labelled -split "`n" | ForEach-Object { $_.Trim() })
    if ($names -notcontains $ContainerName) {
        throw "Container '$ContainerName' exists but does not carry the label $InstanceLabel=$Instance. Refusing to touch a container this rig did not create - choose a different -Instance."
    }
}

function Assert-ContainerRunning {
    if (-not (Test-ContainerRunning)) {
        throw "Container '$ContainerName' is not running. Start it with: powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance $Instance -Port $Port -Verb up"
    }
    Assert-OwnedByThisInstance
}

<#
    Fail loud if another container already publishes the requested host port. Docker would report the
    bind failure itself, but naming the squatter turns a port clash into a one-line fix instead of a
    hunt - and a port clash between two callers is the exact accident this rig is designed to prevent.
#>
function Assert-PortIsFree {
    $rows = Invoke-Docker -DockerArgs @('ps', '--format', '{{.Names}}|{{.Ports}}')
    foreach ($row in ($rows -split "`n")) {
        $trimmed = $row.Trim()
        if (-not $trimmed) { continue }
        $parts = $trimmed -split '\|', 2
        if ($parts[0] -eq $ContainerName) { continue }
        if ($parts.Length -eq 2 -and $parts[1] -match ":$Port->") {
            throw "Host port $Port is already published by container '$($parts[0])'. Pick a different -Port for instance '$Instance'."
        }
    }
}

function Wait-ForPostgres {
    param([int] $TimeoutSeconds = 90)

    # Readiness is probed over TCP (-h 127.0.0.1) with a real query, NOT with a bare pg_isready on the
    # unix socket. The postgres image runs its initdb phase against a temporary server bound only to
    # the unix socket, then shuts that one down and starts the real one - so a socket probe reports
    # READY during init and the very next statement hits "the database system is shutting down". The
    # temporary server never listens on TCP, so a successful TCP query is true readiness.
    #
    # A refused connection is the EXPECTED state while the server is still coming up, so the probe
    # runs with errors non-terminating; the loop's own timeout is what fails, loudly, with the log to
    # read. That is the failure being handled explicitly, not swallowed.
    Write-Step "Waiting for PostgreSQL to accept TCP connections (up to $TimeoutSeconds seconds)..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $probe = Invoke-NativeCapture -Command {
            & docker exec $ContainerName psql -h 127.0.0.1 -U postgres -d $ProofDatabase -c 'SELECT 1'
        }
        if ($probe.ExitCode -eq 0) {
            Write-Step "PostgreSQL is ready."
            return
        }
        Start-Sleep -Milliseconds 500
    }
    throw "PostgreSQL in container '$ContainerName' did not accept a TCP query within $TimeoutSeconds seconds. Inspect it with: docker logs $ContainerName"
}

function Get-SuperuserConnection {
    return "Host=localhost;Port=$Port;Database=$ProofDatabase;Username=postgres;Password=$SuperuserPassword"
}

function Get-RestrictedConnection {
    return "Host=localhost;Port=$Port;Database=$StatsDatabase;Username=$RestrictedRole;Password=$RestrictedPassword"
}

function Invoke-Up {
    if (Test-ContainerExists) {
        Assert-OwnedByThisInstance
        if (Test-ContainerRunning) {
            Write-Step "Container '$ContainerName' is already running."
        }
        else {
            Write-Step "Container '$ContainerName' exists but is stopped - starting it."
            Invoke-Docker -DockerArgs @('start', $ContainerName) | Out-Null
        }
    }
    else {
        Assert-PortIsFree
        Write-Step "Creating container '$ContainerName' from postgres:16 on host port $Port."
        $dockerArgs = @(
            'run', '-d',
            '--name', $ContainerName,
            '--label', "$InstanceLabel=$Instance"
        )
        # The caller's own label, when it gave one, so it can sweep its abandoned rigs by label.
        if ($Label -ne '') { $dockerArgs += @('--label', "$Label=1") }
        $dockerArgs += @(
            '-e', "POSTGRES_PASSWORD=$SuperuserPassword",
            '-e', "POSTGRES_DB=$ProofDatabase",
            '-p', "${Port}:5432",
            'postgres:16'
        )
        Invoke-Docker -DockerArgs $dockerArgs | Out-Null
    }

    Wait-ForPostgres

    # CREATE DATABASE cannot run inside a transaction block and has no IF NOT EXISTS, so it is guarded
    # by a catalog read rather than by swallowing the duplicate-database error.
    $statsExists = (Invoke-Sql -Database $ProofDatabase -Quiet `
            -Sql "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = '$StatsDatabase');").ToString().Trim()
    if ($statsExists -ne 't') {
        Write-Step "Creating the statistics database '$StatsDatabase' (owner postgres, NOT the restricted role)."
        Invoke-Sql -Database $ProofDatabase -Sql "CREATE DATABASE $StatsDatabase OWNER postgres;" | Out-Null
    }
    else {
        Write-Step "Statistics database '$StatsDatabase' already exists."
    }

    Write-Step "Provisioning the restricted role '$RestrictedRole' to mirror the measured hosted gateway_app grants."

    # Every grant below is one the hosted role was MEASURED to hold, and every revoke removes one it
    # was measured NOT to hold. The role is created bare and then given its attributes by ALTER, so
    # the same statement set is safe to re-run against an existing role.
    $sql = @"
DO `$`$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$RestrictedRole') THEN
        CREATE ROLE $RestrictedRole LOGIN;
    END IF;
END
`$`$;

-- Role attributes: exactly what gateway_app has. No superuser, no CREATEDB, no CREATEROLE, no
-- BYPASSRLS, inherits. Stated explicitly rather than relying on CREATE ROLE defaults, so a rig
-- rebuilt on a differently-configured server still produces the same role.
ALTER ROLE $RestrictedRole NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT LOGIN
    PASSWORD '$RestrictedPassword';

-- The database privilege under test. CONNECT is table stakes; CREATE on the DATABASE is what permits
-- CREATE SCHEMA, and it is the single privilege the whole Step 2 design rests on.
GRANT CONNECT ON DATABASE $StatsDatabase TO $RestrictedRole;
GRANT CREATE ON DATABASE $StatsDatabase TO $RestrictedRole;

-- public: USAGE yes, CREATE no. Revoked from PUBLIC as well as from the role, because a role picks up
-- PUBLIC's grants implicitly - leaving PUBLIC's CREATE in place would hand the local role a privilege
-- the hosted role does not have and quietly invalidate the proof.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM $RestrictedRole;
GRANT USAGE ON SCHEMA public TO $RestrictedRole;

-- The existing application schema: owned by the superuser, with the role able to use it and create in
-- it. This is the shape on the hosted database, and it is what makes the "the gateway schema already
-- exists" trap reproducible locally - its existence proves nothing about the create privilege.
CREATE SCHEMA IF NOT EXISTS gateway AUTHORIZATION postgres;
GRANT USAGE, CREATE ON SCHEMA gateway TO $RestrictedRole;

-- The hosted role's search_path, carried over so a query behaves the same here.
ALTER ROLE $RestrictedRole SET search_path = gateway, "`$user", public;
"@

    Invoke-Sql -Database $StatsDatabase -Sql $sql | Out-Null

    Write-Step "Provisioned. Measured grants on '$StatsDatabase':"
    Show-Status
    Write-Host ''
    Write-EnvLines
}

function Invoke-Down {
    if (-not (Test-ContainerExists)) {
        Write-Step "Container '$ContainerName' does not exist - nothing to remove."
        return
    }
    Assert-OwnedByThisInstance
    Write-Step "Removing container '$ContainerName' and its volume."
    Invoke-Docker -DockerArgs @('rm', '-f', '-v', $ContainerName) | Out-Null
    Write-Step "Removed."
}

function Show-Status {
    if (-not (Test-ContainerRunning)) {
        Write-Step "Container '$ContainerName' is NOT running. Start it with: -Verb up"
        return
    }
    Assert-OwnedByThisInstance

    # The same questions that were asked of the live hosted role, asked of the local one.
    $sql = @"
SELECT
    'rolsuper=' || r.rolsuper ||
    ' rolcreatedb=' || r.rolcreatedb ||
    ' rolcreaterole=' || r.rolcreaterole ||
    ' rolbypassrls=' || r.rolbypassrls ||
    ' rolinherit=' || r.rolinherit
FROM pg_roles r WHERE r.rolname = '$RestrictedRole';
SELECT 'memberships=' || count(*) FROM pg_auth_members m
    JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = '$RestrictedRole';
SELECT 'is_database_owner=' || (d.datdba = (SELECT oid FROM pg_roles WHERE rolname = '$RestrictedRole'))
FROM pg_database d WHERE d.datname = '$StatsDatabase';
SELECT 'database_CREATE=' || has_database_privilege('$RestrictedRole', '$StatsDatabase', 'CREATE');
SELECT 'public_CREATE=' || has_schema_privilege('$RestrictedRole', 'public', 'CREATE');
SELECT 'public_USAGE=' || has_schema_privilege('$RestrictedRole', 'public', 'USAGE');
SELECT 'gateway_CREATE=' || has_schema_privilege('$RestrictedRole', 'gateway', 'CREATE');
SELECT 'gateway_stats_exists=' || EXISTS(SELECT 1 FROM information_schema.schemata WHERE schema_name = 'gateway_stats');
"@
    $rows = Invoke-Sql -Database $StatsDatabase -Sql $sql -Quiet
    foreach ($row in ($rows -split "`n")) {
        $trimmed = $row.Trim()
        if ($trimmed) { Write-Host "  $trimmed" }
    }
}

function Write-EnvLines {
    $super = Get-SuperuserConnection
    $restricted = Get-RestrictedConnection

    if ($Emit -eq 'bash') {
        Write-Host "export CC_GATEWAY_TEST_PG_CONNECTION='$super'"
        Write-Host "export CC_GATEWAY_TEST_PG_STATS_CONNECTION='$restricted'"
    }
    else {
        Write-Host "`$env:CC_GATEWAY_TEST_PG_CONNECTION = '$super'"
        Write-Host "`$env:CC_GATEWAY_TEST_PG_STATS_CONNECTION = '$restricted'"
    }
}

function Set-DatabaseCreateGrant {
    param([bool] $Granted)

    Assert-ContainerRunning

    if ($Granted) {
        Write-Step "GRANT CREATE ON DATABASE $StatsDatabase TO $RestrictedRole"
        Invoke-Sql -Database $StatsDatabase -Sql "GRANT CREATE ON DATABASE $StatsDatabase TO $RestrictedRole;" | Out-Null
    }
    else {
        Write-Step "REVOKE CREATE ON DATABASE $StatsDatabase FROM $RestrictedRole"
        Invoke-Sql -Database $StatsDatabase -Sql "REVOKE CREATE ON DATABASE $StatsDatabase FROM $RestrictedRole;" | Out-Null
    }

    $state = (Invoke-Sql -Database $StatsDatabase -Quiet `
            -Sql "SELECT has_database_privilege('$RestrictedRole', '$StatsDatabase', 'CREATE');").ToString().Trim()
    Write-Step "has_database_privilege(CREATE) is now: $state"
}

function Invoke-ResetStatsSchema {
    Assert-ContainerRunning
    Write-Step "DROP SCHEMA IF EXISTS gateway_stats CASCADE - the next migrate runs from nothing."
    Invoke-Sql -Database $StatsDatabase -Sql "DROP SCHEMA IF EXISTS gateway_stats CASCADE;" | Out-Null
    Write-Step "Dropped."
}

switch ($Verb) {
    'up' { Invoke-Up }
    'down' { Invoke-Down }
    'status' { Show-Status }
    'print-env' { Write-EnvLines }
    'revoke-database-create' { Set-DatabaseCreateGrant -Granted $false }
    'grant-database-create' { Set-DatabaseCreateGrant -Granted $true }
    'reset-stats-schema' { Invoke-ResetStatsSchema }
}
