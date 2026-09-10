using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

public interface IProfiledCommandProcessor
{
    void Process(IProfiledCommand command, string? sessionId);
}
