using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseSignInTestData;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE GATEWAY RELAYS AND NEVER KEEPS (License Switching, contract section 8.3). The address a sign-in is approved at
/// and the sealed approval pass through memory for the length of one relay and go nowhere else: no stored table has a
/// column for either, the bytes of the database after a relay contain neither, and no log line carries either - while
/// the lines that DO name the sign-in are there, so the last check cannot pass by the relay having logged nothing.
/// It owns the log for its run, so it sits in the capture collection.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class LicenseSignInKeepsNothingTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");

    // Marked so that a search of a file or a line cannot match by accident.
    private const string AddressMark = "challenge-MARK-7f3a9c";
    private const string Address = "https://claude.example/authorize?code_challenge=" + AddressMark;
    private const string SealedValue = "U0VBTEVELU1BUkstNGQ4ZTFi";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseSignInLog _log;
    private readonly FakeLicenseDirectors _directors = new();

    public LicenseSignInKeepsNothingTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _approvers = new LicenseApproverStore(new TenantSettingsStore(_db));
        _log = new LicenseSignInLog(_db);
    }

    public void Dispose() => _harness.Dispose();

    private LicenseApprovalRelay NewRelay() => new(_approvers, new LicenseReadModel(_store), _directors.IsConnected, _directors.SendAsync,
        _directors.EnterScope, () => T0, CancellationToken.None);

    /// <summary>One approved relay, one refused by the approving machine, one the Gateway refuses itself (no approver),
    /// and one sign-in started from the Cockpit and finished - everything this work writes anywhere.</summary>
    private async Task EverythingHappensOnce()
    {
        _store.ApplyReport(One, "dir-target", 1, Report("the-windows-machine", T0, Holding(B, true)), T0, TimeSpan.Zero);
        _store.ApplyReport(One, "dir-approver", 2, HoldsNothing("the-mac", T0), T0, TimeSpan.Zero);
        _directors.Connect(One, "dir-target");
        _directors.Connect(One, "dir-approver");
        Assert.True(_approvers.Set(One, A, "the-mac", "owner", "work", T0).Accepted);

        var relay = NewRelay();
        _directors.AnswerWith(sent => sent.Verb switch
        {
            LicenseContract.ApproveVerb when ((LicenseApproveRequest)sent.Payload).SignInId == SignIn1 => FakeLicenseDirectors.Ok(new LicenseApprovalResult
                { SignInId = SignIn1, Outcome = LicenseApprovalOutcomes.Approved, Message = "Approved.", SealedApproval = SealedValue }),
            LicenseContract.ApproveVerb => FakeLicenseDirectors.Ok(new LicenseApprovalResult
            {
                SignInId = ((LicenseApproveRequest)sent.Payload).SignInId,
                Outcome = LicenseApprovalOutcomes.Refused,
                ReasonCode = LicenseRenewalErrors.WrongAccount,
                // A Director that wrongly echoes what it was sent, in its sentence and in a field it must leave empty.
                Message = "Refused.",
                SealedApproval = SealedValue,
            }),
            LicenseContract.SignInVerb => FakeLicenseDirectors.Ok(new LicenseSignInResult
                { SignInId = ((LicenseSignInRequest)sent.Payload).SignInId, Outcome = LicenseSignInOutcomes.Added, Message = "Signed in.", FinishedAtUtc = T0 }),
            _ => FakeLicenseDirectors.Ok(new { delivered = true }),
        });
        relay.Accept(One, "dir-target", PublicHalf, Request(SignIn1, A));
        relay.Accept(One, "dir-target", PublicHalf, Request(SignIn2, A));
        relay.Accept(One, "dir-target", PublicHalf, Request("33333333333333333333333333333333", C));
        await relay.WhenIdleAsync();

        var signIns = new LicenseSignInService(_log, new LicenseReadModel(_store), _directors.IsConnected, _directors.SendAsync,
            _directors.EnterScope, () => T0, CancellationToken.None);
        var machine = new LicenseReadModel(_store).Read(One, T0).Machines.Single(m => m.MachineName == "the-windows-machine");
        Assert.True(signIns.Start(One, A, machine.MachineId.ToString("N"), LicenseSignInReasons.Manual).Accepted);
        await signIns.WhenIdleAsync();

        // What was meant to happen did: the sealed value reached the asking Director, and only on the approved relay.
        var answers = _directors.Of(LicenseContract.ApprovalResultVerb).Select(s => (LicenseApprovalResult)s.Payload).ToList();
        Assert.Equal(3, answers.Count);
        Assert.Equal(SealedValue, answers.Single(a => a.SignInId == SignIn1).SealedApproval);
        Assert.All(answers.Where(a => a.SignInId != SignIn1), a => Assert.Null(a.SealedApproval));
        Assert.Equal(2, _directors.Of(LicenseContract.ApproveVerb).Count(s => ((LicenseApproveRequest)s.Payload).AuthorizeUrl == Address));
    }

    private static LicenseApprovalRequest Request(string signInId, string account)
        => new() { SignInId = signInId, AccountEmail = account, AuthorizeUrl = Address, NotAfterUtc = T0.AddMinutes(3) };

    [Fact]
    public void NoStoredTable_HasAColumnForTheAddressOrTheApproval()
    {
        using var ctx = _db.CreateUnscopedContext();
        var columns = ctx.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties().Select(p => $"{entity.GetTableName()}.{p.GetColumnName()}"))
            .ToList();

        // The model was read: the tables this work writes to are in it.
        Assert.Contains(columns, c => c.StartsWith("license_switch_log.", StringComparison.Ordinal));
        Assert.Contains(columns, c => c.StartsWith("tenant_settings.", StringComparison.Ordinal));

        var licenseColumns = columns.Where(c => c.StartsWith("license_", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(licenseColumns, c => Has(c, "authoriz") || Has(c, "url") || Has(c, "sealed") || Has(c, "approval") || Has(c, "challenge"));
        Assert.DoesNotContain(columns, c => Has(c, "authorizeurl") || Has(c, "authorize_url") || Has(c, "sealedapproval") || Has(c, "sealed_approval"));

        static bool Has(string column, string word) => column.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AfterRelaysAndASignIn_TheBytesOfTheDatabaseContainNeitherTheAddressNorTheApproval()
    {
        await EverythingHappensOnce();

        var stored = DatabaseBytes();

        // The search can find what IS stored: the browser profile, the sign-in row and its sentence are in these bytes.
        Assert.True(Contains(stored, "the-windows-machine"));
        Assert.True(Contains(stored, "Signed in."));
        Assert.True(Contains(stored, A));
        Assert.False(Contains(stored, AddressMark));
        Assert.False(Contains(stored, "claude.example/authorize"));
        Assert.False(Contains(stored, SealedValue));
    }

    [Fact]
    public async Task NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier()
    {
        using var log = FileLog.RedirectForTests();
        FileLog.Start();
        await EverythingHappensOnce();

        var lines = log.DrainAndReadLines();

        // The relay and the sign-in DID log, by identifier, account, machines and outcome.
        Assert.Contains(lines, l => l.Contains("[LicenseApprovalRelay]") && l.Contains(SignIn1) && l.Contains(LicenseApprovalOutcomes.Approved));
        Assert.Contains(lines, l => l.Contains("[LicenseApprovalRelay]") && l.Contains(SignIn2) && l.Contains(LicenseRenewalErrors.WrongAccount));
        Assert.Contains(lines, l => l.Contains("[LicenseApprovalRelay]") && l.Contains("33333333333333333333333333333333") && l.Contains(LicenseRenewalErrors.NoApprover));
        Assert.Contains(lines, l => l.Contains("[LicenseSignInService]") && l.Contains(LicenseSignInOutcomes.Added));
        Assert.Contains(lines, l => l.Contains("the-mac"));

        Assert.DoesNotContain(lines, l => l.Contains(AddressMark, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("claude.example", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains(SealedValue, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(PublicHalf, StringComparison.Ordinal));
    }

    /// <summary>Every byte SQLite has written for this database: the file and the journals beside it.</summary>
    private byte[] DatabaseBytes()
    {
        var all = new MemoryStream();
        foreach (var path in new[] { _db.Path, _db.Path + "-wal", _db.Path + "-journal" }.Where(File.Exists))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.CopyTo(all);
        }
        Assert.True(all.Length > 0, "the database file was not found");
        return all.ToArray();
    }

    private static bool Contains(byte[] haystack, string text)
        => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || haystack.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
}
