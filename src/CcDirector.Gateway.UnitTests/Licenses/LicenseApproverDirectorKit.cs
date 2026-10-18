using CcDirector.Core.Licenses;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The approving Director's real daily approver check (<see cref="LicenseApproverCheck"/>), wired straight to the
/// Gateway's real <see cref="LicenseApproverRegistration"/> with no hub between them, so a test can watch the whole loop:
/// the Gateway marks a record due, the Director checks it, the Gateway stores the result. The license tool is a fake that
/// answers every approver check "can approve" for the account it was asked about; no browser opens anywhere. The key
/// holder lock lives in a folder of its own, deleted on dispose.
/// </summary>
internal sealed class LicenseApproverDirectorKit : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "approver-director-" + Guid.NewGuid().ToString("N"));
    private readonly LicenseKeyHolderLock _holder;

    public LicenseApproverDirectorKit(LicenseApproverRegistration registration, TenantId tenant, string directorId, Func<DateTime> utcNow)
    {
        Directory.CreateDirectory(_folder);
        _holder = new LicenseKeyHolderLock(_folder);
        if (!_holder.TryAcquire())
            throw new InvalidOperationException("The test's own key holder lock could not be taken.");
        Tool = new CanApproveTool();
        Check = new LicenseApproverCheck(Tool, _holder, new LicenseBrowserLane(), new NoProfiles(), utcNow, enabled: true, offersApprovers: false);
        Check.AttachChannel(new RegistrationChannel(registration, tenant, directorId));
    }

    public LicenseApproverCheck Check { get; }

    public CanApproveTool Tool { get; }

    public void Dispose()
    {
        _holder.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>The license tool: every approver check finds the profile signed in as the account asked about.</summary>
    internal sealed class CanApproveTool : ILicenseCommandLine
    {
        public List<(string Profile, string? Email)> ApproverChecks { get; } = new();

        public bool IsInstalled => true;

        public Task<LicenseApproverToolAnswer> CheckApproverAsync(string browserProfile, string? accountEmail, CancellationToken cancellationToken)
        {
            ApproverChecks.Add((browserProfile, accountEmail));
            return Task.FromResult(new LicenseApproverToolAnswer
            {
                State = LicenseApproverStates.CanApprove, AccountEmail = accountEmail, ShownAccount = accountEmail, ToolAnswered = true,
            });
        }

        public IReadOnlyList<LicenseHandSwitch> TakeHandSwitches() => Array.Empty<LicenseHandSwitch>();
        public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<LicenseRenewalRow>> CheckRenewalsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool?> HasBrowserProfileAsync(string accountEmail, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CcDirector.Core.Licenses.LicenseSignInStart> StartSignInAsync(string accountEmail, bool askAnotherMachineWhenNoBrowser, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LicenseApproveAnswer> ApproveAsync(string authorizeUrl, string accountEmail, string browserProfile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoProfiles : ILicenseBrowserProfiles
    {
        public IReadOnlyList<LicenseBrowserProfile> List() => Array.Empty<LicenseBrowserProfile>();
    }

    /// <summary>The Director's hub calls, answered by the Gateway's registration for the bound tenant and Director.</summary>
    private sealed class RegistrationChannel(LicenseApproverRegistration registration, TenantId tenant, string directorId) : ILicenseApproverChannel
    {
        public bool CanReach => true;

        public Task<LicenseApproverAssignments> GetAssignmentsAsync(CancellationToken cancellationToken)
            => Task.FromResult(registration.Assignments(tenant, directorId));

        public Task<LicenseApproverCheckAnswer> ReportAsync(LicenseApproverCheckReport report, CancellationToken cancellationToken)
            => Task.FromResult(registration.Report(tenant, directorId, report));
    }
}
