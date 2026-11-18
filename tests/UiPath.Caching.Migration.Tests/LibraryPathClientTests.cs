using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class LibraryPathClientTests : RouterTestBase
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(7);

    private readonly FakeLibraryStore _store = new();
    private readonly FakeConnection _connection = new();

    [Fact]
    public async Task A_read_that_fails_is_unavailable_and_never_absent()
    {
        _store.Read = (_, _) => throw new TimeoutException("redis timeout");

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unavailable);
    }

    [Fact]
    public async Task A_read_while_disconnected_is_unavailable_without_asking_the_library()
    {
        _connection.IsConnected = false;

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unavailable);
        _store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_entry_is_absent_and_the_key_is_the_mapped_text_verbatim()
    {
        var read = await Client(key => $" N:{key} ").ReadAsync("K", Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        _store.Keys.Single().Name.Should().Be(" N:K ");
    }

    [Fact]
    public async Task An_entry_the_envelope_could_not_read_is_unreadable_and_not_absent()
    {
        _store.Read = async (_, _) =>
        {
            await Task.Yield();
            ReadProbe.Current?.MarkUnreadable();
            return null;
        };

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unreadable);
    }

    [Fact]
    public async Task A_failure_after_the_probe_saw_unreadable_bytes_is_unreadable()
    {
        _store.Read = (_, _) =>
        {
            ReadProbe.Current?.MarkUnreadable();
            throw new InvalidOperationException("deserialize failed");
        };

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unreadable);
    }

    [Fact]
    public async Task A_found_entry_carries_the_ttl_of_the_same_snapshot()
    {
        _store.Read = (_, _) => Task.FromResult<object?>(new Item("v"));

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Value.Should().Be(new Item("v"));
        read.Expiration.Should().NotBeNull();
    }

    [Fact]
    public async Task Concurrent_reads_never_see_each_others_probe()
    {
        var client = Client();
        _store.Read = async (key, token) =>
        {
            await Task.Delay(Random.Shared.Next(1, 10), token);
            if (key.Name.StartsWith("bad", StringComparison.Ordinal))
            {
                ReadProbe.Current?.MarkUnreadable();
            }

            return null;
        };

        var reads = await Task.WhenAll(Enumerable.Range(0, 200).Select(async index =>
        {
            var key = index % 2 == 0 ? $"bad{index}" : $"good{index}";
            return (index, (await client.ReadAsync(key, Ct)).Kind);
        }));

        reads.Where(read => read.index % 2 == 0).Should().OnlyContain(read => read.Kind == ReadOutcome.Unreadable);
        reads.Where(read => read.index % 2 == 1).Should().OnlyContain(read => read.Kind == ReadOutcome.Absent);
        ReadProbe.Current.Should().BeNull();
    }

    [Fact]
    public async Task A_write_that_fails_after_sending_is_unknown_and_carries_the_settle_deadline()
    {
        _store.Write = (_, _) => Task.FromResult(false);

        var result = await Client().SetAsync("k", new Item("v"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        result.SettleWithin.Should().Be(Settle);
    }

    [Fact]
    public async Task A_write_while_disconnected_is_not_sent()
    {
        _connection.IsConnected = false;

        (await Client().SetAsync("k", new Item("v"), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await Client().RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        _store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_cancelled_before_the_send_is_not_sent()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Client().SetAsync("k", new Item("v"), null, cts.Token);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        _store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_map_that_throws_is_not_sent()
    {
        var client = Client(_ => throw new InvalidOperationException("no such tenant"));

        (await client.SetAsync("k", new Item("v"), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
    }

    [Fact]
    public async Task A_serializer_failure_before_sending_is_not_sent()
    {
        _store.Write = (_, _) =>
        {
            ReadProbe.Current?.MarkSerializeFailed();
            throw new InvalidOperationException("cannot serialize");
        };

        var result = await Client().SetAsync("k", new Item("v"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
    }

    [Fact]
    public void The_probing_serializer_marks_a_failed_serialize_and_unreadable_bytes()
    {
        var probe = ReadProbe.Begin();
        try
        {
            var proxy = new ProbingSerializerProxy(new ScriptedSerializer());

            var serialize = () => proxy.Serialize("boom");
            serialize.Should().Throw<InvalidOperationException>();
            proxy.TryDeserialize<string>("text", out _).Should().BeFalse();

            probe.SerializeFailed.Should().BeTrue();
            probe.Unreadable.Should().BeTrue("a non-empty value that no serializer read");
        }
        finally
        {
            ReadProbe.End();
        }
    }

    private LibraryPathClient<Item> Client(Func<string, string>? redisKey = null) =>
        new(_store, _connection, redisKey ?? (key => key), Settle);

    private sealed class ScriptedSerializer : ISerializerProxy<byte[]>
    {
        public byte[]? Serialize(object? value) => throw new InvalidOperationException("cannot serialize");

        public T? Deserialize<T>(byte[]? value) => default;

        public bool TryDeserialize<T>(string? value, out T? result)
        {
            result = default;
            return false;
        }

        public bool TryDeserialize<T>(object? value, out T? result)
        {
            result = default;
            return false;
        }
    }
}
