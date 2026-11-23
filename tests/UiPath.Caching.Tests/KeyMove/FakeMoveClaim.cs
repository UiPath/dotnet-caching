using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

internal sealed class FakeMoveClaim : IMoveClaim
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private bool _opened;

    public List<string> Calls { get; } = [];

    public List<LeaseFence?> Fences { get; } = [];

    public ClaimOutcome Pend { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Open { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Reopen { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Certify { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Idle { get; set; } = ClaimOutcome.Done;

    public ClaimStatus? ForcedStatus { get; set; }

    public string? AcquiredToken { get; set; }

    public List<string> Acquired { get; } = [];

    public bool GuardBlocks { get; set; }

    public List<(string Token, ConnectionProof? Marker)> Guarded { get; } = [];

    public Task<TransitionResult?> CommitGuardAsync(string group, string token, ConnectionProof? marker, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Guarded.Add((token, marker));
        }

        return Task.FromResult<TransitionResult?>(GuardBlocks ? new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "guard") : null);
    }

    public Task<string?> AcquireTokenAsync(string group, LeaseFence? fence, CancellationToken cancellationToken)
    {
        var token = AcquiredToken ?? MoveScripts.NewNonce();
        lock (_gate)
        {
            Acquired.Add(token);
        }

        return Task.FromResult<string?>(token);
    }

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
        lock (_gate)
        {
            Calls.Add(call);
            Fences.Add(fence);
        }

        return Task.FromResult(outcome);
    }
}
