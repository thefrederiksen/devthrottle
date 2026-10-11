using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests.Licenses;

/// <summary>
/// License Switching contract 3.3 step 1 and 3.4: the <c>license-switch</c> tunnel verb. A payload it cannot read, or
/// a SwitchId of the wrong shape, is BadRequest with no body; every other outcome - a refusal included - is a
/// successful answer carrying a LicenseSwitchResult. Runs against a fake license tool; no sign-in exists anywhere.
/// </summary>
public sealed class LicenseSwitchExecutorTests : IDisposable
{
    private const string SwitchId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "license-executor-" + Guid.NewGuid().ToString("N"));
    private readonly LicenseSwitching _licenses;
    private readonly OneSpareTool _tool = new();

    public LicenseSwitchExecutorTests()
    {
        Directory.CreateDirectory(_folder);
        _licenses = new LicenseSwitching(_tool, _folder, () => DateTime.UtcNow, "the-windows-machine", "owner", enabled: true);
        Assert.True(_licenses.HolderLock.TryAcquire());
    }

    public void Dispose()
    {
        _licenses.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static DirectorCommand Command(string payload) =>
        new() { CommandId = "cmd-1", Verb = LicenseContract.SwitchVerb, SessionId = "", PayloadJson = payload };

    private static string Payload(string switchId, string target = "account-b@example.com") => JsonSerializer.Serialize(new LicenseSwitchRequest
    {
        SwitchId = switchId,
        TargetAccountEmail = target,
        ExpectedActiveAccountEmail = "account-a@example.com",
        Reason = LicenseSwitchReasons.Manual,
        RequestedAtUtc = DateTime.UtcNow,
        NotAfterUtc = DateTime.UtcNow + LicenseContract.SwitchNotAfter,
    }, Web);

    [Fact]
    public void AreaFor_LicenseSwitch_IsOwnedByTheLicenseArea()
    {
        Assert.IsType<LicenseSwitchExecutor>(SessionCommandExecutor.AreaFor(LicenseContract.SwitchVerb));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"switchId\":\"6F1C2A9E4B7D4E0F9A3C5B8D2E1F7A40\",\"targetAccountEmail\":\"account-b@example.com\"}")]
    [InlineData("{\"switchId\":\"../../escape\",\"targetAccountEmail\":\"account-b@example.com\"}")]
    [InlineData("{\"switchId\":\"6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\"}")]
    public async Task SwitchAsync_UnreadablePayloadOrBadSwitchId_IsBadRequestWithNoBody(string payload)
    {
        var result = await LicenseSwitchExecutor.SwitchAsync(_licenses.Coordinator, Command(payload), CancellationToken.None);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Null(result.BodyJson);
        Assert.Empty(_tool.Switches);
    }

    [Fact]
    public async Task SwitchAsync_GoodRequest_IsOkCarryingTheResultInCamelCase()
    {
        var result = await LicenseSwitchExecutor.SwitchAsync(_licenses.Coordinator, Command(Payload(SwitchId)), CancellationToken.None);

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        Assert.Contains("\"outcome\":\"switched\"", result.BodyJson);
        var body = JsonSerializer.Deserialize<LicenseSwitchResult>(result.BodyJson!, Web)!;
        Assert.Equal(SwitchId, body.SwitchId);
        Assert.Equal("account-b@example.com", body.ActiveAccountEmailAfter);
    }

    [Fact]
    public async Task SwitchAsync_Refusal_IsStillOk()
    {
        var result = await LicenseSwitchExecutor.SwitchAsync(_licenses.Coordinator, Command(Payload(SwitchId, target: "account-c@example.com")), CancellationToken.None);

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var body = JsonSerializer.Deserialize<LicenseSwitchResult>(result.BodyJson!, Web)!;
        Assert.Equal(LicenseSwitchOutcomes.Failed, body.Outcome);
        Assert.Equal(LicenseSwitchReasonCodes.SpareNotHeld, body.ReasonCode);
    }

    /// <summary>Account A active, account B spare; a switch exchanges them.</summary>
    private sealed class OneSpareTool : ILicenseCommandLine
    {
        private string _active = "account-a@example.com";
        public List<string> Switches { get; } = new();

        public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LicenseHolding>>(new[] { "account-a@example.com", "account-b@example.com" }
                .Select(e => new LicenseHolding { AccountEmail = e, IsActive = e == _active })
                .OrderByDescending(h => h.IsActive).ToList());

        public Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken) =>
            ListAsync(cancellationToken);

        public Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken)
        {
            Switches.Add(targetAccountEmail);
            var previous = _active;
            _active = targetAccountEmail;
            return Task.FromResult(new LicenseExchangeAnswer
            {
                Outcome = LicenseSwitchOutcomes.Switched,
                PreviousActiveAccountEmail = previous,
                ActiveAccountEmailAfter = _active,
            });
        }
    }
}
