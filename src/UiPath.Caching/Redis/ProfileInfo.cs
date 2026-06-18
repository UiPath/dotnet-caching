using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

public sealed record ProfileInfo(int Count, string? SessionId, IEnumerable<IProfiledCommand> Commands);
