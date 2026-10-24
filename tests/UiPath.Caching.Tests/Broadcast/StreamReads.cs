using NSubstitute.Core;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>For mocks that count or sequence a reader's new-entry reads: its reads of the pending list find nothing and are not passed on.</summary>
internal static class StreamReads
{
    public static Func<CallInfo, Task<StreamEntry[]>> NewOnly(Func<CallInfo, Task<StreamEntry[]>> answer) =>
        call => call.ArgAt<RedisValue?>(3) == StreamConstants.UndeliveredMessages ? answer(call) : Task.FromResult(Array.Empty<StreamEntry>());

    public static Func<CallInfo, Task<StreamEntry[]>> NewOnly(params Task<StreamEntry[]>[] answers)
    {
        var next = 0;
        return NewOnly(_ => answers[Math.Min(Interlocked.Increment(ref next), answers.Length) - 1]);
    }
}
