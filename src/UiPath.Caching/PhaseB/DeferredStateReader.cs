using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The reader a node's snapshot loop is built with before the connection exists; until it is attached every read fails, which the loop treats as no answer.</summary>
internal sealed class DeferredStateReader : IStateReader
{
    private volatile IStateReader? _inner;

    private IStateReader Inner => _inner ?? throw new RedisException("The state connection is not up yet.");

    public void Attach(IStateReader reader) => _inner = reader;

    public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken) => Inner.ReadAsync(group, cancellationToken);

    public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) => Inner.ProbeAsync(group, cancellationToken);

    public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) => Inner.SampleAsync(group, cancellationToken);
}
