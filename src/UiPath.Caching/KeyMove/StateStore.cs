using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The state hash of each group through the view with no connector prefix, and the control record probe of its slots.</summary>
internal sealed class StateStore(IDatabase stateDatabase, MoveKeyNames names, MoveScripts scripts, ControlSampler sampler, TimeProvider time) : IStateReader
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    public async Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken) =>
        StateRecord.Parse(await stateDatabase.HashGetAllAsync(names.State(group), Master).WaitAsync(cancellationToken).ConfigureAwait(false));

    public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) => sampler.ProbeAsync(group, cancellationToken);

    public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) => sampler.SampleAsync(group, cancellationToken);

    public Task<long> ServerMicrosecondsAsync(string group) => scripts.ServerMicrosecondsAsync(names.State(group));

    /// <summary>X0 for the managed cache ids <paramref name="cacheIds"/>: creates the S0 state, or refreshes the set of an S0 state; any later stage is refused.</summary>
    public async Task<InitResult> InitAsync(string group, IEnumerable<string> cacheIds, CancellationToken cancellationToken)
    {
        var caches = StateRecord.EncodeCaches(cacheIds);
        var sample = await sampler.SampleAsync(group, cancellationToken).ConfigureAwait(false);
        return await scripts.InitAsync(names.State(group), caches, StateRecord.HashOfCaches(caches), sample?.Ep ?? 0, time.GetUtcNow().ToUnixTimeMilliseconds()).ConfigureAwait(false);
    }

    /// <summary>ALLACK: stores that every fresh node acknowledged <paramref name="id"/>, when it is still the stored identity.</summary>
    public async Task<bool> AllAckAsync(string group, string id, LeaseFence? fence = null, long? t3At = null) =>
        await scripts.AllAckAsync(names.State(group), id, fence, t3At).ConfigureAwait(false) == MoveScripts.Written;

    public async Task<bool> ClearAutomaticHoldAsync(string group, LeaseFence fence) =>
        await scripts.HoldClearAsync(names.State(group), fence, StateFields.HoldAuto).ConfigureAwait(false) == MoveScripts.Written;

    /// <summary>Stores a hold with its reason on an existing state; false when the group has no hash.</summary>
    public async Task<bool> SetHoldAsync(string group, HoldKind kind, string reason, LeaseFence? fence = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return await scripts.HoldSetAsync(names.State(group), HoldField(kind), reason, fence).ConfigureAwait(false) == MoveScripts.Written;
    }

    /// <summary>Deletes the operator hold, the automatic hold, or both (<c>Resume</c>); false when the group has no hash.</summary>
    public async Task<bool> ClearHoldAsync(string group, params HoldKind[] kinds) =>
        await scripts.HoldClearAsync(names.State(group), null, [.. kinds.Select(HoldField)]).ConfigureAwait(false) == MoveScripts.Written;

    private static string HoldField(HoldKind kind) => kind == HoldKind.Operator ? StateFields.HoldOp : StateFields.HoldAuto;
}
