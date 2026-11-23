using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateScriptsTests(RedisContainerFixture fixture)
{
    private const string Id = "id-a";

    public static TheoryData<string> Commands => ["transit", "transit-create", "durable", "allack", "init", "init-refresh"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transit_creates_an_absent_hash_with_every_field_the_target_carries(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = env.Make("state");
        var target = new StateTarget("S1", "fwd", 0, 5, Id, "S0:none:0:4:id-0", 1234, MoveId: 5, Claim: "tok", Caches: ("a+b", "hash"));

        (await env.Scripts.TransitAsync(state, StateExpectation.Absent, target)).Should().Be(MoveScripts.Written);

        var held = (await env.Db.HashGetAllAsync(state)).ToDictionary(e => (string)e.Name!, e => (string)e.Value!);
        held.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["st"] = "S1", ["dir"] = "fwd", ["nPop"] = "0", ["ep"] = "5", ["id"] = Id, ["dur"] = "0", ["prev"] = "S0:none:0:4:id-0", ["upd"] = "1234",
            ["moveId"] = "5", ["ct"] = "tok", ["caches"] = "a+b", ["cs"] = "hash",
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transit_replaces_the_expected_identity_and_keeps_the_fields_it_does_not_write(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = await DurableStateAsync(env, ep: 5, id: "id-5");

        var code = await env.Scripts.TransitAsync(state, StateExpectation.Holding(5, "id-5", 1), new StateTarget("S3", "fwd", 1, 6, "id-6", "S1:fwd:0:5:id-5", 9));

        code.Should().Be(MoveScripts.Written);
        var held = await env.Db.HashGetAllAsync(state);
        Field(held, "ep").Should().Be("6");
        Field(held, "id").Should().Be("id-6");
        Field(held, "dur").Should().Be("0");
        Field(held, "st").Should().Be("S3");
        Field(held, "nPop").Should().Be("1");
        Field(held, "prev").Should().Be("S1:fwd:0:5:id-5");
        Field(held, "caches").Should().Be("a");
        Field(held, "moveId").Should().BeNull();
    }

    [Theory]
    [InlineData(true, 4, "id-5", 1)]
    [InlineData(true, 5, "other", 1)]
    [InlineData(true, 5, "id-5", 0)]
    [InlineData(false, 4, "id-5", 1)]
    [InlineData(false, 5, "other", 1)]
    [InlineData(false, 5, "id-5", 0)]
    public async Task Transit_changes_nothing_when_the_stored_state_is_not_the_expected_one(bool useScripts, long ep, string id, int durable)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = await DurableStateAsync(env, ep: 5, id: "id-5");

        var code = await env.Scripts.TransitAsync(state, StateExpectation.Holding(ep, id, durable), new StateTarget("S3", "fwd", 1, 9, "id-9", "x:y:0:5:id-5", 9));

        code.Should().Be(MoveScripts.ExpectationFailed);
        (await env.Db.HashGetAsync(state, "id")).Should().Be("id-5");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transit_abandons_a_state_at_dur_zero_only_when_the_expectation_says_so(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = await DurableStateAsync(env, ep: 5, id: "id-5");
        await env.Scripts.TransitAsync(state, StateExpectation.Holding(5, "id-5", 1), new StateTarget("S1", "fwd", 0, 6, "id-6", "S0:none:0:5:id-5", 1));
        var back = new StateTarget("S0", "none", 0, 7, "id-7", "S0:none:0:5:id-5", 2);

        (await env.Scripts.TransitAsync(state, StateExpectation.Holding(6, "id-6", 1), back)).Should().Be(MoveScripts.ExpectationFailed);
        (await env.Scripts.TransitAsync(state, StateExpectation.Holding(6, "id-6", 0), back)).Should().Be(MoveScripts.Written);

        (await env.Db.HashGetAsync(state, "id")).Should().Be("id-7");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transit_of_the_stored_target_is_a_repeat_and_create_over_an_existing_hash_is_refused(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = await DurableStateAsync(env, ep: 5, id: "id-5");
        var target = new StateTarget("S1", "fwd", 0, 6, "id-6", "S0:none:0:5:id-5", 1);
        await env.Scripts.TransitAsync(state, StateExpectation.Holding(5, "id-5", 1), target);

        (await env.Scripts.TransitAsync(state, StateExpectation.Holding(5, "id-5", 1), target)).Should().Be(MoveScripts.Repeated);
        (await env.Scripts.TransitAsync(state, StateExpectation.Absent, target with { Ep = 7, Id = "id-7" })).Should().Be(MoveScripts.ExpectationFailed);

        (await env.Db.HashGetAsync(state, "id")).Should().Be("id-6");
    }

    [Fact]
    public async Task Transit_refuses_a_target_that_does_not_raise_the_epoch()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: true, openControl: false);

        var act = () => env.Scripts.TransitAsync(env.Make("state"), StateExpectation.Holding(5, "id-5", 1), new StateTarget("S1", "fwd", 0, 5, "id-6", "p", 1));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Durable_sets_dur_and_copies_the_state_into_prev_and_a_wrong_id_changes_nothing(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = env.Make("state");
        await env.Scripts.TransitAsync(state, StateExpectation.Absent, new StateTarget("S3", "fwd", 1, 6, "id-6", "S1:fwd:0:5:id-5", 1));

        (await env.Scripts.DurableAsync(state, "other")).Should().Be(0);
        (await env.Db.HashGetAsync(state, "dur")).Should().Be("0");
        (await env.Scripts.DurableAsync(state, "id-6", "tok:6:S3")).Should().Be(1);

        var held = await env.Db.HashGetAllAsync(state);
        Field(held, "dur").Should().Be("1");
        Field(held, "prev").Should().Be("S3:fwd:1:6:id-6");
        Field(held, "rel").Should().Be("tok:6:S3");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Allack_writes_the_acknowledged_id_only_while_it_is_the_stored_one(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = await DurableStateAsync(env, ep: 5, id: "id-5");

        (await env.Scripts.AllAckAsync(state, "old")).Should().Be(0);
        (await env.Db.HashGetAsync(state, "allAck")).IsNull.Should().BeTrue();
        (await env.Scripts.AllAckAsync(state, "id-5")).Should().Be(1);
        (await env.Scripts.AllAckAsync(state, "id-5")).Should().Be(1);

        (await env.Db.HashGetAsync(state, "allAck")).Should().Be("id-5");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Init_creates_a_durable_s0_state_above_the_clock_and_above_the_highest_record(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = env.Make("state");
        var micros = await env.ServerNowAsync() * 1000;

        var result = await env.Scripts.InitAsync(state, "a+b", "h", highestRecordEp: 0, updatedAt: 7);
        var above = await env.Scripts.InitAsync(env.Make("state2"), "a", "h", highestRecordEp: long.MaxValue / 2, updatedAt: 7);

        result.Code.Should().Be(MoveScripts.StateCreated);
        result.Ep.Should().BeGreaterThanOrEqualTo(micros);
        above.Ep.Should().Be((long.MaxValue / 2) + 1);
        var held = await env.Db.HashGetAllAsync(state);
        Field(held, "st").Should().Be("S0");
        Field(held, "dur").Should().Be("1");
        Field(held, "nPop").Should().Be("0");
        Field(held, "ep").Should().Be(result.Ep.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(held, "id").Should().Be(result.Id);
        Field(held, "prev").Should().Be($"S0:none:0:{result.Ep}:{result.Id}");
        Field(held, "caches").Should().Be("a+b");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Init_of_an_s0_state_refreshes_the_set_and_keeps_the_identity_and_later_stages_are_refused(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var state = env.Make("state");
        var first = await env.Scripts.InitAsync(state, "a", "h1", 0, 1);

        var again = await env.Scripts.InitAsync(state, "a+b", "h2", 0, 2);

        again.Code.Should().Be(MoveScripts.StateRefreshed);
        var held = await env.Db.HashGetAllAsync(state);
        Field(held, "id").Should().Be(first.Id);
        Field(held, "ep").Should().Be(first.Ep.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(held, "caches").Should().Be("a+b");
        Field(held, "cs").Should().Be("h2");

        await env.Scripts.TransitAsync(state, StateExpectation.Holding(first.Ep, first.Id, 1), new StateTarget("S1", "fwd", 0, first.Ep + 1, "id-x", "p", 3));
        (await env.Scripts.InitAsync(state, "z", "h3", 0, 4)).Code.Should().Be(MoveScripts.StateNotInitial);
        (await env.Db.HashGetAsync(state, "caches")).Should().Be("a+b");
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task One_aborted_exec_with_every_predicate_holding_is_sent_again_and_gives_the_script_code(string command)
    {
        await using var scriptEnv = await MoveTestEnvironment.CreateAsync(fixture, useScripts: true, openControl: false);
        var expected = await RunAsync(command, scriptEnv, scriptEnv.Scripts, await PrepareAsync(command, scriptEnv));
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false, openControl: false);
        var state = await PrepareAsync(command, env);
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, field: "zz", watched: state, absent: command is "transit-create" or "init");

        var code = await RunAsync(command, env, new MoveScripts(harness.Database, useScripts: false, env.ServerVersion), state);

        code.Should().Be(expected, command);
        code.Should().BePositive(command);
        harness.Aborted.Should().Be(1, command);
        harness.Executions.Should().BeGreaterThanOrEqualTo(2, command);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task An_exec_that_aborts_on_every_attempt_throws_after_the_attempt_limit(string command)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false, openControl: false);
        var state = await PrepareAsync(command, env);
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: int.MaxValue, field: "zz", watched: state, absent: command is "transit-create" or "init");

        var act = () => RunAsync(command, env, new MoveScripts(harness.Database, useScripts: false, env.ServerVersion), state);

        await act.Should().ThrowAsync<RedisException>();
        harness.Aborted.Should().Be(MoveScripts.MaxWatchAborts, command);
    }

    [Fact]
    public async Task A_fallback_that_finds_the_hash_of_another_type_raises_the_error_like_the_script()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false, openControl: false);
        var state = env.Make("state");
        await env.Db.StringSetAsync(state, "x");

        var act = () => env.Scripts.AllAckAsync(state, "id-5");

        await act.Should().ThrowAsync<RedisServerException>();
    }

    private static string? Field(HashEntry[] held, string name) => held.FirstOrDefault(e => e.Name == name).Value;

    private static async Task<RedisKey> DurableStateAsync(MoveTestEnvironment env, long ep, string id)
    {
        var state = env.Make("state");
        await env.Db.HashSetAsync(
            state,
            [new("st", "S1"), new("dir", "fwd"), new("nPop", "0"), new("ep", ep), new("id", id), new("dur", "1"), new("caches", "a"), new("prev", $"S1:fwd:0:{ep}:{id}")]);
        return state;
    }

    private static async Task<RedisKey> PrepareAsync(string command, MoveTestEnvironment env)
    {
        var state = env.Make("state");
        switch (command)
        {
            case "transit":
                return await DurableStateAsync(env, 5, "id-5");
            case "durable":
                await env.Scripts.TransitAsync(state, StateExpectation.Absent, new StateTarget("S3", "fwd", 1, 6, "id-6", "S1:fwd:0:5:id-5", 1));
                return state;
            case "allack":
                return await DurableStateAsync(env, 5, "id-5");
            case "init-refresh":
                await env.Scripts.InitAsync(state, "a", "h", 0, 1);
                return state;
            default:
                return state;
        }
    }

    private static async Task<long> RunAsync(string command, MoveTestEnvironment env, MoveScripts scripts, RedisKey state)
    {
        switch (command)
        {
            case "transit":
                return await scripts.TransitAsync(state, StateExpectation.Holding(5, "id-5", 1), new StateTarget("S3", "fwd", 1, 6, "id-6", "S1:fwd:0:5:id-5", 1));
            case "transit-create":
                return await scripts.TransitAsync(state, StateExpectation.Absent, new StateTarget("S3", "fwd", 1, 6, "id-6", "S1:fwd:0:5:id-5", 1));
            case "durable":
                return await scripts.DurableAsync(state, "id-6");
            case "allack":
                return await scripts.AllAckAsync(state, "id-5");
            case "init":
            case "init-refresh":
                return (await scripts.InitAsync(state, "a+b", "h", 0, 1)).Code;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }
}
