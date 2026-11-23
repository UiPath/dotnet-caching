using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MoveScriptsWatchAbortTests(RedisContainerFixture fixture)
{
    public static TheoryData<string> Commands =>
        ["pub", "pubh", "fill", "fillh", "bump", "srcdel", "srcset", "srcexp", "cmpdel", "cmpdelh", "ctlsync", "ctlcommit", "gate"];

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task One_aborted_exec_with_every_predicate_holding_is_sent_again_and_gives_the_script_code(string command)
    {
        await using var scriptEnv = await MoveTestEnvironment.CreateAsync(fixture, useScripts: true);
        var expected = await RunAsync(command, scriptEnv, scriptEnv.Scripts);
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1);

        var code = await RunAsync(command, env, new MoveScripts(harness.Database, useScripts: false, env.ServerVersion));

        code.Should().Be(expected, command);
        code.Should().BeGreaterThanOrEqualTo(0, command);
        harness.Aborted.Should().Be(1, command);
        harness.Executions.Should().BeGreaterThanOrEqualTo(2, command);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task An_exec_that_aborts_on_every_attempt_throws_after_the_attempt_limit(string command)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: int.MaxValue);

        var act = () => RunAsync(command, env, new MoveScripts(harness.Database, useScripts: false, env.ServerVersion));

        await act.Should().ThrowAsync<RedisException>();
        harness.Aborted.Should().Be(MoveScripts.MaxWatchAborts, command);
        harness.Executions.Should().Be(MoveScripts.MaxWatchAborts, command);
    }

    [Fact]
    public async Task A_capture_whose_fence_was_evicted_before_it_was_read_runs_again_and_returns_a_token()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var evicting = new FenceEvictingDatabase(env.Db, toEvict: 1);
        var scripts = new MoveScripts(evicting.Database, useScripts: false, env.ServerVersion);

        var capture = await scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n0", 30_000);

        capture.Code.Should().Be(0);
        capture.Token.Should().Be("n0");
        evicting.Batches.Should().Be(2);
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n0");
    }

    [Fact]
    public async Task A_capture_whose_fence_is_gone_every_time_throws_after_the_attempt_limit()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var evicting = new FenceEvictingDatabase(env.Db, toEvict: int.MaxValue);
        var scripts = new MoveScripts(evicting.Database, useScripts: false, env.ServerVersion);

        var act = () => scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n0", 30_000);

        await act.Should().ThrowAsync<RedisException>();
        evicting.Batches.Should().Be(MoveScripts.MaxWatchAborts);
    }

    [Fact]
    public async Task The_cleanup_of_a_count_only_hash_after_hdelc_is_sent_again_when_its_exec_aborts()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var src = await CountedHashAsync(env);
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, skip: 1);

        var result = await new MoveScripts(harness.Database, useScripts: false, env.ServerVersion)
            .HashDeleteFieldsAsync(src, env.Ctl, MoveTestEnvironment.Id, ["a"], versionField: "ver", versionSeed: 100);

        result.Should().Be(1);
        (await env.Db.KeyExistsAsync(src)).Should().BeFalse();
        harness.Aborted.Should().Be(1);
        harness.Executions.Should().Be(3);
    }

    [Fact]
    public async Task A_cleanup_of_a_count_only_hash_that_aborts_every_time_throws_after_the_attempt_limit()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var src = await CountedHashAsync(env);
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: long.MaxValue, skip: 1);

        var act = () => new MoveScripts(harness.Database, useScripts: false, env.ServerVersion)
            .HashDeleteFieldsAsync(src, env.Ctl, MoveTestEnvironment.Id, ["a"], versionField: "ver", versionSeed: 100);

        await act.Should().ThrowAsync<RedisException>();
        harness.Aborted.Should().Be(MoveScripts.MaxWatchAborts);
        harness.Executions.Should().Be(1 + MoveScripts.MaxWatchAborts);
    }

    // The setup goes through the environment's own scripts; only the command under test goes through <paramref name="scripts"/>.
    private static async Task<long> RunAsync(string command, MoveTestEnvironment env, MoveScripts scripts)
    {
        const string id = MoveTestEnvironment.Id;
        var src = env.Make("src");
        switch (command)
        {
            case "pub":
                var forPub = await env.CaptureAsync("n0");
                return await scripts.PublishAsync(env.Keys, id, "v", MoveTestEnvironment.PublicationFor(forPub, "n1"));
            case "pubh":
                var forPubh = await env.CaptureAsync("n0");
                return await scripts.PublishHashAsync(env.Keys, await env.StageHashAsync(), id, MoveTestEnvironment.PublicationFor(forPubh, "n1"));
            case "fill":
                return await scripts.FillAsync(env.Keys, id, "v", -1, "f1", 60_000);
            case "fillh":
                return await scripts.FillHashAsync(env.Keys, await env.StageHashAsync(), id, 30_000, "f1", 60_000);
            case "bump":
                return await scripts.BumpAsync(env.Keys, id, "f1", 60_000, BumpOperation.Set, "v");
            case "srcdel":
                await env.Db.StringSetAsync(src, "v");
                return await scripts.SourceDeleteAsync(src, env.Ctl, id);
            case "srcset":
                return await scripts.SourceSetAsync(src, env.Ctl, id, "v", -1, onlyIfAbsent: false);
            case "srcexp":
                await env.Db.StringSetAsync(src, "v");
                return await scripts.SourceExpireAsync(src, env.Ctl, id, 30_000);
            case "cmpdel":
                await env.Db.StringSetAsync(src, "v");
                return await scripts.CompareDeleteAsync(src, env.Ctl, id, "v");
            case "cmpdelh":
                await env.Db.HashSetAsync(src, "a", "v");
                return await scripts.CompareDeleteHashAsync(src, env.Ctl, id, [new HashEntry("a", "v")]);
            case "ctlsync":
                return await scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(2, "id2", (MoveTestEnvironment.Cid, "n2")));
            case "ctlcommit":
                return await scripts.ControlCommitAsync(env.Ctl, 1, id);
            case "gate":
                return await scripts.GateAsync(env.Ctl, id, MoveTestEnvironment.Cid, "CLOSED:x");
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }

    private static async Task<RedisKey> CountedHashAsync(MoveTestEnvironment env)
    {
        var src = env.Make("src");
        await env.Db.HashSetAsync(src, [new HashEntry("a", "1"), new HashEntry("ver", "5")]);
        return src;
    }
}
