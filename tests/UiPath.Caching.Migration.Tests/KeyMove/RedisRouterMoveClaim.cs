using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>Binds the driver's router claim to the real Redis claim store.</summary>
internal sealed class RedisRouterMoveClaim(ClaimStore store, IGroupFacts facts) : IMoveClaim
{
    public bool InterruptOpen { get; set; }

    public async Task<ClaimOutcome> PendAsync(string group, string token, string descriptor, LeaseFence? fence, CancellationToken cancellationToken) =>
        Outcome(await store.PendAsync(Field(group), token, descriptor, string.Empty).WaitAsync(cancellationToken));

    public async Task<ClaimOutcome> OpenAsync(string group, string token, bool restore, LeaseFence? fence, CancellationToken cancellationToken) =>
        InterruptOpen ? ClaimOutcome.Failed : Outcome(await store.OpenAsync(Field(group), token, facts.ClaimDescriptor(group), restore).WaitAsync(cancellationToken));

    public async Task<ClaimOutcome> ReopenAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) =>
        Outcome(await store.ReopenAsync(Field(group), token).WaitAsync(cancellationToken));

    public async Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, string terminal, LeaseFence? fence, CancellationToken cancellationToken) =>
        Outcome(await store.CertifyAsync(Field(group), token, ep, terminal == "S5").WaitAsync(cancellationToken));

    public async Task<ClaimOutcome> ReleaseIdleAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) =>
        Outcome(await store.IdleAsync(Field(group), token).WaitAsync(cancellationToken));

    public async Task<ClaimStatus> StatusAsync(string group, string token, CancellationToken cancellationToken)
    {
        var record = await store.ReadAsync().WaitAsync(cancellationToken);
        return record.Moves.GetValueOrDefault(Field(group)) switch
        {
            { Status: ClaimGrammar.Pending } claim when claim.Token == token => ClaimStatus.Pending,
            { Status: ClaimGrammar.Open } claim when claim.Token == token => ClaimStatus.Open,
            _ => ClaimStatus.Absent,
        };
    }

    private static ClaimOutcome Outcome(long code) => code switch
    {
        ClaimCodes.Done or ClaimCodes.AlreadyDone => ClaimOutcome.Done,
        ClaimCodes.Refused or ClaimCodes.OtherGroup or ClaimCodes.OtherMove or ClaimCodes.PointerChanged or ClaimCodes.TokenSpent => ClaimOutcome.Refused,
        ClaimCodes.FallbackExhausted or ClaimCodes.BadArgument or ClaimCodes.OwnMalformed => ClaimOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown claim result."),
    };

    private static ClaimOutcome Outcome(ClaimResult result) =>
        result.Durability == ClaimDurability.NotConfirmed ? ClaimOutcome.Failed : Outcome(result.Code);

    private string Field(string group) => $"{facts.ClaimDescriptor(group)}:{group}";
}
