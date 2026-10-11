#Requires -Version 5.1
<#
.SYNOPSIS
    How the gate splits CcDirector.Gateway.Tests across concurrent test processes. Dot-source this file; it
    defines functions and runs nothing.

.DESCRIPTION
    WHY THE SUITE IS SPLIT. Gateway.Tests starts a real Gateway host for most of its tests and runs its tests
    one at a time inside a process, because about 165 of its files set process-wide environment variables.
    One process therefore took 2 hours 9 minutes on the v2.18.0 candidate. Several processes side by side do
    not share any of that state - each test process keeps its files under its own temporary root (see
    TestStorageRootRedirect.cs) and every host binds a port the operating system assigns - so the gate runs
    the suite as several processes, each given a disjoint part of the suite by a test filter.

    HOW A TEST IS PLACED. By the first character of its name after "CcDirector.Gateway.Tests.", which is the
    first letter of the class name or of the sub-namespace (Teams, Account, Attribution...). Each shard but
    the last names its letters; the LAST shard is everything the others do not name, written as the negation
    of their terms. So every test is in exactly one shard, including a test in a class whose letter nobody
    thought of - a new class can make a shard slower, never make a test silently stop running.

    THE LETTERS ARE BALANCED BY MEASUREMENT, not by count. Re-balance from the per-class wall clock in the
    shard TRX files when one shard runs much longer than the others.
#>

# The letters each named shard runs, in order. The shard after the last entry runs every other letter.
$script:GatewayTestShardLetters = @(
    @('T', 'A', 'E', 'I', 'K', 'U'),
    @('H', 'D', 'B', 'N', 'P', 'Q'),
    @('S', 'F', 'L', 'O')
)

$script:GatewayTestNamespacePrefix = 'CcDirector.Gateway.Tests.'

<#
.SYNOPSIS
    The test filter for every shard of Gateway.Tests, one string per shard, the last being the complement of
    the others. Together they select every test exactly once.
#>
function Get-GatewayTestShardFilters {
    $filters = @()
    $named = @()
    foreach ($letters in $script:GatewayTestShardLetters) {
        $terms = @($letters | ForEach-Object { "FullyQualifiedName~$($script:GatewayTestNamespacePrefix)$_" })
        $filters += ($terms -join '|')
        $named += $letters
    }
    $filters += (@($named | ForEach-Object { "FullyQualifiedName!~$($script:GatewayTestNamespacePrefix)$_" }) -join '&')
    return ,$filters
}

<#
.SYNOPSIS
    One shard's filter combined with the caller's own -Filter, so a filtered run is split the same way.
#>
function Join-GatewayTestShardFilter([string] $CallerFilter, [string] $ShardFilter) {
    if ([string]::IsNullOrWhiteSpace($CallerFilter)) { return $ShardFilter }
    return "($CallerFilter)&($ShardFilter)"
}

<#
.SYNOPSIS
    The suite a result belongs to, without a shard label: "CcDirector.Gateway.Tests.shard2of4" is
    "CcDirector.Gateway.Tests". Any other name is returned as it is.
#>
function Get-SuiteBaseName([string] $Suite) {
    return ($Suite -replace '\.shard\d+of\d+$', '')
}
