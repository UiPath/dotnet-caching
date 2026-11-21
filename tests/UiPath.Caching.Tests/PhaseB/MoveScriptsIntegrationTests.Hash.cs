using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

public partial class MoveScriptsIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_key_replace_deletes_the_old_fields(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "old", "1");

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, entries: [new HashEntry("a", "1")]));

        result.Should().Be(1);
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["a"] = "1" });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_hash_replace_merges_and_without_an_expiry_removes_an_earlier_ttl(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "old", "1");
        await env.Db.KeyExpireAsync(key, TimeSpan.FromSeconds(30));

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.HashReplace, entries: [new HashEntry("a", "1")]));

        result.Should().Be(1);
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["old"] = "1", ["a"] = "1" });
        (await env.Db.KeyTimeToLiveAsync(key)).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_sets_the_absolute_expiry(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        var at = await env.ServerNowAsync() + 60_000;

        (await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, expireAt: at, entries: [new HashEntry("a", "1")]))).Should().Be(1);

        if (env.SupportsPExpireTime)
        {
            (await env.ExpireTimeAsync(key)).Should().Be(at);
        }
        else
        {
            (await env.Db.KeyTimeToLiveAsync(key))!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_drops_the_named_field(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("x", "1"), new HashEntry("keep", "1")]);

        (await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.HashReplace, drop: "x", entries: [new HashEntry("a", "1")]))).Should().Be(1);

        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["keep"] = "1", ["a"] = "1" });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_counts_versions_from_the_seed_without_losing_bits(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        var seed = (1L << 61) + 12_345;
        var write = Write(HashCacheSetOption.HashReplace, version: "ver", seed: seed, entries: [new HashEntry("a", "1")]);

        (await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, write)).Should().Be(seed + 1);
        (await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, write)).Should().Be(seed + 2);

        (await env.Db.HashGetAsync(key, "ver")).Should().Be((seed + 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_replaces_the_fence_only_when_given_one(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        var write = Write(HashCacheSetOption.HashReplace, entries: [new HashEntry("a", "1")]);

        (await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, write)).Should().Be(1);
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();

        (await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, write, new FenceBump(env.Fence, "fn", 30_000))).Should().Be(1);
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("fn");
        (await env.Db.KeyTimeToLiveAsync(env.Fence)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "old", "1");

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, "other", Write(HashCacheSetOption.KeyReplace, entries: [new HashEntry("a", "1")]), new FenceBump(env.Fence, "fn", 30_000));

        result.Should().Be(MoveScripts.IdentityMismatch);
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["old"] = "1" });
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hdelc_deletes_the_fields_and_bumps_the_fence(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("a", "1"), new HashEntry("b", "2"), new HashEntry("c", "3")]);

        var result = await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, MoveTestEnvironment.Id, ["a", "b"], fence: new FenceBump(env.Fence, "fn", 30_000));

        result.Should().Be(1);
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["c"] = "3" });
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("fn");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hdelc_removes_a_hash_left_with_only_its_version_field(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("a", "1"), new HashEntry("ver", "5")]);

        (await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, MoveTestEnvironment.Id, ["a"], versionField: "ver", versionSeed: 100)).Should().Be(1);

        (await env.Db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hdelc_keeps_a_hash_that_still_has_a_data_field(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("a", "1"), new HashEntry("b", "2"), new HashEntry("ver", "5")]);

        (await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, MoveTestEnvironment.Id, ["a"], versionField: "ver", versionSeed: 100)).Should().Be(1);

        var fields = await FieldsAsync(env, key);
        fields.Should().ContainKey("b").And.ContainKey("ver").And.NotContainKey("a");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hdelc_without_a_fence_creates_none(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("a", "1"), new HashEntry("b", "2")]);

        (await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, MoveTestEnvironment.Id, ["a"])).Should().Be(1);

        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hdelc_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("a", "1"), new HashEntry("b", "2")]);

        var result = await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, "other", ["a"], fence: new FenceBump(env.Fence, "fn", 30_000));

        result.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.HashLengthAsync(key)).Should().Be(2);
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hexpc_sets_the_expiry_of_a_hash_that_exists(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "a", "1");
        var at = await env.ServerNowAsync() + 60_000;

        (await env.Scripts.HashExpireAsync(key, env.Ctl, MoveTestEnvironment.Id, at, new FenceBump(env.Fence, "fn", 30_000))).Should().Be(1);

        if (env.SupportsPExpireTime)
        {
            (await env.ExpireTimeAsync(key)).Should().Be(at);
        }
        else
        {
            (await env.Db.KeyTimeToLiveAsync(key)).Should().NotBeNull();
        }

        (await env.Db.StringGetAsync(env.Fence)).Should().Be("fn");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hexpc_with_no_expiry_persists_the_hash(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "a", "1");
        await env.Db.KeyExpireAsync(key, TimeSpan.FromSeconds(30));

        (await env.Scripts.HashExpireAsync(key, env.Ctl, MoveTestEnvironment.Id, -1)).Should().Be(1);

        (await env.Db.KeyTimeToLiveAsync(key)).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hexpc_returns_0_for_an_absent_hash(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.HashExpireAsync(env.Make("absent"), env.Ctl, MoveTestEnvironment.Id, -1)).Should().Be(0);
        (await env.Scripts.HashExpireAsync(env.Make("absent"), env.Ctl, MoveTestEnvironment.Id, await env.ServerNowAsync() + 60_000)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hexpc_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "a", "1");

        var result = await env.Scripts.HashExpireAsync(key, env.Ctl, "other", await env.ServerNowAsync() + 60_000, new FenceBump(env.Fence, "fn", 30_000));

        result.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyTimeToLiveAsync(key)).Should().BeNull();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    private static ControlledHashWrite Write(HashCacheSetOption mode, long expireAt = -1, string? drop = null, string? version = null, long seed = 0, params HashEntry[] entries) =>
        new(entries, mode, expireAt, drop, version, seed);

    private static async Task<Dictionary<string, string>> FieldsAsync(MoveTestEnvironment env, RedisKey key) =>
        (await env.Db.HashGetAllAsync(key)).ToDictionary(e => (string)e.Name!, e => (string)e.Value!);
}
