using StackExchange.Redis;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>A fake multiplexer that counts the handlers attached to its connection events.</summary>
internal sealed class EventCountingMultiplexer
{
    private int _attached;

    public EventCountingMultiplexer() =>
        Multiplexer = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, _) =>
        {
            if (method.Name == "add_ConnectionFailed")
            {
                Interlocked.Increment(ref _attached);
            }
            else if (method.Name == "remove_ConnectionFailed")
            {
                Interlocked.Decrement(ref _attached);
            }
            else if (!method.Name.StartsWith("add_", StringComparison.Ordinal) && !method.Name.StartsWith("remove_", StringComparison.Ordinal))
            {
                throw new NotSupportedException(method.Name);
            }

            return null;
        });

    public IConnectionMultiplexer Multiplexer { get; }

    /// <summary>The handlers of ConnectionFailed now attached; the counter attaches every event together.</summary>
    public int Attached => Volatile.Read(ref _attached);
}
