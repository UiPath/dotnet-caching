using StackExchange.Redis;

namespace UiPath.Caching.Tests.Broadcast;

public class StreamIdsTests
{
    [Fact]
    public void Entries_reads_the_ids_and_fields_of_an_XREADGROUP_reply()
    {
        var reply = RedisResult.Create(
        [
            RedisResult.Create(
            [
                RedisResult.Create((RedisValue)"stream"),
                RedisResult.Create(
                [
                    RedisResult.Create([RedisResult.Create((RedisValue)"1-0"), RedisResult.Create(new RedisValue[] { "f", "a" })]),
                    RedisResult.Create([RedisResult.Create((RedisValue)"2-0"), RedisResult.Create(new RedisValue[] { "f", "b", "g", "c" })]),
                ]),
            ]),
        ]);

        var entries = StreamIds.Entries(reply);

        entries.Select(e => (string?)e.Id).Should().Equal("1-0", "2-0");
        entries[0]["f"].Should().Be((RedisValue)"a");
        entries[1]["g"].Should().Be((RedisValue)"c");
    }

    [Fact]
    public void Entries_of_a_nil_reply_are_none() =>
        StreamIds.Entries(RedisResult.Create(RedisValue.Null)).Should().BeEmpty();

    [Theory]
    [InlineData("3-0", "0-0", 6L, 3L, true)]
    [InlineData("3-0", "0-0", 4L, 2L, false)]
    [InlineData("1-0", "0-0", 6L, 3L, false)]
    [InlineData("3-0", "0-0", 2L, null, true)]
    [InlineData("1-0", "0-0", 6L, null, false)]
    [InlineData("3-0", "2-5", 4L, null, true)]
    [InlineData("3-0", null, 4L, 2L, true)]
    [InlineData("1-0", null, 4L, 2L, false)]
    public void Gap_reads_the_XINFO_replies(string firstEntry, string? maxDeleted, long entriesAdded, long? entriesRead, bool removed)
    {
        ReadCount? known = null;

        (StreamIds.Gap(Stream(2, "9-0", entriesAdded, firstEntry, maxDeleted), Groups("2-0", entriesRead), "node", ref known) is not null)
            .Should().Be(removed);
    }

    [Fact]
    public void A_count_Redis_cannot_tell_is_every_entry_added_at_the_newest_entry_and_kept_until_the_group_reads_on()
    {
        ReadCount? known = null;

        // A group created at $ on a stream whose history was trimmed away.
        StreamIds.Gap(Stream(0, "9-0", 6, null, "0-0"), Groups("9-0", null), "node", ref known).Should().BeNull();
        known.Should().Be(new ReadCount("9-0", 6));
        StreamIds.Gap(Stream(1, "10-0", 7, "10-0", "0-0"), Groups("9-0", null), "node", ref known).Should().BeNull("the one entry added since is still there");
        StreamIds.Gap(Stream(2, "14-0", 12, "13-0", "0-0"), Groups("9-0", null), "node", ref known).Should().NotBeNull("three of the five added since were trimmed");
    }

    [Fact]
    public void A_count_Redis_cannot_tell_after_the_group_read_on_is_taken_as_a_trim()
    {
        ReadCount? known = new ReadCount("2-0", 1);

        StreamIds.Gap(Stream(2, "9-0", 10, "7-0", "0-0"), Groups("5-0", null), "node", ref known).Should().NotBeNull();
        known.Should().BeNull();
    }

    private static RedisResult Stream(long length, string lastGenerated, long entriesAdded, string? firstEntry, string? maxDeleted)
    {
        var fields = new List<RedisResult>
        {
            RedisResult.Create((RedisValue)"length"), RedisResult.Create((RedisValue)length),
            RedisResult.Create((RedisValue)"last-generated-id"), RedisResult.Create((RedisValue)lastGenerated),
            RedisResult.Create((RedisValue)"entries-added"), RedisResult.Create((RedisValue)entriesAdded),
        };
        if (firstEntry is not null)
        {
            fields.Add(RedisResult.Create((RedisValue)"first-entry"));
            fields.Add(RedisResult.Create([RedisResult.Create((RedisValue)firstEntry)]));
        }

        if (maxDeleted is not null)
        {
            fields.Add(RedisResult.Create((RedisValue)"max-deleted-entry-id"));
            fields.Add(RedisResult.Create((RedisValue)maxDeleted));
        }

        return RedisResult.Create([.. fields]);
    }

    private static RedisResult Groups(string lastDelivered, long? entriesRead) => RedisResult.Create(
    [
        RedisResult.Create(new RedisValue[] { "name", "other", "last-delivered-id", "0-0" }),
        RedisResult.Create(new RedisValue[] { "name", "node", "last-delivered-id", lastDelivered, "entries-read", entriesRead is { } read ? read : RedisValue.Null }),
    ]);
}
