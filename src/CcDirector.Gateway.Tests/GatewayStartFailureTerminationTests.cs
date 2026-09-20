using System.Reflection;
using CcDirector.Gateway;
using CcDirector.Gateway.Data;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The 2 August outage fix (issue #2383), guarded at the two places it actually lives.
///
/// WHAT WENT WRONG. GatewayService.StartAsync catches every startup exception, logs it, and does NOT
/// rethrow. GatewayWorker then awaited Task.Delay(Timeout.Infinite). So a Gateway that could not open its
/// database stayed ALIVE with nothing listening; the platform waited out its 230-second container start
/// limit, found no port, and STOPPED THE SITE - killing the healthy container serving beside it. The fix is
/// not a retry. It is that the PROCESS must end, because a container that exits gets restarted and a
/// container that is alive and silent gets waited out.
///
/// WHY THESE TESTS AND NOT THE PREVIOUS ONES. The first version of this file tested pure helpers only. The
/// reviewer reverted the real call sites - the two UseNpgsql lines and the DescribeFailure call - and every
/// test stayed green. Those tests were decoration. Each test below was checked by mutating the exact
/// production LINE it claims to guard, confirming by diff that the mutation was really in the file, and
/// requiring a RED before the mutation was reverted.
/// </summary>
public sealed class GatewayStartFailureTerminationTests
{
    // ---- the termination policy ------------------------------------------------------------------
    // Guards: GatewayWorker.MustTerminate. Mutating it to `=> false` reddens FailedMustEndTheProcess.

    [Fact]
    public void AFailedStart_MustEndTheProcess()
    {
        Assert.True(GatewayWorker.MustTerminate(GatewayServiceState.Failed));
    }

    [Theory]
    [InlineData(GatewayServiceState.Running)]
    [InlineData(GatewayServiceState.Starting)]
    [InlineData(GatewayServiceState.Stopped)]
    public void AnyOtherState_MustNotEndTheProcess(GatewayServiceState state)
    {
        // A Gateway that started must never be killed by this rule, and a deliberate stop is not a failure.
        Assert.False(GatewayWorker.MustTerminate(state));
    }

    [Fact]
    public void TheFailureExitCode_IsNonZero()
    {
        // The platform decides what to do from the exit code; zero reads as a clean, intended shutdown,
        // which is exactly the wrong signal for a Gateway that could not start.
        Assert.NotEqual(0, GatewayWorker.StartFailureExitCode);
    }

    // ---- the termination WIRING ------------------------------------------------------------------
    // The policy above is worthless if nothing calls it, and that call sits inside an async state machine
    // that cannot be invoked from a test without ending the test run. So the wiring is read out of the
    // compiled IL: ExecuteAsync's state machine must call BOTH MustTerminate and Environment.Exit.
    // Deleting either from GatewayWorker reddens this.

    [Fact]
    public void ExecuteAsync_ConsultsThePolicyAndEndsTheProcess()
    {
        // SCOPED TO THE ExecuteAsync STATE MACHINE, not the whole type. The type also contains
        // Environment.Exit(0) from the constructor's ShutdownRequested handler, so a type-wide check stayed
        // GREEN when the failure-path exit was deleted - it was decoration, and mutation testing caught it.
        // The async body compiles into a nested <ExecuteAsync>d__N; the constructor's lambda does not.
        var module = ModuleDefinition.ReadModule(typeof(GatewayWorker).Assembly.Location);
        var worker = module.GetType(typeof(GatewayWorker).FullName);
        Assert.NotNull(worker);

        var stateMachine = worker!.NestedTypes.FirstOrDefault(n => n.Name.Contains("ExecuteAsync"));
        Assert.NotNull(stateMachine);

        var calls = new List<string>();
        foreach (var method in stateMachine!.Methods)
        {
            if (!method.HasBody) continue;
            foreach (var instruction in method.Body.Instructions)
            {
                if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                    && instruction.Operand is MethodReference called)
                    calls.Add($"{called.DeclaringType.FullName}::{called.Name}");
            }
        }

        Assert.Contains(calls, c => c.EndsWith("GatewayWorker::MustTerminate"));
        Assert.Contains(calls, c => c == "System.Environment::Exit");
    }

    // ---- wiring guards for the sites the previous tests left unguarded ---------------------------
    // The reviewer reverted the two UseNpgsql lines and the DescribeFailure call and every test stayed
    // green. These close the DELETION case at both sites by reading the compiled IL.
    //
    // WHAT AN IL GUARD CANNOT DO, stated in full rather than as the one limitation that came to mind first.
    // An earlier version of this note said only that dataflow is unprovable; that was narrower than the
    // truth and would have read as considered while leaving three other holes unmentioned:
    //
    //  1. PRESENCE IS NOT REACHABILITY. It proves the call instruction exists in the compiled method. It
    //     proves nothing about whether execution ever gets there - a call behind a condition that is never
    //     true is still in the IL. This is the same defect as the swallowed exception one level up, which
    //     is why the termination path also has an end-to-end test above that drives the real sequence and
    //     asserts the resulting state. Where a guard below has no such end-to-end partner, it is proving
    //     the weaker property, and that is a real gap rather than a technicality.
    //  2. PRESENCE IS NOT DATAFLOW. It cannot prove WHICH value reaches UseNpgsql, so a mutation that keeps
    //     the call and passes the raw connection string stays green. Proving that needs an integration test
    //     against a real server, where PostgresProviderProofTests lives - it is in
    //     CcDirector.Gateway.Postgres.Tests now, with every other proof that needs a database.
    //  3. PRESENCE IS NOT ORDER. Nothing here proves the bounding happens BEFORE the provider is built.
    //
    // Hole (4) - a call sitting in some unrelated method of the same type - is closed below by scoping each
    // scan to the ONE method that must make the call, rather than to the whole type. That hole was not
    // hypothetical: the first version of the termination guard scanned the whole GatewayWorker type and was
    // satisfied by the constructor's Environment.Exit(0), so deleting the failure-path exit left it green.

    private static List<string> CallsInMethod(Type type, string methodName)
    {
        var module = ModuleDefinition.ReadModule(type.Assembly.Location);
        var target = module.GetType(type.FullName);
        Assert.NotNull(target);

        var methods = target!.Methods.Where(m => m.Name == methodName && m.HasBody).ToList();
        Assert.True(methods.Count > 0, $"no method body named '{methodName}' on {type.FullName}");

        var calls = new List<string>();
        foreach (var m in methods)
            foreach (var i in m.Body.Instructions)
                if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r)
                    calls.Add($"{r.DeclaringType.Name}::{r.Name}");
        return calls;
    }

    [Fact]
    public void TheGatewayStoreConstructor_BoundsItsPool()
    {
        // Scoped to the CONSTRUCTOR because that is where the pool bound belongs: it is part of PARSING the
        // connection string, which still happens at construction even now that connecting does not. Moving
        // it into a helper that nothing calls would keep a type-wide scan green, which is why this is scoped
        // to the one method rather than the type.
        var calls = CallsInMethod(typeof(GatewayDatabase), ".ctor");
        Assert.Contains(calls, c => c == "GatewayDatabase::WithBoundedPool");
    }

    [Fact]
    public void TheGatewayStoreOpen_RedactsItsFailures()
    {
        // Scoped to Open, which is where CONNECTING now happens.
        //
        // This assertion used to name the constructor, and it moved with the code rather than being relaxed:
        // the open was split out of the constructor so the Gateway's listener can bind before any database
        // work (#2383, #2585). The guard it provides is unchanged and still worth having - every failure on
        // the connect path must go through DescribeFailure, because the provider's own message can echo part
        // of the connection string and GatewayService.StartAsync writes ex.Message straight to disk.
        //
        // Naming the method that actually owns the call is the whole point of scoping these to one method;
        // widening this to a type-wide scan to survive the move would have thrown away the guarantee.
        var calls = CallsInMethod(typeof(GatewayDatabase), "Open");
        Assert.Contains(calls, c => c == "GatewayDatabase::DescribeFailure");
    }

    [Fact]
    public void TheStatisticsStoreProvider_BoundsItsPoolToo()
    {
        // The second Npgsql pool in every container - the one that roughly doubled the connection demand
        // and turned a slow post-swap boot into a refused one. Scoped to BuildProvider for the same reason.
        var statsStore = typeof(GatewayDatabase).Assembly.GetType("CcDirector.Gateway.Stats.Data.GatewayStatsStore");
        Assert.NotNull(statsStore);
        Assert.Contains(CallsInMethod(statsStore!, "BuildProvider"), c => c == "GatewayDatabase::WithBoundedPool");
    }

}

/// <summary>
/// The redaction boundary around the connection-string parse, in its own class because it SETS the
/// process-wide <c>CC_GATEWAY_DB_CONNECTION</c>. It joins the collection that exists for exactly that
/// hazard: a concurrent test constructing a GatewayDatabase would otherwise read this test's value
/// mid-flight and select the wrong provider. The value is also saved and restored in a finally, as that
/// collection's own tests do.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class GatewayConnectionStringRedactionTests
{
    // ---- the redaction boundary around the connection-string parse -------------------------------
    // Guards the REAL production line: GatewayDatabase's constructor calling WithBoundedPool inside a
    // redacting try/catch. Removing that try/catch reddens this, because Npgsql's own message carries the
    // offending keyword and would reach the caller - and GatewayService.StartAsync writes ex.Message
    // straight to disk.
    //
    // The secret is placed in KEYWORD position because that is the position Npgsql echoes. Measured:
    // "SUPERSECRET=x" throws "Couldn't set supersecret (Parameter 'supersecret')". The value position does
    // not echo, so a test using it would pass whether or not the boundary existed.

    /// <summary>
    /// THE END-TO-END LINK, and the one an IL guard cannot give. An IL guard proves the exit call exists in
    /// the assembly; it proves nothing about whether anything ever REACHES it - which is the same defect as
    /// the swallowed exception, one level up. So this drives the real path: a Gateway whose database cannot
    /// be opened must leave <see cref="GatewayService.StartAsync"/> in the state the termination decision
    /// reads.
    ///
    /// It runs the production sequence, not a stand-in: GatewayService.StartAsync -> new GatewayHost ->
    /// GatewayDatabase throws -> the catch at GatewayService.cs:133 -> DiagnoseStartFailureAsync ->
    /// SetState(Failed). If anything in that chain ever catches and returns without recording the failure,
    /// State is no longer Failed, MustTerminate returns false, and the process would go back to hanging -
    /// which is exactly the regression this asserts against.
    ///
    /// The connection string is UNPARSEABLE rather than merely unreachable so the failure lands before
    /// GatewayDatabase's retry window and the test costs no ninety-second wait.
    /// </summary>
    [Fact]
    public async Task ADatabaseThatCannotBeOpened_LeavesTheServiceInTheStateTerminationReads()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        // A port nothing else in the suite uses; the Gateway never gets far enough to bind it.
        var service = new GatewayService(new GatewayServiceOptions
        {
            Port = 59_317,
            Managed = false,
            RegisterAutostart = false,
            ModeLabel = "test",
        });
        try
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, "unparseable=x");

            await service.StartAsync();

            Assert.Equal(GatewayServiceState.Failed, service.State);
            // And therefore the decision the worker makes on that state ends the process.
            Assert.True(GatewayWorker.MustTerminate(service.State));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            service.Dispose();
        }
    }

    [Fact]
    public void AnUnparseableConnectionString_NeverPutsItsTextInTheError()
    {
        const string secret = "hunter2secretfragment";
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, $"{secret}=x");

            var ex = Assert.ThrowsAny<Exception>(
                () => new GatewayDatabase(new CcDirector.Core.Tenancy.SingleTenantContext()));

            // Case-INSENSITIVE: Npgsql lowercases the keyword it echoes, so a case-sensitive check would
            // pass while the secret sat in the log. That is how this leak was nearly missed.
            Assert.DoesNotContain(secret, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
        }
    }
}
