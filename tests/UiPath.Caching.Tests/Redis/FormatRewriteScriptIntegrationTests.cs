using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class FormatRewriteScriptIntegrationTests(RedisContainerFixture fixture)
{
    private static readonly byte[] Old = [0, 1, 2, 0xFF, 0x80, (byte)'o'];
    private static readonly byte[] Rewritten = [0x10, 0, 0xFE, (byte)'n'];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rewrite_applies_when_unchanged_and_keeps_ttl(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();
        await db.StringSetAsync(key, Old, TimeSpan.FromSeconds(30));
        var before = (await db.KeyTimeToLiveAsync(key))!.Value;

        var applied = await FormatRewriteScripts.RewriteStringAsync(db, key, Old, Rewritten, useScripts);

        applied.Should().BeTrue();
        ((byte[]?)await db.StringGetAsync(key)).Should().Equal(Rewritten);
        var after = (await db.KeyTimeToLiveAsync(key))!.Value;
        (before - after).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rewrite_applies_to_a_key_with_no_ttl_and_leaves_it_without_one(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();
        await db.StringSetAsync(key, Old);

        (await FormatRewriteScripts.RewriteStringAsync(db, key, Old, Rewritten, useScripts)).Should().BeTrue();

        ((byte[]?)await db.StringGetAsync(key)).Should().Equal(Rewritten);
        (await db.KeyTimeToLiveAsync(key)).Should().BeNull();
        await db.KeyDeleteAsync(key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rewrite_skips_after_a_concurrent_set(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();
        byte[] concurrent = [.. Old[..^1], (byte)'p'];
        await db.StringSetAsync(key, Old, TimeSpan.FromSeconds(30));
        await db.StringSetAsync(key, concurrent, TimeSpan.FromSeconds(30));

        var applied = await FormatRewriteScripts.RewriteStringAsync(db, key, Old, Rewritten, useScripts);

        applied.Should().BeFalse();
        ((byte[]?)await db.StringGetAsync(key)).Should().Equal(concurrent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rewrite_skips_after_delete(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();
        await db.StringSetAsync(key, Old, TimeSpan.FromSeconds(30));
        await db.KeyDeleteAsync(key);

        var applied = await FormatRewriteScripts.RewriteStringAsync(db, key, Old, Rewritten, useScripts);

        applied.Should().BeFalse();
        (await db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hash_rewrite_skips_a_missing_field_and_creates_no_key(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();

        var rewritten = await FormatRewriteScripts.RewriteHashFieldsAsync(db, key, [("f", Old, Rewritten)], useScripts);

        rewritten.Should().Be(0);
        (await db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hash_rewrite_changes_only_the_unchanged_fields_and_keeps_ttl(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();
        byte[] stale = [9, 9];
        await db.HashSetAsync(key, [new HashEntry("kept", Old), new HashEntry("raced", stale)]);
        await db.KeyExpireAsync(key, TimeSpan.FromSeconds(30));
        var before = (await db.KeyTimeToLiveAsync(key))!.Value;

        var rewritten = await FormatRewriteScripts.RewriteHashFieldsAsync(
            db, key, [("kept", Old, Rewritten), ("raced", Old, Rewritten), ("gone", Old, Rewritten)], useScripts);

        rewritten.Should().Be(1);
        ((byte[]?)await db.HashGetAsync(key, "kept")).Should().Equal(Rewritten);
        ((byte[]?)await db.HashGetAsync(key, "raced")).Should().Equal(stale);
        (await db.HashExistsAsync(key, "gone")).Should().BeFalse();
        var after = (await db.KeyTimeToLiveAsync(key))!.Value;
        (before - after).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hash_rewrite_of_no_fields_does_nothing(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();

        (await FormatRewriteScripts.RewriteHashFieldsAsync(db, key, [], useScripts)).Should().Be(0);

        (await db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_key_of_another_type_is_a_skip_whichever_way_it_is_compared(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var (hash, text) = (Unique(), Unique());
        await db.HashSetAsync(hash, "f", Old);
        await db.StringSetAsync(text, Old);

        (await FormatRewriteScripts.RewriteStringAsync(db, hash, Old, Rewritten, useScripts)).Should().BeFalse();
        (await FormatRewriteScripts.RewriteHashFieldsAsync(db, text, [("f", Old, Rewritten)], useScripts)).Should().Be(0);

        ((byte[]?)await db.HashGetAsync(hash, "f")).Should().Equal(Old);
        ((byte[]?)await db.StringGetAsync(text)).Should().Equal(Old);
        await db.KeyDeleteAsync([hash, text]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_concurrent_set_of_the_same_bytes_with_a_new_ttl_keeps_the_new_ttl(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();
        await db.StringSetAsync(key, Old, TimeSpan.FromSeconds(30));
        await db.StringSetAsync(key, Old, TimeSpan.FromSeconds(300));

        (await FormatRewriteScripts.RewriteStringAsync(db, key, Old, Rewritten, useScripts)).Should().BeTrue();

        (await db.KeyTimeToLiveAsync(key))!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(200));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hash_of_many_fields_is_rewritten_whichever_way_it_is_sent(bool useScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        var key = Unique();
        var names = Enumerable.Range(0, 45).Select(i => $"f{i}").ToArray();
        await db.HashSetAsync(key, [.. names.Select(n => new HashEntry(n, Old))]);

        var rewritten = await FormatRewriteScripts.RewriteHashFieldsAsync(db, key, [.. names.Select(n => ((RedisValue)n, (ReadOnlyMemory<byte>)Old, (ReadOnlyMemory<byte>)Rewritten))], useScripts);

        rewritten.Should().Be(45);
        (await db.HashGetAllAsync(key)).Should().OnlyContain(e => ((byte[]?)e.Value)!.SequenceEqual(Rewritten));
        await db.KeyDeleteAsync(key);
    }

    [Theory]
    [InlineData("_version_")]
    [InlineData("_metadata_")]
    [InlineData("_anything_")]
    public async Task A_reserved_field_is_refused(string field)
    {
        var db = Substitute.For<IDatabase>();

        var act = () => FormatRewriteScripts.RewriteHashFieldsAsync(db, "k", [(field, Old, Rewritten)]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage($"*{field}*");
        db.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_field_named_twice_is_refused()
    {
        var db = Substitute.For<IDatabase>();

        var act = () => FormatRewriteScripts.RewriteHashFieldsAsync(db, "k", [("f", Old, Rewritten), ("f", Old, Rewritten)]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*f*");
        db.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_that_forgot_the_script_is_sent_it_again()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync(allowAdmin: true);
        var db = mux.GetDatabase();
        var (text, hash) = (Unique(), Unique());
        await db.StringSetAsync(text, Old);
        await db.HashSetAsync(hash, "f", Old);
        await mux.GetServer(mux.GetEndPoints()[0]).ScriptFlushAsync();

        (await FormatRewriteScripts.RewriteStringAsync(db, text, Old, Rewritten)).Should().BeTrue();
        (await FormatRewriteScripts.RewriteHashFieldsAsync(db, hash, [("f", Old, Rewritten)])).Should().Be(1);
        await db.StringSetAsync(text, Old);
        (await FormatRewriteScripts.RewriteStringAsync(db, text, Old, Rewritten)).Should().BeTrue();
        await db.KeyDeleteAsync([text, hash]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hash_rewrite_of_no_fields_sends_nothing(bool useScripts)
    {
        var db = Substitute.For<IDatabase>();

        (await FormatRewriteScripts.RewriteHashFieldsAsync(db, "k", [], useScripts)).Should().Be(0);

        db.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Fallback_transaction_behaves_the_same()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectAsync();
        var db = mux.GetDatabase();

        var script = await ScenarioAsync(db, useScripts: true);
        var transaction = await ScenarioAsync(db, useScripts: false);

        transaction.Should().Be(script);
        script.Should().Be("applied:True|raced:False|gone:False|hash:1|hash-raced:Old|ttl-kept:True");
    }

    [Fact]
    public async Task A_server_that_refuses_scripts_falls_back_to_the_transaction_and_says_so()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        await using var admin = await ConnectAsync();
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var user = $"it-norewrite-{Guid.NewGuid():N}";
        await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-@scripting");
        try
        {
            await using var restricted = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},user={user},password=pw");
            var db = restricted.GetDatabase();
            var refused = new List<Exception>();
            var (stringKey, hashKey) = (Unique(), Unique());
            await db.StringSetAsync(stringKey, Old, TimeSpan.FromSeconds(30));
            await db.HashSetAsync(hashKey, "f", Old);

            var applied = await FormatRewriteScripts.RewriteStringAsync(db, stringKey, Old, Rewritten, useScripts: true, refused.Add);
            var rewritten = await FormatRewriteScripts.RewriteHashFieldsAsync(db, hashKey, [("f", Old, Rewritten)], useScripts: true, refused.Add);

            applied.Should().BeTrue();
            rewritten.Should().Be(1);
            refused.Should().HaveCount(2);
            ((byte[]?)await db.StringGetAsync(stringKey)).Should().Equal(Rewritten);
            ((byte[]?)await db.HashGetAsync(hashKey, "f")).Should().Equal(Rewritten);
            (await db.KeyTimeToLiveAsync(stringKey)).Should().NotBeNull();

            await db.StringSetAsync(stringKey, Old, TimeSpan.FromSeconds(30));
            (await FormatRewriteScripts.RewriteStringAsync(db, stringKey, Old, Rewritten, useScripts: false, refused.Add)).Should().BeTrue();
            refused.Should().HaveCount(2);
        }
        finally
        {
            await server.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    private static string Unique() => $"it-rewrite-{Guid.NewGuid():N}";

    private static async Task<string> ScenarioAsync(IDatabase db, bool useScripts)
    {
        var (applied, raced, gone, hash) = (Unique(), Unique(), Unique(), Unique());
        await db.StringSetAsync(applied, Old, TimeSpan.FromSeconds(30));
        await db.StringSetAsync(raced, new byte[] { 1 }, TimeSpan.FromSeconds(30));
        await db.HashSetAsync(hash, [new HashEntry("a", Old), new HashEntry("b", new byte[] { 7 })]);
        await db.KeyExpireAsync(hash, TimeSpan.FromSeconds(30));

        var a = await FormatRewriteScripts.RewriteStringAsync(db, applied, Old, Rewritten, useScripts);
        var r = await FormatRewriteScripts.RewriteStringAsync(db, raced, Old, Rewritten, useScripts);
        var g = await FormatRewriteScripts.RewriteStringAsync(db, gone, Old, Rewritten, useScripts);
        var h = await FormatRewriteScripts.RewriteHashFieldsAsync(db, hash, [("a", Old, Rewritten), ("b", Old, Rewritten)], useScripts);
        var hashRaced = (byte[]?)await db.HashGetAsync(hash, "b") is [7] ? "Old" : "Changed";
        var kept = await db.KeyTimeToLiveAsync(applied) is not null && await db.KeyTimeToLiveAsync(hash) is not null;
        return $"applied:{a}|raced:{r}|gone:{g}|hash:{h}|hash-raced:{hashRaced}|ttl-kept:{kept}";
    }

    private async Task<ConnectionMultiplexer> ConnectAsync(bool allowAdmin = false) =>
        await ConnectionMultiplexer.ConnectAsync(allowAdmin ? fixture.ConnectionString + ",allowAdmin=true" : fixture.ConnectionString);
}
