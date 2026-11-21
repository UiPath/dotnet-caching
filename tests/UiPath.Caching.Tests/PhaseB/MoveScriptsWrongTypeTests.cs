using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MoveScriptsWrongTypeTests(RedisContainerFixture fixture)
{
    public static TheoryData<bool, string> Failing()
    {
        string[] commands = ["hsetc", "hsetc-count", "hdelc", "hdelc-count", "cap", "pub", "ctlsync", "ctlcommit", "gate"];
        var data = new TheoryData<bool, string>();
        foreach (var command in commands)
        {
            data.Add(true, command);
            data.Add(false, command);
        }

        return data;
    }

    // EXEC commits although one of its commands fails; the fallback must raise the error the script raises, not report success.
    [Theory]
    [MemberData(nameof(Failing))]
    public async Task A_command_failing_with_wrongtype_inside_a_committed_exec_is_not_reported_as_success(bool useScripts, string command)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: command is not ("ctlsync" or "ctlcommit" or "gate"));
        var src = env.Make("src");

        var act = () => RunAsync(command, env, src);

        await act.Should().ThrowAsync<RedisServerException>(command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hexpc_on_a_string_key_expires_it_in_both_modes(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v");

        var result = await env.Scripts.HashExpireAsync(src, env.Ctl, MoveTestEnvironment.Id, await env.ServerNowAsync() + 60_000);

        result.Should().Be(1);
        (await env.Db.KeyTimeToLiveAsync(src)).Should().NotBeNull();
    }

    private static async Task<long> RunAsync(string command, MoveTestEnvironment env, RedisKey src)
    {
        const string id = MoveTestEnvironment.Id;
        switch (command)
        {
            case "hsetc":
                await env.Db.StringSetAsync(src, "v");
                return await env.Scripts.HashWriteAsync(src, env.Ctl, id, new ControlledHashWrite([new HashEntry("a", "1")], HashCacheSetOption.HashReplace, -1));
            case "hsetc-count":
                await env.Db.StringSetAsync(src, "v");
                return await env.Scripts.HashWriteAsync(src, env.Ctl, id, new ControlledHashWrite([new HashEntry("a", "1")], HashCacheSetOption.HashReplace, -1, VersionField: "ver"));
            case "hdelc":
                await env.Db.StringSetAsync(src, "v");
                return await env.Scripts.HashDeleteFieldsAsync(src, env.Ctl, id, ["a"]);
            case "hdelc-count":
                await env.Db.StringSetAsync(src, "v");
                return await env.Scripts.HashDeleteFieldsAsync(src, env.Ctl, id, ["a"], versionField: "ver", versionSeed: 100);
            case "cap":
                await env.Db.HashSetAsync(env.Fence, "a", "1");
                return (await env.CaptureAsync("n0")).Code;
            case "pub":
                await env.Db.HashSetAsync(env.Fence, "a", "1");
                return await env.Scripts.PublishAsync(env.Keys, id, "v", new Publication("tok", env.Gate, MoveTestEnvironment.Cid, "n1", 60_000, -1, -1, false));
            case "ctlsync":
                await env.Db.StringSetAsync(env.Ctl, "v");
                return await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(1, id, (MoveTestEnvironment.Cid, "n1")));
            case "ctlcommit":
                await env.Db.StringSetAsync(env.Ctl, "v");
                return await env.Scripts.ControlCommitAsync(env.Ctl, 1, id);
            case "gate":
                await env.Db.StringSetAsync(env.Ctl, "v");
                return await env.Scripts.GateAsync(env.Ctl, id, MoveTestEnvironment.Cid, "CLOSED:x");
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }
}
