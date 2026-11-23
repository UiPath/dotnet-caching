using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>A connector, and the errors a server answers the Streams probe with.</summary>
internal static class StreamsFakes
{
    internal static RedisServerException UnknownCommand() => new(RedisErrorKind.None, CommandFlags.None, "ERR unknown command 'XGROUP', with args beginning with: 'CREATE'");

    internal static RedisServerException NoPermission() => new(RedisErrorKind.None, CommandFlags.None, "NOPERM User app has no permissions to run the 'xgroup' command");

    internal static IRedisConnector Connector(out IDatabase database, out IServer primary)
    {
        var connector = Substitute.For<IRedisConnector>();
        database = Substitute.For<IDatabase>();
        primary = Substitute.For<IServer>();
        database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>()).Returns(true);
        database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        database.StreamAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<long?>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<StreamTrimMode>(), Arg.Any<CommandFlags>()).Returns(new RedisValue("1-0"));
        database.RereadFails(NoGroup());
        connector.Database.Returns(database);
        connector.GetPrimaries().Returns([primary]);
        return connector;
    }

    internal static RedisServerException NoGroup() => new(RedisErrorKind.None, CommandFlags.None, "NOGROUP No such key 'app:streams:capability-probe' or consumer group 'g'");

    /// <summary>The group read of the quirk check fails with <paramref name="error"/>.</summary>
    internal static void RereadFails(this IDatabase database, Exception error) =>
        database.StreamReadGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<int?>(), Arg.Any<CommandFlags>()).ThrowsAsync(error);

    internal static void ProbeFails(this IDatabase database, Exception error) =>
        database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>()).ThrowsAsync(error);

    /// <summary>Matches the probe key of <paramref name="settings"/>, whatever the suffix that makes it a capability's own.</summary>
    internal static RedisKey ProbeKey(StreamsProbeSettings settings) =>
        Arg.Is<RedisKey>(key => key.ToString().StartsWith(settings.ProbeKey.ToString(), StringComparison.Ordinal));

    /// <summary>The distinct keys the group was created on.</summary>
    internal static List<string> ProbeKeysUsed(IDatabase database) => database.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IDatabase.StreamCreateConsumerGroupAsync))
        .Select(call => call.GetArguments()[0]!.ToString()!)
        .Distinct()
        .ToList();

    internal static StreamsProbeSettings Settings(StreamsUnsupportedAction action = StreamsUnsupportedAction.PubSub, bool useOnQuirkServers = false, ILogger? logger = null) =>
        new(new RedisKey("app:streams:capability-probe"), action, useOnQuirkServers, logger ?? new RecordedLogger());

    /// <summary>A capability for a connector of its own, with its retries sped up.</summary>
    internal static BroadcastCapability Capability(IRedisConnector connector)
    {
        var capability = BroadcastCapability.For(connector);
        capability.InitialRetryDelay = TimeSpan.FromMilliseconds(5);
        capability.MaxRetryDelay = TimeSpan.FromMilliseconds(20);
        return capability;
    }
}
