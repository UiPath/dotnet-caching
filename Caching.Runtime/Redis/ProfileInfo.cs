using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

public sealed record ProfileInfo(int Count, string? SessionId, IEnumerable<IProfiledCommand> Commands);
