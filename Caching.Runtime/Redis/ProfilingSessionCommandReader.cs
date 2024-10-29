using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

public sealed class ProfilingSessionCommandReader : IProfilingSessionCommandReader
{
    private static readonly ProfileInfo _emptyProfileInfo = new(0, null, []);

    public ProfileInfo Get(ProfilingSession? session)
    {
        if (session is null)
        {
            return _emptyProfileInfo;
        }

        var commands = session.FinishProfiling();
        return new(commands.Count(), session.UserToken?.ToString(), commands);
    }
}
