namespace UiPath.Caching.KeyMove;

/// <summary>The claim of a group that takes none: every step fails, and nothing asks for one while <see cref="IGroupFacts.TakesClaim"/> is false.</summary>
internal sealed class UnavailableMoveClaim : IMoveClaim
{
    public Task<ClaimOutcome> PendAsync(string group, string token, string descriptor, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

    public Task<ClaimOutcome> OpenAsync(string group, string token, bool restore, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

    public Task<ClaimOutcome> ReopenAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

    public Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, string terminal, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

    public Task<ClaimOutcome> ReleaseIdleAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

    public Task<ClaimStatus> StatusAsync(string group, string token, CancellationToken cancellationToken) => Task.FromResult(ClaimStatus.Absent);
}
