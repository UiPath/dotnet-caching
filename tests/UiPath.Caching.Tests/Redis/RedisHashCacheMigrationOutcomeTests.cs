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

    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    private CancellationToken Token => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task A_write_the_server_commits_is_committed_and_one_it_refuses_to_run_is_failed_with_nothing_applied()
    {
        var sut = Create();
        Stub();

        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Committed);

        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);
        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Failed);
    }

    [Fact]
    public async Task A_write_and_a_refresh_whose_transaction_runs_while_a_command_in_it_fails_are_unknown_because_the_other_commands_were_applied()
    {
        var sut = Create();
        Stub();
        var wrongType = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "WRONGTYPE Operation against a key holding the wrong kind of value");
        _transaction.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>()).Returns(Task.FromException(wrongType));
        _transaction.HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(wrongType));
        var merge = Replace with { SetOption = HashCacheSetOption.HashReplace };

        (await sut.WriteAsync("k", Fields, merge, null, Token)).Should().Be(StoreOutcome.Unknown, "EXEC answered true, the HSET in it did not run and the expiry in it did");
        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Unknown);
        (await sut.RenewAsync<string>("k", Replace with { Metadata = new Dictionary<string, string?> { ["m"] = "v" } }, null, Token)).Should().Be(StoreOutcome.Unknown, "the metadata write failed and the expiry was applied");
        (await sut.RenewAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Unknown, "so did the metadata delete");
    }

    [Fact]
    public async Task A_write_and_a_refresh_whose_expiry_or_persist_command_fails_are_unknown_while_the_librarys_own_fires_and_forgets_them()
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

        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Unknown, "the expiry was refused after the fields were written");
        (await sut.WriteAsync("k", Fields, persistent, null, Token)).Should().Be(StoreOutcome.Unknown, "the persist was refused after the fields were written");
        (await sut.RenewAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Unknown);
        (await sut.RenewAsync<string>("k", new HashCacheEntryOptions(ExpireTime: DateTimeOffset.MaxValue), null, Token)).Should().Be(StoreOutcome.Unknown);
    }

    [Fact]
    public async Task A_write_that_times_out_after_the_send_is_unknown_and_one_cancelled_in_flight_too()
    {
        var sut = Create(new GivingUp(TimeSpan.FromMilliseconds(100), _time));
        Stub();
        var never = new TaskCompletionSource<bool>();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(never.Task);

        (await sut.WriteAsync("k", Fields, Replace, null, Token)).Should().Be(StoreOutcome.Unknown);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var write = Create(new GivingUp(TimeSpan.FromSeconds(30), _time)).WriteAsync("k", Fields, Replace, null, cts.Token);
        await cts.CancelAsync();
        (await write).Should().Be(StoreOutcome.Unknown);
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
    public async Task An_expired_merge_does_not_send_a_delete()
    {
        var sut = Create();
        Stub();
        var expired = new HashCacheEntryOptions(ExpireTime: _time.GetUtcNow().AddMinutes(1), SetOption: HashCacheSetOption.HashReplace);
        _time.Advance(TimeSpan.FromMinutes(2));

        (await sut.WriteAsync("key", Fields, expired, null, Token)).Should().Be(StoreOutcome.NotSent);
        await _database.DidNotReceiveWithAnyArgs().KeyDeleteAsync(default(RedisKey), default);
    }

    [Fact]
    public async Task A_merge_that_expires_during_the_type_check_never_starts_a_transaction()
    {
        var sut = Create();
        Stub();
        _database.KeyTypeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            return RedisType.Hash;
        });

        var merge = Replace with { SetOption = HashCacheSetOption.HashReplace };
        (await sut.WriteAsync("key", Fields, merge, null, Token)).Should().Be(StoreOutcome.NotSent);
        await _transaction.DidNotReceiveWithAnyArgs().ExecuteAsync(default);
    }

    [Fact]
    public async Task A_chunked_merge_that_expires_before_its_first_chunk_sends_no_fields()
    {
        var sut = Create(fieldBatch: 1, version: new Version(7, 0));
        Stub();
        sut.OnChunk = _ =>
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            return Task.CompletedTask;
        };

        var fields = new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" };
        var merge = Replace with { SetOption = HashCacheSetOption.HashReplace };
        (await sut.WriteAsync("key", fields, merge, null, Token)).Should().Be(StoreOutcome.Failed);
        await _database.DidNotReceiveWithAnyArgs().ScriptEvaluateAsync(default(string)!, default, default, default);
    }

    [Fact]
    public async Task A_delete_is_committed_when_answered_unknown_when_not_and_not_sent_when_refused()
    {
        var sut = Create();
        Stub();
        (await sut.DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.Committed);

        var never = new TaskCompletionSource<bool>();
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(never.Task);
        (await Create(new GivingUp(TimeSpan.FromMilliseconds(100), _time)).DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.Unknown);

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
        var delete = sut.DeleteFieldsAsync<string>("k", ["a"], cts.Token);
        await cts.CancelAsync();
        (await delete).Should().Be(StoreOutcome.Unknown);
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
        (await sut.RenewExpiryAsync<string>("k", options, null, Token)).Should().Be(StoreOutcome.Failed);
    }

    [Fact]
    public async Task A_refresh_that_times_out_is_unknown_and_one_that_never_left_is_not_sent()
    {
        Stub();
        var never = new TaskCompletionSource<bool>();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(never.Task);

        (await Create(new GivingUp(TimeSpan.FromMilliseconds(100), _time)).RenewAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Unknown);
        (await Create(connected: false).RenewAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.NotSent);
    }

    [Fact]
    public async Task An_expiry_only_refresh_is_committed_when_its_transaction_runs_failed_when_the_hash_is_not_there_or_a_command_in_it_fails_and_never_writes_metadata()
    {
        var sut = Create();
        Stub();

        (await sut.RenewExpiryAsync<string>("k", Replace with { Metadata = new Dictionary<string, string?> { ["ignored"] = "v" } }, null, Token)).Should().Be(StoreOutcome.Committed);
        await _transaction.DidNotReceive().HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>());
        await _transaction.DidNotReceive().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        _transaction.Received().AddCondition(Arg.Any<Condition>());

        var denied = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'expire' command");
        _transaction.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(denied));
        (await sut.RenewExpiryAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Unknown, "EXEC ran and the expiry in it did not, which leaves the metadata and the fields as they were but is no answer that nothing was applied");

        _transaction.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);
        (await sut.RenewExpiryAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Failed, "the condition that the hash is there failed");
    }

    [Fact]
    public async Task An_expiry_only_refresh_to_no_expiry_persists_and_one_that_times_out_is_unknown_and_one_that_never_left_is_not_sent()
    {
        var sut = Create();
        Stub();
        _transaction.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);

        (await sut.RenewExpiryAsync<string>("k", new HashCacheEntryOptions(ExpireTime: DateTimeOffset.MaxValue), null, Token)).Should().Be(StoreOutcome.Committed);
        await _transaction.Received().KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());

        var never = new TaskCompletionSource<bool>();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(never.Task);
        (await Create(new GivingUp(TimeSpan.FromMilliseconds(100), _time)).RenewExpiryAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.Unknown);
        (await Create(connected: false).RenewExpiryAsync<string>("k", Replace, null, Token)).Should().Be(StoreOutcome.NotSent);
    }

    // The commands that are no transaction have no condition to refuse them: a delete or a field delete that faults may have applied, so it is unknown and never failed.
    [Fact]
    public async Task A_delete_and_a_field_delete_that_fault_are_unknown_and_never_failed()
    {
        var sut = Create();
        Stub();
        var denied = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'del' command");
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(denied));
        _database.HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<long>(denied));

        (await sut.DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.Unknown);
        (await sut.DeleteFieldsAsync<string>("k", ["a"], Token)).Should().Be(StoreOutcome.Unknown);
    }

    // The rename of a chunked replacement may have applied although its answer was lost: when the deadline has passed by the time the cleanup runs, the replacement has expired and the cleanup finds nothing, which proves nothing.
    [Fact]
    public async Task A_chunked_replacement_whose_rename_may_have_applied_stays_unknown_when_its_cleanup_finds_nothing_to_delete()
    {
        var sut = Create(fieldBatch: 1);
        Stub();
        _database.KeyRenameAsync(Arg.Any<RedisKey>(), Arg.Any<RedisKey>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(_ => LostRenameAsync());
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        var deadline = _time.GetUtcNow().AddMilliseconds(250);

        var outcome = await sut.WriteAsync("k", new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" }, new HashCacheEntryOptions(ExpireTime: deadline), null, Token);

        outcome.Should().Be(StoreOutcome.Unknown);
        await _database.Received().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    // A cleanup that did remove the key is an answer: the replacement expired and is gone, as an expired write leaves the key.
    [Fact]
    public async Task A_chunked_replacement_whose_cleanup_removed_the_key_is_committed()
    {
        var sut = Create(fieldBatch: 1);
        Stub();
        _database.KeyRenameAsync(Arg.Any<RedisKey>(), Arg.Any<RedisKey>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(_ => LostRenameAsync());
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);

        var outcome = await sut.WriteAsync("k", new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" }, new HashCacheEntryOptions(ExpireTime: _time.GetUtcNow().AddMilliseconds(250)), null, Token);

        outcome.Should().Be(StoreOutcome.Committed);
    }

    // The type is held by a condition of the transaction: a key that changed type between the read and the commit aborts it with nothing applied, and the merge reads the type again before it gives up.
    [Fact]
    public async Task A_merge_whose_type_condition_aborts_the_transaction_reads_the_type_again_and_is_failed_only_when_it_keeps_aborting()
    {
        var sut = Create();
        Stub();
        var merge = Replace with { SetOption = HashCacheSetOption.HashReplace };
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false, false, true);

        (await sut.WriteAsync("k", Fields, merge, null, Token)).Should().Be(StoreOutcome.Committed);
        await _database.Received(3).KeyTypeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        _transaction.Received(3).AddCondition(Arg.Any<Condition>());

        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);
        (await sut.WriteAsync("k", Fields, merge, null, Token)).Should().Be(StoreOutcome.Failed);
    }

    [Fact]
    public async Task A_merge_into_a_key_that_holds_a_string_queues_nothing_and_is_failed()
    {
        var sut = Create();
        Stub();
        _database.KeyTypeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisType.String);

        (await sut.WriteAsync("k", Fields, Replace with { SetOption = HashCacheSetOption.HashReplace }, null, Token)).Should().Be(StoreOutcome.Failed);

        await _transaction.DidNotReceiveWithAnyArgs().ExecuteAsync(default);
        _ = _transaction.DidNotReceiveWithAnyArgs().KeyExpireAsync(default, default(DateTime?), default);
    }

    // A script that fails from inside has run commands: the write may have applied, so it is not run again another way, whatever the fallback would then find.
    [Fact]
    public async Task A_chunk_script_that_fails_from_inside_after_its_write_is_unknown_and_never_falls_back_to_a_type_refusal()
    {
        var sut = Create(fieldBatch: 1, version: new Version(7, 0));
        Stub();
        var inside = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ERR Error running script (call to f_1a2b): @user_script:4: @user_script: 4: -NOPERM this user has no permissions to run the 'pexpireat' command");
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<RedisResult>(inside));
        _database.KeyTypeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisType.String);

        var outcome = await sut.WriteAsync("k", new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" }, Replace with { SetOption = HashCacheSetOption.HashReplace }, null, Token);

        outcome.Should().Be(StoreOutcome.Unknown);
        await _database.DidNotReceive().KeyTypeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    // A refusal of the EVAL itself ran nothing, so the same chunk goes the other way, and a string found there is a refusal with nothing applied.
    [Fact]
    public async Task A_chunk_script_refused_at_the_eval_falls_back_and_a_string_found_there_is_failed_with_nothing_applied()
    {
        var sut = Create(fieldBatch: 1, version: new Version(7, 0));
        Stub();
        var refused = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'eval' command");
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<RedisResult>(refused));
        _database.KeyTypeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisType.String);

        var outcome = await sut.WriteAsync("k", new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" }, Replace with { SetOption = HashCacheSetOption.HashReplace }, null, Token);

        outcome.Should().Be(StoreOutcome.Failed);
        await _database.Received(1).KeyTypeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    private Task<bool> LostRenameAsync()
    {
        _time.Advance(TimeSpan.FromMilliseconds(400));
        return Task.FromException<bool>(new TimeoutException("the answer of the rename was lost"));
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

    private RedisHashCache Create(IResiliencePipeline? write = null, ISerializerProxy<byte[]>? proxy = null, bool connected = true, bool awaitRefresh = true, int fieldBatch = 0, Version? version = null)
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.Database.Returns(_database);
        connector.Version.Returns(version ?? new Version(6, 0));
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
        var options = new RedisCacheOptions { RedisKeyStrategyFactory = strategies, ReadScriptsEnabled = false, AwaitRefresh = awaitRefresh, HashFieldBatchSize = fieldBatch > 0 ? fieldBatch : new RedisCacheOptions().HashFieldBatchSize };
        return new RedisHashCache(
            connector,
            proxy ?? new SystemJsonByteSerializerProxy(),
            pipelines,
            new RecordingTelemetryProvider(),
            options,
            new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true },
            new DefaultCachePolicyFactory(null, null),
            _time,
            NullLogger<RedisHashCache>.Instance);
    }

    private sealed class GivingUp(TimeSpan timeout, ManualTimeProvider clock) : IResiliencePipeline
    {
        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timer = clock.CreateTimer(_ => cts.Cancel(), null, timeout, Timeout.InfiniteTimeSpan);
            try
            {
                var running = callback(cts.Token).AsTask();
                clock.Advance(timeout);
                return await running.WaitAsync(cts.Token);
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
