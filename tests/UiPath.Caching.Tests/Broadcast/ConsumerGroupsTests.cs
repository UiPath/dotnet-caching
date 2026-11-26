using StackExchange.Redis;

namespace UiPath.Caching.Tests.Broadcast;

public class ConsumerGroupsTests
{
    private static readonly ManualTimeProvider Clock = new(DateTimeOffset.UnixEpoch);
    private static readonly long Joined = Clock.GetTimestamp();
    private static readonly long RepliedAt = Joined + Clock.TimestampFrequency;

    [Fact]
    public void An_unknown_head_counts_as_added() =>
        ConsumerGroups.AddedSince(null, Joined, Clock).Should().BeTrue();

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 0L)]
    public void A_missing_or_never_written_stream_had_nothing_added(bool exists, long added) =>
        ConsumerGroups.AddedSince(Head(exists, lastMs: 0, added), Joined, Clock).Should().BeFalse();

    [Fact]
    public void A_head_from_before_the_join_had_nothing_added() =>
        ConsumerGroups.AddedSince(Head(lastMs: 100_000 - 1_000 - 50), Joined, Clock).Should().BeFalse();

    [Theory]
    [InlineData(100_000 - 1_000)]        // the join instant itself
    [InlineData(100_000 - 1_000 - 2)]    // within the truncation margin
    [InlineData(100_000)]
    public void A_head_from_the_join_on_was_added(long lastMs) =>
        ConsumerGroups.AddedSince(Head(lastMs: lastMs), Joined, Clock).Should().BeTrue();

    [Fact]
    public void A_head_with_no_id_counts_as_added() =>
        ConsumerGroups.AddedSince(Head(lastMs: null), Joined, Clock).Should().BeTrue();

    [Fact]
    public void Below_Redis_7_with_no_entry_count_the_id_decides() =>
        ConsumerGroups.AddedSince(Head(lastMs: 10, added: null), Joined, Clock).Should().BeFalse();

    [Fact]
    public async Task Below_Redis_7_the_group_is_made_at_the_id_the_head_read()
    {
        // No max-deleted-entry-id, so no count: made at that id, not $, so what was added since the read is delivered.
        var db = Substitute.For<IDatabase>();
        db.StreamCreateConsumerGroupAsync(default, default, default).ReturnsForAnyArgs(true);
        var head = new StreamHead(100_000, RepliedAt, Exists: true, "99000-3", 99_000, EntriesAdded: null, Start: null);

        (await ConsumerGroups.CreateAsync(db, "s", "g", StreamPosition.NewMessages, head)).Should().BeTrue();

        await db.Received(1).StreamCreateConsumerGroupAsync("s", "g", (RedisValue)"99000-3");
    }

    [Fact]
    public async Task A_missing_stream_the_server_cannot_count_from_is_made_from_its_start()
    {
        // Below Redis 7.0 ENTRIESREAD is refused; the stream made now holds only what is added after the read.
        var db = Substitute.For<IDatabase>();
        db.StreamCreateConsumerGroupAsync(default, default, default).ReturnsForAnyArgs(true);
        db.ExecuteAsync("XGROUP", Arg.Any<object[]>())
            .Returns(Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ERR syntax error")));
        var head = new StreamHead(100_000, RepliedAt, Exists: false, RedisValue.Null, null, EntriesAdded: 0, Start: new ReadCount("0-0", 0));

        (await ConsumerGroups.CreateAsync(db, "s", "g", StreamPosition.NewMessages, head)).Should().BeTrue();

        await db.Received(1).StreamCreateConsumerGroupAsync("s", "g", (RedisValue)"0");
    }

    [Fact]
    public async Task With_the_head_unknown_the_group_is_made_at_the_newest_entry()
    {
        var db = Substitute.For<IDatabase>();
        db.StreamCreateConsumerGroupAsync(default, default, default).ReturnsForAnyArgs(true);
        db.StreamInfoAsync("s").Returns(Task.FromException<StreamInfo>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM")));

        (await ConsumerGroups.CreateAsync(db, "s", "g", StreamPosition.NewMessages, head: null)).Should().BeTrue();

        await db.Received(1).StreamCreateConsumerGroupAsync("s", "g", StreamPosition.NewMessages);
    }

    [Fact]
    public async Task A_group_that_exists_is_found_whatever_text_follows_the_code()
    {
        var db = Substitute.For<IDatabase>();
        db.ExecuteAsync("XGROUP", Arg.Any<object[]>())
            .Returns(Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, TestErrors.ValkeyGroupExists)));
        var head = new StreamHead(100_000, RepliedAt, Exists: true, "99000-3", 99_000, EntriesAdded: 5, Start: new ReadCount("99000-3", 5));

        var act = () => ConsumerGroups.CreateAsync(db, "s", "g", StreamPosition.NewMessages, head);

        await act.Should().ThrowAsync<RedisServerException>();
        await db.DidNotReceiveWithAnyArgs().StreamCreateConsumerGroupAsync(default, default, default);
    }

    [Fact]
    public async Task A_missing_stream_is_recognized_without_the_ERR_prefix()
    {
        var db = Substitute.For<IDatabase>();
        db.StreamInfoAsync("s").Returns(Task.FromException<StreamInfo>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "no such key")));
        db.ExecuteAsync("XGROUP", Arg.Any<object[]>()).Returns(RedisResult.Create((RedisValue)"OK"));

        (await ConsumerGroups.CreateAsync(db, "s", "g", StreamPosition.NewMessages, head: null)).Should().BeTrue();

        await db.Received(1).ExecuteAsync("XGROUP", Arg.Is<object[]>(a => a.Contains("0-0")));
    }

    // The server read its clock at 100 000 ms, 1 s after the join.
    private static StreamHead Head(bool exists = true, long? lastMs = 0, long? added = 5) =>
        new(ServerMs: 100_000, RepliedAt, exists, lastMs is { } ms ? $"{ms}-0" : RedisValue.Null, lastMs, added, Start: null);
}
