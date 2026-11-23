using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public partial class MoveScriptsIntegrationTests
{
    public static TheoryData<bool, string> StaleCommands()
    {
        string[] commands = ["bump", "fill", "fillh", "pub", "pubh", "srcdel", "srcset", "srcexp", "cmpdel", "cmpdelh", "hsetc", "hdelc", "hexpc"];
        var data = new TheoryData<bool, string>();
        foreach (var command in commands)
        {
            data.Add(true, command);
            data.Add(false, command);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StaleCommands))]
    public async Task A_delayed_command_of_the_old_identity_is_refused_after_the_transition(bool useScripts, string command)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(2, "id2", (MoveTestEnvironment.Cid, MoveScripts.NewNonce())))).Should().Be(1);

        var result = await SendStaleAsync(env, command);

        result.Should().Be(MoveScripts.IdentityMismatch, command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stale_ctlsync_after_a_later_epoch_returns_0_and_leaves_the_record(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);
        await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(1, "id1", ("c1", "n1")));
        await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(3, "id3", ("c1", "n3")));

        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(1, "id1", ("c1", "n1")))).Should().Be(0);

        (await env.Db.HashGetAsync(env.Ctl, ["ep", "id", "c:c1"])).Select(v => (string?)v).Should().Equal("3", "id3", "OPEN:n3");
    }

    // The later write is acknowledged under id2; the old-id command must leave it standing.
    private static async Task<long> SendStaleAsync(MoveTestEnvironment env, string command)
    {
        const string stale = MoveTestEnvironment.Id;
        const string later = "id2";
        var keys = env.Keys;
        var src = env.Make("src");
        var staging = env.Make("staging");
        switch (command)
        {
            case "bump":
                (await env.Scripts.BumpAsync(keys, later, "f-later", 60_000, BumpOperation.Set, "later")).Should().Be(1);
                var bump = await env.Scripts.BumpAsync(keys, stale, "f-stale", 60_000, BumpOperation.Delete);
                (await env.Db.StringGetAsync(env.Dst)).Should().Be("later");
                (await env.Db.StringGetAsync(env.Fence)).Should().Be("f-later");
                return bump;
            case "fill":
                (await env.Scripts.BumpAsync(keys, later, "f-later", 60_000, BumpOperation.Set, "later")).Should().Be(1);
                await env.Db.KeyDeleteAsync(env.Dst);
                var fill = await env.Scripts.FillAsync(keys, stale, "stale", -1, "f-stale", 60_000);
                (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
                (await env.Db.StringGetAsync(env.Fence)).Should().Be("f-later");
                return fill;
            case "fillh":
                (await env.Scripts.BumpAsync(keys, later, "f-later", 60_000, BumpOperation.None)).Should().Be(1);
                await env.Db.HashSetAsync(staging, "a", "stale");
                var fillh = await env.Scripts.FillHashAsync(keys, staging, stale, -1, "f-stale", 60_000);
                (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
                (await env.Db.StringGetAsync(env.Fence)).Should().Be("f-later");
                return fillh;
            case "pub":
                (await env.Scripts.BumpAsync(keys, later, "f-later", 60_000, BumpOperation.Set, "later")).Should().Be(1);
                var pub = await env.Scripts.PublishAsync(keys, stale, "stale", new Publication("f-later", "OPEN:x", MoveTestEnvironment.Cid, "f-stale", 60_000, -1, -1, false));
                (await env.Db.StringGetAsync(env.Dst)).Should().Be("later");
                (await env.Db.StringGetAsync(env.Fence)).Should().Be("f-later");
                return pub;
            case "pubh":
                (await env.Scripts.BumpAsync(keys, later, "f-later", 60_000, BumpOperation.Set, "later")).Should().Be(1);
                await env.Db.HashSetAsync(staging, "a", "stale");
                var pubh = await env.Scripts.PublishHashAsync(keys, staging, stale, new Publication("f-later", "OPEN:x", MoveTestEnvironment.Cid, "f-stale", 60_000, -1, -1, false));
                (await env.Db.StringGetAsync(env.Dst)).Should().Be("later");
                (await env.Db.StringGetAsync(env.Fence)).Should().Be("f-later");
                return pubh;
            case "srcdel":
                (await env.Scripts.SourceSetAsync(src, env.Ctl, later, "later", -1, false)).Should().Be(1);
                var srcdel = await env.Scripts.SourceDeleteAsync(src, env.Ctl, stale);
                (await env.Db.StringGetAsync(src)).Should().Be("later");
                return srcdel;
            case "srcset":
                (await env.Scripts.SourceSetAsync(src, env.Ctl, later, "later", -1, false)).Should().Be(1);
                var srcset = await env.Scripts.SourceSetAsync(src, env.Ctl, stale, "stale", 30_000, false);
                (await env.Db.StringGetAsync(src)).Should().Be("later");
                (await env.Db.KeyTimeToLiveAsync(src)).Should().BeNull();
                return srcset;
            case "srcexp":
                (await env.Scripts.SourceSetAsync(src, env.Ctl, later, "later", -1, false)).Should().Be(1);
                var srcexp = await env.Scripts.SourceExpireAsync(src, env.Ctl, stale, 1_000);
                (await env.Db.KeyTimeToLiveAsync(src)).Should().BeNull();
                return srcexp;
            case "cmpdel":
                (await env.Scripts.SourceSetAsync(src, env.Ctl, later, "later", -1, false)).Should().Be(1);
                var cmpdel = await env.Scripts.CompareDeleteAsync(src, env.Ctl, stale, "later");
                (await env.Db.StringGetAsync(src)).Should().Be("later");
                return cmpdel;
            case "cmpdelh":
                await env.Db.HashSetAsync(src, "a", "later");
                var cmpdelh = await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, stale, [new HashEntry("a", "later")]);
                (await env.Db.HashGetAsync(src, "a")).Should().Be("later");
                return cmpdelh;
            case "hsetc":
                (await env.Scripts.HashWriteAsync(src, env.Ctl, later, Write(HashCacheSetOption.KeyReplace, entries: [new HashEntry("a", "later")]))).Should().Be(1);
                var hsetc = await env.Scripts.HashWriteAsync(src, env.Ctl, stale, Write(HashCacheSetOption.KeyReplace, entries: [new HashEntry("a", "stale")]));
                (await env.Db.HashGetAsync(src, "a")).Should().Be("later");
                return hsetc;
            case "hdelc":
                (await env.Scripts.HashWriteAsync(src, env.Ctl, later, Write(HashCacheSetOption.KeyReplace, entries: [new HashEntry("a", "later")]))).Should().Be(1);
                var hdelc = await env.Scripts.HashDeleteFieldsAsync(src, env.Ctl, stale, ["a"]);
                (await env.Db.HashGetAsync(src, "a")).Should().Be("later");
                return hdelc;
            case "hexpc":
                (await env.Scripts.HashWriteAsync(src, env.Ctl, later, Write(HashCacheSetOption.KeyReplace, entries: [new HashEntry("a", "later")]))).Should().Be(1);
                var hexpc = await env.Scripts.HashExpireAsync(src, env.Ctl, stale, await env.ServerNowAsync() + 1_000);
                (await env.Db.KeyTimeToLiveAsync(src)).Should().BeNull();
                return hexpc;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }
}
