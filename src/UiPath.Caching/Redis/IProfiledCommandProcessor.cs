using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

public interface IProfiledCommandProcessor
{
    public void Process(IProfiledCommand command, string? sessionId);
}
