using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

public partial class MoveScriptsIntegrationTests
{
    [Theory]
    [MemberData(nameof(StaleCommands))]
    public async Task A_command_is_refused_and_changes_nothing_when_there_is_no_control_record(bool useScripts, string command)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);

        var result = await SendWithoutRecordAsync(env, command);

        result.Should().Be(MoveScripts.IdentityMismatch, command);
        (await env.Db.KeyExistsAsync(env.Ctl)).Should().BeFalse(command);
    }

    private static async Task<long> SendWithoutRecordAsync(MoveTestEnvironment env, string command)
    {
        const string id = MoveTestEnvironment.Id;
        var keys = env.Keys;
        var src = env.Make("src");
        var staging = env.Make("staging");
        var publication = new Publication("tok", "OPEN:x", MoveTestEnvironment.Cid, "f-new", 60_000, -1, -1, false);
        switch (command)
        {
            case "bump":
                await env.Db.StringSetAsync(env.Dst, "old");
                await env.Db.StringSetAsync(env.Fence, "tok");
                var bump = await env.Scripts.BumpAsync(keys, id, "f-new", 60_000, BumpOperation.Set, "new");
                await AssertDestinationAndFenceAsync(env, "old", "tok");
                return bump;
            case "fill":
                await env.Db.StringSetAsync(env.Fence, "tok");
                var fill = await env.Scripts.FillAsync(keys, id, "new", -1, "f-new", 60_000);
                (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
                (await env.Db.StringGetAsync(env.Fence)).Should().Be("tok");
                return fill;
            case "fillh":
                await env.Db.HashSetAsync(staging, "a", "1");
                await env.Db.StringSetAsync(env.Fence, "tok");
                var fillh = await env.Scripts.FillHashAsync(keys, staging, id, -1, "f-new", 60_000);
                (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
                (await env.Db.StringGetAsync(env.Fence)).Should().Be("tok");
                return fillh;
            case "pub":
                await env.Db.StringSetAsync(env.Dst, "old");
                await env.Db.StringSetAsync(env.Fence, "tok");
                var pub = await env.Scripts.PublishAsync(keys, id, "new", publication);
                await AssertDestinationAndFenceAsync(env, "old", "tok");
                return pub;
            case "pubh":
                await env.Db.StringSetAsync(env.Dst, "old");
                await env.Db.StringSetAsync(env.Fence, "tok");
                await env.Db.HashSetAsync(staging, "a", "1");
                var pubh = await env.Scripts.PublishHashAsync(keys, staging, id, publication);
                await AssertDestinationAndFenceAsync(env, "old", "tok");
                return pubh;
            case "srcdel":
                await env.Db.StringSetAsync(src, "v");
                var srcdel = await env.Scripts.SourceDeleteAsync(src, env.Ctl, id);
                (await env.Db.StringGetAsync(src)).Should().Be("v");
                return srcdel;
            case "srcset":
                var srcset = await env.Scripts.SourceSetAsync(src, env.Ctl, id, "new", -1, onlyIfAbsent: false);
                (await env.Db.KeyExistsAsync(src)).Should().BeFalse();
                return srcset;
            case "srcexp":
                await env.Db.StringSetAsync(src, "v");
                var srcexp = await env.Scripts.SourceExpireAsync(src, env.Ctl, id, 1_000);
                (await env.Db.KeyTimeToLiveAsync(src)).Should().BeNull();
                return srcexp;
            case "cmpdel":
                await env.Db.StringSetAsync(src, "v");
                var cmpdel = await env.Scripts.CompareDeleteAsync(src, env.Ctl, id, "v");
                (await env.Db.StringGetAsync(src)).Should().Be("v");
                return cmpdel;
            case "cmpdelh":
                await env.Db.HashSetAsync(src, "a", "v");
                var cmpdelh = await env.Scripts.CompareDeleteHashAsync(src, env.Ctl, id, [new HashEntry("a", "v")]);
                (await env.Db.HashGetAsync(src, "a")).Should().Be("v");
                return cmpdelh;
            case "hsetc":
                var hsetc = await env.Scripts.HashWriteAsync(src, env.Ctl, id, Write(HashCacheSetOption.KeyReplace, entries: [new HashEntry("a", "new")]));
                (await env.Db.KeyExistsAsync(src)).Should().BeFalse();
                return hsetc;
            case "hdelc":
                await env.Db.HashSetAsync(src, "a", "v");
                var hdelc = await env.Scripts.HashDeleteFieldsAsync(src, env.Ctl, id, ["a"]);
                (await env.Db.HashGetAsync(src, "a")).Should().Be("v");
                return hdelc;
            case "hexpc":
                await env.Db.HashSetAsync(src, "a", "v");
                var hexpc = await env.Scripts.HashExpireAsync(src, env.Ctl, id, await env.ServerNowAsync() + 1_000);
                (await env.Db.KeyTimeToLiveAsync(src)).Should().BeNull();
                return hexpc;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }

    private static async Task AssertDestinationAndFenceAsync(MoveTestEnvironment env, string destination, string fence)
    {
        (await env.Db.StringGetAsync(env.Dst)).Should().Be(destination);
        (await env.Db.StringGetAsync(env.Fence)).Should().Be(fence);
    }
}
