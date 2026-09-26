using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace UiPath.Caching.Tests.Redis;

public sealed class ConnectorKeyPrefixTests : IDisposable
{
    // Never connects: the prefix wrappers are built locally, which is all the reflection read needs.
    private readonly ConnectionMultiplexer _multiplexer = ConnectionMultiplexer.Connect("127.0.0.1:1,abortConnect=false,connectTimeout=50");
    private readonly ILogger _logger = Substitute.For<ILogger>();

    public ConnectorKeyPrefixTests() => _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

    private int LoggedErrors => Logged(LogLevel.Error);

    [Fact]
    public void A_WithKeyPrefix_database_resolves_to_its_prefix_when_none_is_configured()
    {
        NewPrefix().Get(_multiplexer.GetDatabase().WithKeyPrefix("{app}:"), configured: string.Empty, _logger).Should().Be("{app}:");
    }

    [Fact]
    public void Nested_prefixes_resolve_to_what_the_server_receives()
    {
        NewPrefix().Get(_multiplexer.GetDatabase().WithKeyPrefix("a:").WithKeyPrefix("b:"), configured: string.Empty, _logger).Should().Be("a:b:");
    }

    [Fact]
    public void A_bare_database_resolves_to_no_prefix_whatever_is_configured()
    {
        NewPrefix().Get(_multiplexer.GetDatabase(), configured: "stale:", _logger).Should().BeEmpty();
    }

    [Fact]
    public void The_reflection_read_recognizes_the_current_StackExchange_Redis_wrapper()
    {
        // A StackExchange.Redis bump that renames the wrapper would fall back to the probe silently; fail here instead.
        ConnectorKeyPrefix.TryRead(_multiplexer.GetDatabase().WithKeyPrefix("p:"), out var prefix).Should().BeTrue();
        prefix.Should().Equal(System.Text.Encoding.UTF8.GetBytes("p:"));
    }

    [Fact]
    public void A_prefix_that_is_not_UTF8_is_skipped_for_the_configured_one()
    {
        NewPrefix().Get(_multiplexer.GetDatabase().WithKeyPrefix(new byte[] { 0xFF, (byte)':' }), "configured:", _logger).Should().Be("configured:");
    }

    [Theory]
    [InlineData('*')]
    [InlineData('?')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('\\')]
    public void A_prefix_holding_a_SCAN_glob_character_is_skipped_for_the_configured_one(char glob)
    {
        NewPrefix().Get(_multiplexer.GetDatabase().WithKeyPrefix($"app{glob}:"), "configured:", _logger).Should().Be("configured:");
    }

    [Theory]
    [InlineData("app*:")]
    [InlineData("[app]:")]
    public void A_configured_prefix_holding_a_SCAN_glob_character_is_skipped_for_no_prefix(string configured)
    {
        // "app*:" would otherwise widen the maintainer's SCAN to other apps' keys.
        var database = Substitute.For<IDatabase>();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).ThrowsAsync(new RedisException("down"));

        NewPrefix().Get(database, configured, _logger).Should().BeEmpty();
    }

    [Fact]
    public async Task A_skipped_configured_prefix_is_warned_about_once_and_not_as_a_mismatch()
    {
        var database = _multiplexer.GetDatabase().WithKeyPrefix("{app}:");
        var prefix = NewPrefix();

        (await prefix.GetAsync(database, "app*:", _logger, TestContext.Current.CancellationToken)).Should().Be("{app}:");
        await prefix.GetAsync(database, "app*:", _logger, TestContext.Current.CancellationToken);

        Logged(LogLevel.Warning).Should().Be(1);
        LoggedErrors.Should().Be(0);
    }

    [Fact]
    public void The_synchronous_read_warns_once_about_each_skipped_prefix()
    {
        var database = _multiplexer.GetDatabase().WithKeyPrefix("[app]:");
        var prefix = NewPrefix();

        prefix.Get(database, "app*:", _logger).Should().BeEmpty();
        prefix.Get(database, "app*:", _logger).Should().BeEmpty();

        Logged(LogLevel.Warning).Should().Be(2, "the connector's prefix and the configured one are each skipped, and each is reported once");
        LoggedErrors.Should().Be(0);
    }

    [Fact]
    public void The_synchronous_read_logs_a_disagreeing_configured_prefix_once()
    {
        var database = _multiplexer.GetDatabase().WithKeyPrefix("{app}:");
        var prefix = NewPrefix();

        prefix.Get(database, "stale:", _logger).Should().Be("{app}:");
        prefix.Get(database, "stale:", _logger);

        LoggedErrors.Should().Be(1);
    }

    [Fact]
    public async Task A_skipped_prefix_is_not_probed_again()
    {
        var database = Echoing("[app]:");
        var prefix = NewPrefix();

        prefix.Get(database, "configured:", _logger).Should().Be("configured:");
        prefix.Get(database, "configured:", _logger).Should().Be("configured:");

        await database.Received(1).ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_skipped_prefix_is_warned_about_once()
    {
        var database = _multiplexer.GetDatabase().WithKeyPrefix("[app]:");
        var prefix = NewPrefix();

        await prefix.GetAsync(database, "configured:", _logger, TestContext.Current.CancellationToken);
        await prefix.GetAsync(database, "configured:", _logger, TestContext.Current.CancellationToken);

        Logged(LogLevel.Warning).Should().Be(1);
        LoggedErrors.Should().Be(0, "a skipped prefix is not a mismatch");
    }

    [Fact]
    public void Each_connector_has_one_prefix_shared_by_every_consumer()
    {
        var connector = Substitute.For<IRedisConnector>();

        ConnectorKeyPrefix.For(connector).Should().BeSameAs(ConnectorKeyPrefix.For(connector));
        ConnectorKeyPrefix.For(connector).Should().NotBeSameAs(ConnectorKeyPrefix.For(Substitute.For<IRedisConnector>()));
    }

    [Fact]
    public async Task A_decorated_database_resolves_through_one_probe_kept_for_the_connector()
    {
        var database = Echoing("{app}:");
        var prefix = NewPrefix();

        (await prefix.GetAsync(database, string.Empty, _logger, TestContext.Current.CancellationToken)).Should().Be("{app}:");
        prefix.Get(database, configured: string.Empty, _logger).Should().Be("{app}:");
        await database.Received(1).ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task The_asynchronous_read_waits_for_a_running_probe()
    {
        var echo = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Substitute.For<IDatabase>();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(echo.Task);

        var reading = NewPrefix().GetAsync(database, string.Empty, _logger, TestContext.Current.CancellationToken).AsTask();
        echo.SetResult(Echo("{app}:"));

        (await reading).Should().Be("{app}:");
    }

    [Fact]
    public async Task The_synchronous_read_does_not_wait_for_a_running_probe_and_uses_its_answer_once_it_lands()
    {
        var echo = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Substitute.For<IDatabase>();
        var prefix = NewPrefix();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(echo.Task);

        prefix.Get(database, "configured:", _logger).Should().Be("configured:");

        echo.SetResult(Echo("{app}:"));
        (await prefix.GetAsync(database, "configured:", _logger, TestContext.Current.CancellationToken)).Should().Be("{app}:");

        prefix.Get(database, "configured:", _logger).Should().Be("{app}:");
        await database.Received(1).ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>());
    }

    [Theory]
    [InlineData("{app}:", LogLevel.Error)]
    [InlineData("app*:", LogLevel.Warning)]
    public async Task The_synchronous_read_reports_the_answer_of_a_probe_it_did_not_wait_for(string connectorPrefix, LogLevel level)
    {
        var echo = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Substitute.For<IDatabase>();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(echo.Task);

        NewPrefix().Get(database, "configured:", _logger).Should().Be("configured:");
        Logged(level).Should().Be(0);

        echo.SetResult(Echo(connectorPrefix));

        await WaitUntilAsync(() => Logged(level) == 1);
    }

    [Fact]
    public void A_failed_probe_falls_back_to_the_configured_prefix()
    {
        var database = Substitute.For<IDatabase>();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).ThrowsAsync(new RedisException("down"));

        NewPrefix().Get(database, "configured:", _logger).Should().Be("configured:");
    }

    [Fact]
    public void An_unexpected_echo_falls_back_to_the_configured_prefix()
    {
        var database = Substitute.For<IDatabase>();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(RedisResult.Create((RedisValue)"something else"));

        NewPrefix().Get(database, "configured:", _logger).Should().Be("configured:");
    }

    [Fact]
    public void An_echo_that_is_not_UTF8_falls_back_to_the_configured_prefix()
    {
        var database = Substitute.For<IDatabase>();
        byte[] echoed = [0xFF, .. System.Text.Encoding.UTF8.GetBytes(ConnectorKeyPrefix.ProbeMarker)];
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(RedisResult.Create((RedisValue)echoed));

        NewPrefix().Get(database, "configured:", _logger).Should().Be("configured:");
    }

    [Fact]
    public void A_failed_probe_is_retried_by_a_later_call()
    {
        var database = Substitute.For<IDatabase>();
        var prefix = NewPrefix();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromException<RedisResult>(new RedisException("timeout")), _ => Task.FromResult(Echo("{app}:")));

        prefix.Get(database, configured: string.Empty, _logger).Should().BeEmpty();
        prefix.Get(database, configured: string.Empty, _logger).Should().Be("{app}:");
    }

    [Fact]
    public async Task Probing_stops_once_every_attempt_has_failed()
    {
        var database = Substitute.For<IDatabase>();
        var prefix = NewPrefix();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).ThrowsAsync(new RedisException("down"));

        for (var call = 0; call < 5; call++)
        {
            prefix.Get(database, "configured:", _logger).Should().Be("configured:");
        }

        await database.Received(3).ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_configured_prefix_that_disagrees_with_the_connector_is_logged_once()
    {
        var database = _multiplexer.GetDatabase().WithKeyPrefix("{app}:");
        var prefix = NewPrefix();

        (await prefix.GetAsync(database, "stale:", _logger, TestContext.Current.CancellationToken)).Should().Be("{app}:");
        await prefix.GetAsync(database, "stale:", _logger, TestContext.Current.CancellationToken);

        LoggedErrors.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{app}:")]
    public async Task A_configured_prefix_that_is_empty_or_agrees_is_not_logged(string configured)
    {
        await NewPrefix().GetAsync(_multiplexer.GetDatabase().WithKeyPrefix("{app}:"), configured, _logger, TestContext.Current.CancellationToken);

        LoggedErrors.Should().Be(0);
    }

    [Fact]
    public async Task A_fallback_to_the_configured_prefix_is_not_logged_as_a_mismatch()
    {
        var database = Substitute.For<IDatabase>();
        database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).ThrowsAsync(new RedisException("down"));

        (await NewPrefix().GetAsync(database, "configured:", _logger, TestContext.Current.CancellationToken)).Should().Be("configured:");

        LoggedErrors.Should().Be(0);
    }

    public void Dispose() => _multiplexer.Dispose();

    private static ConnectorKeyPrefix NewPrefix() => ConnectorKeyPrefix.For(Substitute.For<IRedisConnector>());

    private static RedisResult Echo(string prefix) => RedisResult.Create((RedisValue)(prefix + ConnectorKeyPrefix.ProbeMarker));

    private static IDatabase Echoing(string prefix)
    {
        var database = Substitute.For<IDatabase>();
        database.ExecuteAsync("ECHO", Arg.Is<ICollection<object>>(args => args.Single().Equals((RedisKey)ConnectorKeyPrefix.ProbeMarker)), Arg.Any<CommandFlags>())
            .Returns(Echo(prefix));
        return database;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue();
    }

    private int Logged(LogLevel level) =>
        _logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && (LogLevel)call.GetArguments()[0]! == level);
}
