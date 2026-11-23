using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Broadcast;

public class BroadcastCapabilityTests(ITestContextAccessor testContextAccessor)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private CancellationToken Token => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task A_server_that_runs_the_group_command_is_supported_and_the_probe_key_is_deleted()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings();

        _ = sut.Start(settings);
        var state = await sut.WaitDecidedAsync(Wait, Token);

        state.Should().Be(StreamsState.Supported);
        await database.Received(1).StreamCreateConsumerGroupAsync(StreamsFakes.ProbeKey(settings), "g", StreamPosition.NewMessages, true, CommandFlags.DemandMaster);
        await database.Received(2).KeyDeleteAsync(StreamsFakes.ProbeKey(settings), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task A_probe_key_that_cannot_be_deleted_does_not_make_the_server_unsupported()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(true), Task.FromException<bool>(StreamsFakes.NoPermission()));
        var sut = StreamsFakes.Capability(connector);

        _ = sut.Start(StreamsFakes.Settings());

        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Supported);
    }

    [Fact]
    public async Task An_unknown_command_makes_the_server_unsupported_with_one_warning()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.ProbeFails(StreamsFakes.UnknownCommand());
        var logger = new RecordedLogger();
        var sut = StreamsFakes.Capability(connector);

        _ = sut.Start(StreamsFakes.Settings(logger: logger));
        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Unsupported);
        sut.Refused(StreamsFakes.UnknownCommand()).Should().BeFalse("it is decided already");

        sut.Reason.Should().Contain("XGROUP CREATE was refused");
        logger.Records.Where(r => r.Level >= LogLevel.Warning).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task A_missing_permission_makes_the_server_unsupported()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.ProbeFails(StreamsFakes.NoPermission());
        var sut = StreamsFakes.Capability(connector);

        _ = sut.Start(StreamsFakes.Settings());

        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Unsupported);
    }

    [Fact]
    public async Task A_timeout_leaves_the_state_unknown_and_the_probe_tries_again()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var attempts = 0;
        database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>())
            .Returns(_ => ++attempts < 3
                ? Task.FromException<bool>(new RedisTimeoutException(CommandFlags.None, "timed out", CommandStatus.Unknown))
                : Task.FromResult(true));
        var sut = StreamsFakes.Capability(connector);

        _ = sut.Start(StreamsFakes.Settings());
        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Supported);

        attempts.Should().Be(3);
    }

    [Theory]
    [InlineData("LOADING Redis is loading the dataset in memory")]
    [InlineData("READONLY You can't write against a read only replica.")]
    [InlineData("ERR syntax error")]
    public void A_server_that_is_busy_or_a_replica_is_not_a_refusal(string message)
    {
        RedisUtils.IsServerRefusal(new RedisServerException(RedisErrorKind.None, CommandFlags.None, message)).Should().BeFalse();
    }

    [Fact]
    public void A_client_side_command_error_and_a_timeout_are_not_a_refusal()
    {
        RedisUtils.IsServerRefusal(new RedisCommandException("This operation has been disabled in the command-map and cannot be used: XGROUP")).Should().BeFalse();
        RedisUtils.IsServerRefusal(new RedisTimeoutException(CommandFlags.None, "timed out", CommandStatus.Unknown)).Should().BeFalse();
        RedisUtils.IsServerRefusal(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down")).Should().BeFalse();
    }

    [Fact]
    public async Task A_refusal_of_a_command_the_probe_never_ran_makes_the_state_unsupported_and_is_raised_once()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        var sut = StreamsFakes.Capability(connector);
        var raised = 0;
        sut.Changed += () => raised++;

        sut.Refused(new RedisCommandException("disabled in the command-map")).Should().BeFalse();
        sut.State.Should().Be(StreamsState.Unknown);
        sut.Refused(StreamsFakes.NoPermission()).Should().BeTrue();
        sut.Refused(StreamsFakes.UnknownCommand()).Should().BeFalse();

        sut.State.Should().Be(StreamsState.Unsupported);
        raised.Should().Be(1);
        (await sut.WaitDecidedAsync(TimeSpan.Zero, Token)).Should().Be(StreamsState.Unsupported);
    }

    [Fact]
    public async Task A_refusal_after_the_probe_found_the_server_supported_wins()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        var sut = StreamsFakes.Capability(connector);
        _ = sut.Start(StreamsFakes.Settings());
        await sut.WaitDecidedAsync(Wait, Token);

        sut.Refused(StreamsFakes.UnknownCommand()).Should().BeTrue();

        sut.State.Should().Be(StreamsState.Unsupported);
    }

    [Fact]
    public async Task A_server_that_keeps_the_groups_of_a_deleted_stream_is_unsupported_with_one_warning()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.StreamReadGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<int?>(), Arg.Any<CommandFlags>()).Returns([]);
        var logger = new RecordedLogger();
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings(logger: logger);

        _ = sut.Start(settings);

        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Unsupported);
        sut.Reason.Should().Contain("keeps the consumer groups");
        logger.Records.Where(r => r.Level >= LogLevel.Warning).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        await database.Received(2).KeyDeleteAsync(StreamsFakes.ProbeKey(settings), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task A_NOGROUP_answer_to_the_re_read_is_supported_and_the_probe_key_is_deleted()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings();

        _ = sut.Start(settings);

        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Supported);
        await database.Received(1).StreamAddAsync(StreamsFakes.ProbeKey(settings), "f", "1", Arg.Any<RedisValue?>(), Arg.Any<long?>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<StreamTrimMode>(), CommandFlags.DemandMaster);
        await database.Received(1).StreamReadGroupAsync(StreamsFakes.ProbeKey(settings), "g", Arg.Any<RedisValue>(), StreamPosition.NewMessages, 1, CommandFlags.DemandMaster);
        await database.Received(2).KeyDeleteAsync(StreamsFakes.ProbeKey(settings), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task The_opt_out_sends_no_XADD_or_XREADGROUP_and_only_the_cleanup_DEL()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings(useOnQuirkServers: true);

        _ = sut.Start(settings);

        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Supported);
        await database.DidNotReceive().StreamAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<long?>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<StreamTrimMode>(), Arg.Any<CommandFlags>());
        await database.DidNotReceive().StreamReadGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<int?>(), Arg.Any<CommandFlags>());
        await database.Received(1).KeyDeleteAsync(StreamsFakes.ProbeKey(settings), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task A_timeout_during_the_check_leaves_the_state_unknown_and_the_probe_tries_again()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var attempts = 0;
        database.StreamReadGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<int?>(), Arg.Any<CommandFlags>())
            .Returns(_ => ++attempts < 3
                ? Task.FromException<StreamEntry[]>(new RedisTimeoutException(CommandFlags.None, "timed out", CommandStatus.Unknown))
                : Task.FromException<StreamEntry[]>(StreamsFakes.NoGroup()));
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings();

        _ = sut.Start(settings);

        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Supported);
        attempts.Should().Be(3);
        await database.Received(6).KeyDeleteAsync(StreamsFakes.ProbeKey(settings), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task A_timeout_of_the_XADD_of_the_check_stays_unknown_while_it_lasts_and_the_key_is_deleted()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.StreamAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<long?>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<StreamTrimMode>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisTimeoutException(CommandFlags.None, "timed out", CommandStatus.Unknown));
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings();

        _ = sut.Start(settings);

        (await sut.WaitDecidedAsync(TimeSpan.FromMilliseconds(100), Token)).Should().Be(StreamsState.Unknown);
        await database.Received().KeyDeleteAsync(StreamsFakes.ProbeKey(settings), CommandFlags.DemandMaster);
        Stop(connector);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refusal_of_the_XADD_of_the_check_is_unsupported_and_the_key_is_deleted(bool unknownCommand)
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.StreamAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<long?>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<StreamTrimMode>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(unknownCommand ? StreamsFakes.UnknownCommand() : StreamsFakes.NoPermission());
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings();

        _ = sut.Start(settings);

        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Unsupported);
        sut.Reason.Should().Contain("was refused");
        await database.Received(2).KeyDeleteAsync(StreamsFakes.ProbeKey(settings), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task The_probe_runs_once_however_many_start_it()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>()).Returns(_ => release.Task);
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings();

        var first = sut.Start(settings);
        var second = sut.Start(settings);
        BroadcastCapability.For(connector).Should().BeSameAs(sut);
        release.SetResult(true);
        await Task.WhenAll(first, second);

        second.Should().BeSameAs(first);
        sut.State.Should().Be(StreamsState.Supported);
        await database.Received(1).StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Two_capabilities_probe_on_keys_of_their_own_under_the_configured_key()
    {
        var first = StreamsFakes.Connector(out var firstDatabase, out _);
        var second = StreamsFakes.Connector(out var secondDatabase, out _);
        var settings = StreamsFakes.Settings();
        var one = StreamsFakes.Capability(first);
        var two = StreamsFakes.Capability(second);

        _ = one.Start(settings);
        _ = two.Start(settings);
        await one.WaitDecidedAsync(Wait, Token);
        await two.WaitDecidedAsync(Wait, Token);

        var keys = StreamsFakes.ProbeKeysUsed(firstDatabase).Concat(StreamsFakes.ProbeKeysUsed(secondDatabase)).ToList();
        keys.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        keys.Should().OnlyContain(key => key.StartsWith(settings.ProbeKey.ToString(), StringComparison.Ordinal) && key.Length > settings.ProbeKey.ToString().Length);
        StreamsFakes.ProbeKeysUsed(firstDatabase).Should().ContainSingle("every command of one probe uses one key");
    }

    [Fact]
    public async Task The_probe_key_is_given_an_expiry_so_a_crashed_node_leaves_nothing_behind()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var sut = StreamsFakes.Capability(connector);
        var settings = StreamsFakes.Settings();

        _ = sut.Start(settings);
        await sut.WaitDecidedAsync(Wait, Token);

        await database.Received(1).KeyExpireAsync(StreamsFakes.ProbeKey(settings), TimeSpan.FromSeconds(60), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task A_group_that_exists_already_leaves_the_state_unknown_and_the_probe_tries_again()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.ProbeFails(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "BUSYGROUP Consumer Group name already exists"));
        var sut = StreamsFakes.Capability(connector);

        _ = sut.Start(StreamsFakes.Settings());

        (await sut.WaitDecidedAsync(TimeSpan.FromMilliseconds(200), Token)).Should().Be(StreamsState.Unknown);
        sut.State.Should().Be(StreamsState.Unknown);
        var attempts = database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.StreamCreateConsumerGroupAsync));
        attempts.Should().BeGreaterThan(1);
        Stop(connector);
    }

    [Fact]
    public async Task The_probe_goes_on_when_the_caller_that_started_it_stops_waiting_and_decides_for_the_next_one()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>()).Returns(_ => release.Task);
        var sut = StreamsFakes.Capability(connector);
        using var firstCaller = new CancellationTokenSource();
        _ = sut.Start(StreamsFakes.Settings());
        var waiting = sut.WaitDecidedAsync(Wait, firstCaller.Token);

        await firstCaller.CancelAsync();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult(true);

        (await sut.WaitDecidedAsync(Wait, Token)).Should().Be(StreamsState.Supported);
    }

    [Fact]
    public async Task A_connector_that_is_disposed_ends_the_probe_with_the_state_unknown()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        ValueTaskStubs.Returns(connector.ConnectAsync(Arg.Any<CancellationToken>()), _ => ValueTask.FromException(new ObjectDisposedException("connector")));
        var sut = StreamsFakes.Capability(connector);

        await sut.Start(StreamsFakes.Settings()).WaitAsync(Wait, Token);

        sut.State.Should().Be(StreamsState.Unknown);
    }

    [Fact]
    public async Task Later_settings_that_differ_are_warned_about_once_naming_both()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        var sut = StreamsFakes.Capability(connector);
        var firstLogger = new RecordedLogger();
        var laterLogger = new RecordedLogger();
        var first = StreamsFakes.Settings(StreamsUnsupportedAction.PubSub, logger: firstLogger);
        var later = StreamsFakes.Settings(StreamsUnsupportedAction.Disable, logger: laterLogger);

        await sut.Start(first).WaitAsync(Wait, Token);
        _ = sut.Start(later);
        _ = sut.Start(later);
        _ = sut.Start(StreamsFakes.Settings(StreamsUnsupportedAction.PubSub, logger: laterLogger));

        var (_, message) = laterLogger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning).Subject;
        message.Should().Contain("StreamsUnsupported=PubSub").And.Contain("StreamsUnsupported=Disable");
        firstLogger.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Waiting_for_a_probe_that_cannot_tell_gives_back_unknown_when_the_time_is_up()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), Arg.Any<bool>(), Arg.Any<CommandFlags>())
            .Returns(_ => new TaskCompletionSource<bool>().Task);
        var sut = StreamsFakes.Capability(connector);
        _ = sut.Start(StreamsFakes.Settings());

        (await sut.WaitDecidedAsync(TimeSpan.FromMilliseconds(50), Token)).Should().Be(StreamsState.Unknown);
    }

    [Fact]
    public async Task Waiting_is_cut_short_by_the_callers_token()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        var sut = StreamsFakes.Capability(connector);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await FluentActions.Awaiting(() => sut.WaitDecidedAsync(Wait, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(StreamsUnsupportedAction.PubSub, LogLevel.Warning)]
    [InlineData(StreamsUnsupportedAction.Disable, LogLevel.Warning)]
    [InlineData(StreamsUnsupportedAction.Fail, LogLevel.Error)]
    public void The_one_log_of_a_refusal_follows_the_action(StreamsUnsupportedAction action, LogLevel expected)
    {
        var connector = StreamsFakes.Connector(out _, out _);
        var logger = new RecordedLogger();
        var sut = StreamsFakes.Capability(connector);
        sut.Attach(StreamsFakes.Settings(action, logger: logger));

        sut.Refused(StreamsFakes.UnknownCommand());
        sut.Refused(StreamsFakes.NoPermission());

        logger.Records.Should().ContainSingle().Which.Level.Should().Be(expected);
    }

    [Fact]
    public void Streams_work_is_allowed_until_the_server_refuses()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        var sut = StreamsFakes.Capability(connector);
        sut.AllowsStreamWork.Should().BeTrue("nothing has probed it");

        sut.Refused(StreamsFakes.UnknownCommand());

        sut.AllowsStreamWork.Should().BeFalse();
    }

    // A probe that cannot decide retries for as long as the connector lives: a disposed connector ends it, as the test's end would.
    private static void Stop(IRedisConnector connector) =>
        ValueTaskStubs.Returns(connector.ConnectAsync(Arg.Any<CancellationToken>()), _ => ValueTask.FromException(new ObjectDisposedException("connector")));
}
