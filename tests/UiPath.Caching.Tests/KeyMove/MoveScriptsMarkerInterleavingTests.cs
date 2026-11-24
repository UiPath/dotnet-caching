using System.Globalization;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The transaction forms of the marker scripts with the record changed between what they read and what they commit.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MoveScriptsMarkerInterleavingTests(RedisContainerFixture fixture)
{
    private const string Field = "sm:src-key";

    [Fact]
    public async Task Compaction_does_not_write_back_a_marker_that_was_replaced_after_it_was_read()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        await env.Db.HashSetAsync(env.Ctl, [new("sm:a", "id1|a"), new("pk", "40")]);
        var db = InterleavedDatabase.After(env.Db, nameof(IDatabase.HashGetAllAsync), async () =>
        {
            await env.Db.HashDeleteAsync(env.Ctl, "sm:a");
            await env.Db.HashSetAsync(env.Ctl, "sm:b", "id1|b");
        });

        var length = await new MoveScripts(db, useScripts: false, env.ServerVersion).CompactControlAsync(env.Ctl, MoveTestEnvironment.Id);

        (await env.Db.HashGetAsync(env.Ctl, "sm:b")).Should().Be("id1|b", "a live marker is never dropped");
        (await env.Db.HashExistsAsync(env.Ctl, "sm:a")).Should().BeFalse();
        (await env.Db.HashGetAsync(env.Ctl, "pk")).Should().Be(length.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Compaction_keeps_every_marker_of_a_record(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts);
        await env.Db.HashSetAsync(env.Ctl, [new("sm:a", "id1|a"), new("sm:b", "id1|b"), new("pk", "400")]);

        await env.Scripts.CompactControlAsync(env.Ctl, MoveTestEnvironment.Id);

        (await env.Db.HashGetAsync(env.Ctl, ["sm:a", "sm:b"])).Select(v => (string?)v).Should().Equal("id1|a", "id1|b");
    }

    [Fact]
    public async Task A_first_marked_write_meets_a_limit_that_was_lowered_after_its_admission_read()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var fields = await env.Db.HashLengthAsync(env.Ctl);
        await env.Db.HashSetAsync(env.Ctl, "lim", fields + 5);
        var db = InterleavedDatabase.After(env.Db, nameof(IDatabase.HashLengthAsync), () => env.Db.HashSetAsync(env.Ctl, "lim", fields));

        var reply = await Write(env, db, first: true);

        reply.Code.Should().Be(MoveScripts.RecordFull);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.HashExistsAsync(env.Ctl, Field)).Should().BeFalse();
    }

    [Fact]
    public async Task A_first_marked_write_does_not_keep_the_exemption_of_a_field_that_was_replaced_by_another()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        await env.Db.HashSetAsync(env.Ctl, Field, "id1|old");
        var fields = await env.Db.HashLengthAsync(env.Ctl);
        await env.Db.HashSetAsync(env.Ctl, "lim", fields);
        var db = InterleavedDatabase.After(env.Db, nameof(IDatabase.HashLengthAsync), async () =>
        {
            await env.Db.HashDeleteAsync(env.Ctl, Field);
            await env.Db.HashSetAsync(env.Ctl, "sm:other", "id1|x");
        });

        var reply = await Write(env, db, first: true);

        reply.Code.Should().Be(MoveScripts.RecordFull, "the record is at its limit and the key has no field now");
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
    }

    [Fact]
    public async Task A_rerun_is_admitted_whatever_the_record_became_and_the_peak_never_goes_down()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        await env.Db.HashSetAsync(env.Ctl, [new("lim", "1"), new("pk", "5")]);
        var db = InterleavedDatabase.After(env.Db, nameof(IDatabase.HashLengthAsync), () => env.Db.HashSetAsync(env.Ctl, "pk", "1000"));

        var reply = await Write(env, db, first: false);

        reply.Code.Should().Be(1);
        (await env.Db.HashGetAsync(env.Ctl, "pk")).Should().Be("1000");
        (await env.Db.HashGetAsync(env.Ctl, Field)).Should().Be(reply.Marker);
    }

    private static Task<MarkedBump> Write(MoveTestEnvironment env, IDatabase db, bool first) =>
        new MoveScripts(db, useScripts: false, env.ServerVersion).BumpMarkedAsync(env.Keys, MoveTestEnvironment.Id, "b1", 30_000, "v1", MoveScripts.NoExpiry, Field, first, 0);
}
