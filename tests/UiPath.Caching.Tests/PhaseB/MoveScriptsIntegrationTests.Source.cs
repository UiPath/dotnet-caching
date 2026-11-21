using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

public partial class MoveScriptsIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcdel_deletes_a_key_and_counts(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v");

        (await env.Scripts.SourceDeleteAsync(src, env.Ctl, "other")).Should().Be(-1);
        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
        (await env.Scripts.SourceDeleteAsync(src, env.Ctl, MoveTestEnvironment.Id)).Should().Be(1);
        (await env.Scripts.SourceDeleteAsync(src, env.Ctl, MoveTestEnvironment.Id)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcset_writes_with_and_without_a_life(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");

        (await env.Scripts.SourceSetAsync(src, env.Ctl, MoveTestEnvironment.Id, "v1", -1, onlyIfAbsent: false)).Should().Be(1);
        (await env.Db.StringGetAsync(src)).Should().Be("v1");
        (await env.Db.KeyTimeToLiveAsync(src)).Should().BeNull();

        (await env.Scripts.SourceSetAsync(src, env.Ctl, MoveTestEnvironment.Id, "v2", 30_000, onlyIfAbsent: false)).Should().Be(1);
        (await env.Db.StringGetAsync(src)).Should().Be("v2");
        (await env.Db.KeyTimeToLiveAsync(src))!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(28)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcset_with_nx_returns_2_on_an_existing_key_and_keeps_it(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");

        (await env.Scripts.SourceSetAsync(src, env.Ctl, MoveTestEnvironment.Id, "first", -1, onlyIfAbsent: true)).Should().Be(1);
        (await env.Scripts.SourceSetAsync(src, env.Ctl, MoveTestEnvironment.Id, "second", -1, onlyIfAbsent: true)).Should().Be(2);

        (await env.Db.StringGetAsync(src)).Should().Be("first");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcset_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");

        (await env.Scripts.SourceSetAsync(src, env.Ctl, "other", "v", -1, onlyIfAbsent: false)).Should().Be(-1);

        (await env.Db.KeyExistsAsync(src)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcexp_with_a_count_sets_the_life_of_a_live_key(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v");

        (await env.Scripts.SourceExpireAsync(src, env.Ctl, MoveTestEnvironment.Id, 30_000)).Should().Be(1);

        (await env.Db.KeyTimeToLiveAsync(src))!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(28)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcexp_with_a_count_returns_0_for_an_absent_key(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.SourceExpireAsync(env.Make("absent"), env.Ctl, MoveTestEnvironment.Id, 30_000)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcexp_with_no_expiry_persists_a_key_that_has_a_ttl(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v", TimeSpan.FromSeconds(30));

        (await env.Scripts.SourceExpireAsync(src, env.Ctl, MoveTestEnvironment.Id, -1)).Should().Be(1);

        (await env.Db.KeyTimeToLiveAsync(src)).Should().BeNull();
        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcexp_with_no_expiry_returns_1_for_a_key_that_has_no_ttl(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v");

        (await env.Scripts.SourceExpireAsync(src, env.Ctl, MoveTestEnvironment.Id, -1)).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcexp_with_no_expiry_returns_0_for_an_absent_key(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.SourceExpireAsync(env.Make("absent"), env.Ctl, MoveTestEnvironment.Id, -1)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcexp_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v", TimeSpan.FromSeconds(30));

        (await env.Scripts.SourceExpireAsync(src, env.Ctl, "other", -1)).Should().Be(-1);

        (await env.Db.KeyTimeToLiveAsync(src)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdel_deletes_only_a_key_holding_the_same_bytes(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        byte[] bytes = [0, 1, 0xFF, 0x80, 7];
        await env.Db.StringSetAsync(src, bytes);

        (await env.Scripts.CompareDeleteAsync(src, env.Ctl, MoveTestEnvironment.Id, new byte[] { 0, 1, 0xFF, 0x80, 8 })).Should().Be(0);
        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
        (await env.Scripts.CompareDeleteAsync(src, env.Ctl, MoveTestEnvironment.Id, bytes)).Should().Be(1);
        (await env.Db.KeyExistsAsync(src)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdel_on_a_hash_key_returns_0(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.HashSetAsync(src, "a", "1");

        (await env.Scripts.CompareDeleteAsync(src, env.Ctl, MoveTestEnvironment.Id, "1")).Should().Be(0);

        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdel_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v");

        (await env.Scripts.CompareDeleteAsync(src, env.Ctl, "other", "v")).Should().Be(-1);

        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdelh_deletes_a_hash_that_is_identical(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.HashSetAsync(src, [new HashEntry("a", "1"), new HashEntry("b", "2")]);

        (await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, MoveTestEnvironment.Id, [new HashEntry("b", "2"), new HashEntry("a", "1")])).Should().Be(1);

        (await env.Db.KeyExistsAsync(src)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdelh_keeps_a_hash_with_an_extra_field(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.HashSetAsync(src, [new HashEntry("a", "1"), new HashEntry("b", "2")]);

        (await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, MoveTestEnvironment.Id, [new HashEntry("a", "1")])).Should().Be(0);

        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdelh_keeps_a_hash_with_a_changed_value(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.HashSetAsync(src, [new HashEntry("a", "1"), new HashEntry("b", "2")]);

        (await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, MoveTestEnvironment.Id, [new HashEntry("a", "1"), new HashEntry("b", "3")])).Should().Be(0);

        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdelh_on_a_string_key_returns_0(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v");

        (await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, MoveTestEnvironment.Id, [new HashEntry("a", "1")])).Should().Be(0);

        (await env.Db.StringGetAsync(src)).Should().Be("v");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdelh_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.HashSetAsync(src, "a", "1");

        (await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, "other", [new HashEntry("a", "1")])).Should().Be(-1);

        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdel_of_a_key_of_another_type_is_refused_for_a_stale_identity_and_zero_for_the_current_one(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.HashSetAsync(src, "a", "1");
        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(2, "id2", (MoveTestEnvironment.Cid, MoveScripts.NewNonce())))).Should().Be(1);

        (await env.Scripts.CompareDeleteAsync(src, env.Ctl, MoveTestEnvironment.Id, "1")).Should().Be(MoveScripts.IdentityMismatch);
        (await env.Scripts.CompareDeleteAsync(src, env.Ctl, "id2", "1")).Should().Be(0);

        (await env.Db.HashGetAsync(src, "a")).Should().Be("1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cmpdelh_of_a_key_of_another_type_is_refused_for_a_stale_identity_and_zero_for_the_current_one(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "1");
        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(2, "id2", (MoveTestEnvironment.Cid, MoveScripts.NewNonce())))).Should().Be(1);

        (await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, MoveTestEnvironment.Id, [new HashEntry("a", "1")])).Should().Be(MoveScripts.IdentityMismatch);
        (await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, "id2", [new HashEntry("a", "1")])).Should().Be(0);

        (await env.Db.StringGetAsync(src)).Should().Be("1");
    }
}
