using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateHoldAndLeaseScriptsTests(RedisContainerFixture fixture)
{
    public static TheoryData<string> Commands => ["holdset", "holdclear", "lease-acquire", "lease-renew", "lease-release", "transit-extras"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hold_is_set_and_cleared_only_on_an_existing_hash_and_never_creates_one(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = env.Make("state");

        (await env.Scripts.HoldSetAsync(state, "hold:op", "why")).Should().Be(0);
        (await env.Scripts.HoldClearAsync(state, "hold:op")).Should().Be(0);
        (await env.Db.KeyExistsAsync(state)).Should().BeFalse();

        await env.Db.HashSetAsync(state, "st", "S1");
        (await env.Scripts.HoldSetAsync(state, "hold:op", "why")).Should().Be(1);
        (await env.Scripts.HoldSetAsync(state, "hold:auto", "canary")).Should().Be(1);
        (await env.Db.HashGetAsync(state, "hold:op")).Should().Be("why");
        (await env.Scripts.HoldClearAsync(state, "hold:op", "hold:auto")).Should().Be(1);
        (await env.Db.HashGetAllAsync(state)).Select(e => (string)e.Name!).Should().Equal("st");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_store_sets_reads_and_clears_the_two_hold_classes(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        (await kit.Store.SetHoldAsync(kit.Group, HoldKind.Operator, "x")).Should().BeFalse("no state yet");
        await kit.InitAsync("a");

        (await kit.Store.SetHoldAsync(kit.Group, HoldKind.Operator, "operator hold")).Should().BeTrue();
        (await kit.Store.SetHoldAsync(kit.Group, HoldKind.Automatic, "canary")).Should().BeTrue();
        var held = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        held.Should().Match<StateRecord>(s => s.HoldOp == "operator hold" && s.HoldAuto == "canary");

        await kit.Store.ClearHoldAsync(kit.Group, HoldKind.Automatic);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.HoldOp == "operator hold" && s.HoldAuto == string.Empty);
        await kit.Store.ClearHoldAsync(kit.Group, HoldKind.Operator, HoldKind.Automatic);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.HoldOp == string.Empty && s.HoldAuto == string.Empty);
        var empty = () => kit.Store.SetHoldAsync(kit.Group, HoldKind.Operator, string.Empty);
        await empty.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transit_writes_mv_ct_rel_and_the_hold_and_deletes_the_fields_a_rebuilt_state_must_not_keep(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = env.Make("state");
        await env.Db.HashSetAsync(
            state,
            [new("st", "S4"), new("dir", "fwd"), new("nPop", "1"), new("ep", 5), new("id", "id-5"), new("dur", "1"), new("res:a", "0"), new("appr:close", "x"), new("allAck", "id-5"), new("lswept", "q")]);
        var target = new StateTarget(
            "S4", "fwd", 1, 9, "id-9", "S4:fwd:1:5:id-5", 1, MoveId: 9, Claim: "tok", Move: "fmt:1>2", Release: "tok:9:S0", HoldOp: "reassert", Clear: ["res:a", "appr:close", "allAck", "lswept"]);

        (await env.Scripts.TransitAsync(state, StateExpectation.Holding(5, "id-5", 1), target)).Should().Be(MoveScripts.Written);

        var held = (await env.Db.HashGetAllAsync(state)).ToDictionary(e => (string)e.Name!, e => (string)e.Value!);
        held.Should().Contain("mv", "fmt:1>2").And.Contain("ct", "tok").And.Contain("rel", "tok:9:S0").And.Contain("hold:op", "reassert").And.Contain("moveId", "9");
        held.Keys.Should().NotContain(["res:a", "appr:close", "allAck", "lswept"]);
        var record = StateRecord.Parse(await env.Db.HashGetAllAsync(state))!;
        record.Move.Should().Be("fmt:1>2");
        record.KeepsSourceFallback("a").Should().BeTrue("an absent residue count is unknown");
    }

    [Fact]
    public void A_residue_count_of_zero_ends_the_source_fallback_and_any_other_value_keeps_it()
    {
        var record = new StateRecord("S4", "fwd", 1, "i", true, 1, string.Empty, null, "a+b+c", "cs", string.Empty, string.Empty, string.Empty, string.Empty, Residue: new Dictionary<string, long> { ["a"] = 0, ["b"] = 7 });

        record.KeepsSourceFallback("a").Should().BeFalse();
        record.KeepsSourceFallback("b").Should().BeTrue();
        record.KeepsSourceFallback("c").Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_lease_is_taken_when_free_renewed_by_its_holder_and_refused_to_another(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var lease = env.Make("lead");

        (await env.Scripts.LeaseAsync(lease, "a", 5000)).Should().Be(MoveScripts.LeaseAcquired);
        (await env.Scripts.LeaseAsync(lease, "b", 5000)).Should().Be(MoveScripts.LeaseHeldByAnother);
        (await env.Scripts.LeaseAsync(lease, "a", 9000)).Should().Be(MoveScripts.LeaseRenewed);
        (await env.Db.KeyTimeToLiveAsync(lease))!.Value.TotalMilliseconds.Should().BeInRange(5500, 9000);
        (await env.Scripts.LeaseReleaseAsync(lease, "b")).Should().BeFalse();
        (await env.Scripts.LeaseReleaseAsync(lease, "a")).Should().BeTrue();
        (await env.Db.KeyExistsAsync(lease)).Should().BeFalse();
        (await env.Scripts.LeaseAsync(lease, "b", 5000)).Should().Be(MoveScripts.LeaseAcquired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_trial_script_tells_scripts_from_the_fallback(bool useScripts)
    {
        var expected = useScripts ? ScriptSupport.Scripts : ScriptSupport.Fallback;
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);

        (await env.Scripts.ScriptSupportAsync()).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task One_aborted_exec_with_every_predicate_holding_is_sent_again_and_gives_the_script_code(string command)
    {
        await using var scriptEnv = await MoveTestEnvironment.CreateAsync(fixture, useScripts: true, openControl: false);
        var expected = await RunAsync(command, scriptEnv, scriptEnv.Scripts, await PrepareAsync(command, scriptEnv));
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false, openControl: false);
        var key = await PrepareAsync(command, env);
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, field: "zz", watched: key, absent: command == "lease-acquire", touch: Touch(command, key));

        var code = await RunAsync(command, env, new MoveScripts(harness.Database, useScripts: false, env.ServerVersion), key);

        code.Should().Be(expected, command);
        code.Should().BePositive(command);
        harness.Aborted.Should().Be(1, command);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task An_exec_that_aborts_on_every_attempt_throws_after_the_attempt_limit(string command)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false, openControl: false);
        var key = await PrepareAsync(command, env);
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: int.MaxValue, field: "zz", watched: key, absent: command == "lease-acquire", touch: Touch(command, key));

        var act = () => RunAsync(command, env, new MoveScripts(harness.Database, useScripts: false, env.ServerVersion), key);

        await act.Should().ThrowAsync<RedisException>();
        harness.Aborted.Should().Be(MoveScripts.MaxWatchAborts, command);
    }

    private static Func<IDatabase, Task>? Touch(string command, RedisKey key) => command switch
    {
        "lease-acquire" => db => db.StringSetAsync(key, "x"),
        "lease-renew" or "lease-release" => db => db.StringSetAsync(key, "a", null, keepTtl: true),
        _ => null,
    };

    private static async Task<RedisKey> PrepareAsync(string command, MoveTestEnvironment env)
    {
        var key = env.Make("key");
        switch (command)
        {
            case "holdset":
            case "holdclear":
                await env.Db.HashSetAsync(key, [new("st", "S1"), new("hold:op", "w")]);
                break;
            case "lease-renew":
            case "lease-release":
                await env.Db.StringSetAsync(key, "a", TimeSpan.FromSeconds(30));
                break;
            case "transit-extras":
                await env.Db.HashSetAsync(key, [new("st", "S1"), new("dir", "fwd"), new("nPop", "0"), new("ep", 5), new("id", "id-5"), new("dur", "1"), new("res:a", "1")]);
                break;
        }

        return key;
    }

    private static async Task<long> RunAsync(string command, MoveTestEnvironment env, MoveScripts scripts, RedisKey key)
    {
        _ = env;
        switch (command)
        {
            case "holdset":
                return await scripts.HoldSetAsync(key, "hold:op", "why");
            case "holdclear":
                return await scripts.HoldClearAsync(key, "hold:op");
            case "lease-acquire":
            case "lease-renew":
                return await scripts.LeaseAsync(key, "a", 30_000);
            case "lease-release":
                return await scripts.LeaseReleaseAsync(key, "a") ? 1 : 0;
            case "transit-extras":
                return await scripts.TransitAsync(key, StateExpectation.Holding(5, "id-5", 1), new StateTarget("S1", "fwd", 0, 6, "id-6", "p", 1, Move: "m", Clear: ["res:a"]));
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }
}
