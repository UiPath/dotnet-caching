using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

// What the plain library path of a hash reports for a write, a delete and a refresh: never sent, answered, or sent without an answer.
[Collection(WriterPoolCollection.Name)]
public class RedisHashCacheMigrationOutcomeTests(ITestContextAccessor testContextAccessor)
{
    private static readonly HashCacheEntryOptions Replace = new(TimeToLive: TimeSpan.FromMinutes(5));
    private static readonly Dictionary<string, string?> Fields = new() { ["a"] = "x" };

    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    private CancellationToken Token => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task A_write_the_server_commits_is_committed_and_one_it_refuses_to_run_is_failed()
    {
        var sut = Create();
        Stub();

        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Committed);

        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);
        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Failed);
    }

    [Fact]
    public async Task A_write_and_a_refresh_whose_transaction_commits_while_a_command_in_it_fails_are_failed()
    {
        var sut = Create();
        Stub();
        var wrongType = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "WRONGTYPE Operation against a key holding the wrong kind of value");
        _transaction.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>()).Returns(Task.FromException(wrongType));
        _transaction.HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(wrongType));
        var merge = Replace with { SetOption = HashCacheSetOption.HashReplace };

        (await sut.WriteAsync("k", Fields, merge, null, Token)).Should().Be(StoreOutcome.Failed, "EXEC answered true, and the HSET in it did not run");
        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Failed);
        (await sut.RenewAsync<string>("k", Replace with { Metadata = new Dictionary<string, string?> { ["m"] = "v" } }, null, Token)).Should().Be(StoreOutcome.Failed, "the metadata write failed");
        (await sut.RenewAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Failed, "so did the metadata delete");
    }

    [Fact]
    public async Task A_write_and_a_refresh_whose_expiry_or_persist_command_fails_are_failed_while_the_librarys_own_fires_and_forgets_them()
    {
        var sut = Create();
        Stub();
        var denied = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'expire' command");
        var flags = new List<CommandFlags>();
        _transaction.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Do<CommandFlags>(flags.Add)).Returns(true);

        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Committed);
        (await sut.SetAsync("k", Fields, Replace, null, Token)).Should().BeTrue();
        flags.Should().HaveCount(2);
        flags[0].HasFlag(CommandFlags.FireAndForget).Should().BeFalse("a traced write is answered by its expiry command");
        flags[1].HasFlag(CommandFlags.FireAndForget).Should().BeTrue("the library's own write is unchanged");

        _transaction.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(denied));
        _transaction.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(denied));
        var persistent = new HashCacheEntryOptions(ExpireTime: DateTimeOffset.MaxValue, SetOption: HashCacheSetOption.HashReplace);

        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Failed, "the expiry was refused");
        (await sut.WriteAsync("k", Fields, persistent, null, Token)).Should().Be(StoreOutcome.Failed, "the persist was refused");
        (await sut.RenewAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Failed);
        (await sut.RenewAsync<string>("k", new HashCacheEntryOptions(ExpireTime: DateTimeOffset.MaxValue), null, Token)).Should().Be(StoreOutcome.Failed);
    }

    [Fact]
    public async Task A_write_that_times_out_after_the_send_is_unknown_and_one_cancelled_in_flight_too()
    {
        var sut = Create(new GivingUp(TimeSpan.FromMilliseconds(100)));
        Stub();
        var never = new TaskCompletionSource<bool>();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(never.Task);

        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Unknown);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        (await Create(new GivingUp(TimeSpan.FromSeconds(30))).WriteAsync("k", Fields, Replace, null, cts.Token)).Should().Be(StoreOutcome.Unknown);
    }

    [Fact]
    public async Task A_write_that_never_left_is_not_sent_whatever_stopped_it()
    {
        var sut = Create();
        Stub();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        (await sut.WriteAsync("k", Fields, Replace, null, cancelled.Token)).Should().Be(StoreOutcome.NotSent, "cancelled before the send");
        (await sut.WriteAsync("k", new Dictionary<string, string?> { ["_odd_"] = "x" }, Replace, null, Token)).Should().Be(StoreOutcome.NotSent, "a reserved field name fails before the send");
        (await Create(proxy: new ThrowingProxy()).WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.NotSent, "a serializer that throws fails before the send");
        (await Create(connected: false).WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.NotSent, "not connected");
        await _transaction.DidNotReceiveWithAnyArgs().ExecuteAsync(default);
    }

    [Fact]
    public async Task An_empty_write_that_stores_no_key_is_a_delete_and_is_reported_as_one()
    {
        var sut = Create();
        Stub();

        (await sut.WriteAsync("k", new Dictionary<string, string?>(), Replace, null, Token)).Should().Be(StoreOutcome.Committed);

        await _database.Received(1).KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_delete_is_committed_when_answered_unknown_when_not_and_not_sent_when_refused()
    {
        var sut = Create();
        Stub();
        (await sut.DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.Committed);

        var never = new TaskCompletionSource<bool>();
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(never.Task);
        (await Create(new GivingUp(TimeSpan.FromMilliseconds(100))).DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.Unknown);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        (await sut.DeleteAsync<string>("k", cancelled.Token)).Should().Be(StoreOutcome.NotSent);
        (await Create(connected: false).DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.NotSent);
    }

    [Fact]
    public async Task A_field_delete_is_committed_unknown_or_not_sent_and_an_empty_one_sends_nothing()
    {
        var sut = Create();
        Stub();
        _database.HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()).Returns(1L);
        (await sut.DeleteFieldsAsync<string>("k", ["a"], Token)).Should().Be(StoreOutcome.Committed);
        (await sut.DeleteFieldsAsync<string>("k", [], Token)).Should().Be(StoreOutcome.Committed);
        await _database.Received(1).HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());

        var never = new TaskCompletionSource<long>();
        _database.HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()).Returns(never.Task);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        (await sut.DeleteFieldsAsync<string>("k", ["a"], cts.Token)).Should().Be(StoreOutcome.Unknown);
        (await sut.DeleteFieldsAsync<string>("k", ["_odd_"], Token)).Should().Be(StoreOutcome.NotSent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refresh_is_committed_when_its_transaction_runs_with_the_hash_there_and_failed_when_the_hash_is_not(bool withMetadata)
    {
        var sut = Create();
        Stub();
        var options = withMetadata ? Replace with { Metadata = new Dictionary<string, string?> { ["m"] = "v" } } : Replace;

        (await sut.RenewAsync<string>("k", options, null, Token)).Should().Be(StoreOutcome.Committed);

        // The transaction requires the key, so a missing one fails its condition and EXEC answers false.
        _transaction.Received().AddCondition(Arg.Any<Condition>());
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);
        (await sut.RenewAsync<string>("k", options, null, Token)).Should().Be(StoreOutcome.Failed);
    }

    [Fact]
    public async Task A_refresh_that_times_out_is_unknown_and_one_that_never_left_is_not_sent()
    {
        Stub();
        var never = new TaskCompletionSource<bool>();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(never.Task);

        (await Create(new GivingUp(TimeSpan.FromMilliseconds(100))).RenewAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Unknown);
        (await Create(connected: false).RenewAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.NotSent);
    }

    private void Stub()
    {
        _transaction.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        _transaction.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
    }

    private RedisHashCache Create(IResiliencePipeline? write = null, ISerializerProxy<byte[]>? proxy = null, bool connected = true, bool awaitRefresh = true)
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.Database.Returns(_database);
        connector.Version.Returns(new Version(6, 0));
        connector.IsConnected.Returns(connected);
        _database.Multiplexer.GetHashSlot(Arg.Any<RedisKey>()).Returns(0);
        _database.CreateTransaction(Arg.Any<object?>()).Returns(_transaction);
        var pipelines = Substitute.For<IResiliencePipelineProvider>();
        pipelines.Get(ResiliencePipelineNames.Read).Returns(EmptyResiliencePipeline.Instance);
        pipelines.Get(ResiliencePipelineNames.Write).Returns(write ?? EmptyResiliencePipeline.Instance);
        var strategy = Substitute.For<IRedisKeyStrategy>();
        strategy.GetRedisKey(Arg.Any<CacheKey>()).Returns(call => (RedisKey)$"test:hash:{call.Arg<CacheKey>()}");
        var strategies = Substitute.For<IRedisKeyStrategyFactory>();
        strategies.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(strategy);
        var options = new RedisCacheOptions { RedisKeyStrategyFactory = strategies, ReadScriptsEnabled = false, AwaitRefresh = awaitRefresh };
        return new RedisHashCache(
            connector,
            proxy ?? new SystemJsonByteSerializerProxy(),
            pipelines,
            new RecordingTelemetryProvider(),
            options,
            new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true },
            new DefaultCachePolicyFactory(null, null),
            TimeProvider.System,
            NullLogger<RedisHashCache>.Instance);
    }

    private sealed class GivingUp(TimeSpan timeout) : IResiliencePipeline
    {
        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                return await callback(cts.Token).AsTask().WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return defaultValue;
            }
        }
    }

    private sealed class ThrowingProxy : ISerializerProxy<byte[]>
    {
        public byte[]? Serialize(object? value) => throw new NotSupportedException();

        public T? Deserialize<T>(byte[]? value) => throw new NotSupportedException();

        public bool TryDeserialize<T>(string? value, out T? result) => throw new NotSupportedException();

        public bool TryDeserialize<T>(object? value, out T? result) => throw new NotSupportedException();
    }
}
