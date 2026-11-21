namespace UiPath.Caching.PhaseB;

/// <summary>The move claim of a router group (DR-1) behind a seam; every step is idempotent, carries its own replication wait, and takes the leader's lease fence for the claim script to check.</summary>
internal interface IMoveClaim
{
    /// <summary>CLAIMPEND with an ownership token, before TRANSIT.</summary>
    Task<ClaimOutcome> PendAsync(string group, string token, string descriptor, LeaseFence? fence, CancellationToken cancellationToken);

    /// <summary>CLAIMOPEN before COMMIT; <paramref name="restore"/> recreates a missing field of a live move.</summary>
    Task<ClaimOutcome> OpenAsync(string group, string token, bool restore, LeaseFence? fence, CancellationToken cancellationToken);

    /// <summary>CLAIMREOPEN: the first step of X11, closed to open.</summary>
    Task<ClaimOutcome> ReopenAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken);

    /// <summary>CLAIMREL with the certificate (<paramref name="ep"/>, <paramref name="terminal"/>).</summary>
    Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, string terminal, LeaseFence? fence, CancellationToken cancellationToken);

    /// <summary>CLAIMREL idle, after the certificate and the state's copy of it, or alone for a claim that never opened.</summary>
    Task<ClaimOutcome> ReleaseIdleAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken);

    /// <summary>Whether the field of this token is pending, open or gone: an abandon of X1 certifies only an open claim.</summary>
    Task<ClaimStatus> StatusAsync(string group, string token, CancellationToken cancellationToken);
}
