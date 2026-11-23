using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

internal sealed class FakeMoveClaim : IMoveClaim
{
    private bool _opened;

    public List<string> Calls { get; } = [];

    public List<LeaseFence?> Fences { get; } = [];

    public ClaimOutcome Pend { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Open { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Reopen { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Certify { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Idle { get; set; } = ClaimOutcome.Done;

    public ClaimStatus? ForcedStatus { get; set; }

    public Task<ClaimOutcome> PendAsync(string group, string token, string descriptor, LeaseFence? fence, CancellationToken cancellationToken) => Record($"pend:{descriptor}", Pend, fence);

    public Task<ClaimOutcome> OpenAsync(string group, string token, bool restore, LeaseFence? fence, CancellationToken cancellationToken)
    {
        _opened |= Open == ClaimOutcome.Done;
        return Record("open", Open, fence);
    }

    public Task<ClaimOutcome> ReopenAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) => Record("reopen", Reopen, fence);

    public Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, string terminal, LeaseFence? fence, CancellationToken cancellationToken) => Record($"cert:{terminal}", Certify, fence);

    public Task<ClaimOutcome> ReleaseIdleAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken)
    {
        var outcome = Record("idle", Idle, fence);
        _opened = false;
        return outcome;
    }

    public Task<ClaimStatus> StatusAsync(string group, string token, CancellationToken cancellationToken) =>
        Task.FromResult(ForcedStatus ?? (Calls.Contains("pend:rt") || Calls.Any(c => c.StartsWith("pend:", StringComparison.Ordinal)) ? (_opened ? ClaimStatus.Open : ClaimStatus.Pending) : ClaimStatus.Absent));

    private Task<ClaimOutcome> Record(string call, ClaimOutcome outcome, LeaseFence? fence)
    {
        lock (Calls)
        {
            Calls.Add(call);
            Fences.Add(fence);
        }

        return Task.FromResult(outcome);
    }
}
