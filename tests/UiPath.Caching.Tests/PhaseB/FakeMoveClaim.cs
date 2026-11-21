using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

internal sealed class FakeMoveClaim : IMoveClaim
{
    public List<string> Calls { get; } = [];

    public ClaimOutcome Pend { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Open { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Reopen { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Certify { get; set; } = ClaimOutcome.Done;

    public ClaimOutcome Idle { get; set; } = ClaimOutcome.Done;

    public Task<ClaimOutcome> PendAsync(string group, string token, string descriptor, CancellationToken cancellationToken) => Record($"pend:{descriptor}", Pend);

    public Task<ClaimOutcome> OpenAsync(string group, string token, bool restore, CancellationToken cancellationToken) => Record("open", Open);

    public Task<ClaimOutcome> ReopenAsync(string group, string token, CancellationToken cancellationToken) => Record("reopen", Reopen);

    public Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, string terminal, CancellationToken cancellationToken) => Record($"cert:{terminal}", Certify);

    public Task<ClaimOutcome> ReleaseIdleAsync(string group, string token, CancellationToken cancellationToken) => Record("idle", Idle);

    private Task<ClaimOutcome> Record(string call, ClaimOutcome outcome)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }

        return Task.FromResult(outcome);
    }
}
