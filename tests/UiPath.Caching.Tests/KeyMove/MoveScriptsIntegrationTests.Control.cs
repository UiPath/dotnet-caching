using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public partial class MoveScriptsIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_creates_the_record_with_every_field(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);
        var record = new ControlRecord(7, "id-7", "S2", "fwd", "np1", "S1", "rev", "np0", "6", "id-6", [("c1", "n1"), ("c2", "n2")]);

        (await env.Scripts.ControlSyncAsync(env.Ctl, record)).Should().Be(MoveScripts.Written);

        (await ReadAsync(env)).Should().Equal(new Dictionary<string, string>
        {
            ["ep"] = "7",
            ["id"] = "id-7",
            ["st"] = "S2",
            ["dir"] = "fwd",
            ["np"] = "np1",
            ["dur"] = "0",
            ["pst"] = "S1",
            ["pdir"] = "rev",
            ["pnp"] = "np0",
            ["pep"] = "6",
            ["pid"] = "id-6",
            ["cl"] = "c1+c2",
            ["c:c1"] = "OPEN:n1",
            ["c:c2"] = "OPEN:n2",
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_with_a_higher_epoch_replaces_the_gates_as_a_set_and_clears_the_commit(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);
        await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(1, "id1", ("c1", "n1"), ("c2", "n2")));
        await env.Scripts.ControlCommitAsync(env.Ctl, 1, "id1");

        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(2, "id2", ("c2", "m2"), ("c3", "m3")))).Should().Be(1);

        var fields = await ReadAsync(env);
        fields["ep"].Should().Be("2");
        fields["id"].Should().Be("id2");
        fields["dur"].Should().Be("0");
        fields.Should().NotContainKey("c:c1");
        fields["c:c2"].Should().Be("OPEN:m2");
        fields["c:c3"].Should().Be("OPEN:m3");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_with_a_lower_epoch_returns_0_and_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);
        await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(5, "id5", ("c1", "n1")));
        var before = await ReadAsync(env);

        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(4, "id4", ("c9", "x")))).Should().Be(MoveScripts.AlreadyWrittenOrOlder);

        (await ReadAsync(env)).Should().Equal(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_with_the_same_epoch_and_id_returns_0_and_keeps_the_gates(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);
        await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(5, "id5", ("c1", "n1")));
        var before = await ReadAsync(env);

        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(5, "id5", ("c1", "other"), ("c2", "x")))).Should().Be(0);

        (await ReadAsync(env)).Should().Equal(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_with_the_same_epoch_and_another_id_conflicts(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);
        await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(5, "id5", ("c1", "n1")));
        var before = await ReadAsync(env);

        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(5, "id5b", ("c1", "n1")))).Should().Be(MoveScripts.IdentityConflict);

        (await ReadAsync(env)).Should().Equal(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_rejects_a_gate_name_with_a_reserved_character(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);

        var act = async () => await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(1, "id1", ("a+b", "n")));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlcommit_marks_the_record_committed_and_repeats(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.ControlCommitAsync(env.Ctl, 1, MoveTestEnvironment.Id)).Should().Be(1);
        (await env.Db.HashGetAsync(env.Ctl, "dur")).Should().Be("1");
        (await env.Scripts.ControlCommitAsync(env.Ctl, 1, MoveTestEnvironment.Id)).Should().Be(1);
        (await env.Db.HashGetAsync(env.Ctl, "dur")).Should().Be("1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlcommit_of_another_epoch_returns_0(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.ControlCommitAsync(env.Ctl, 2, "id2")).Should().Be(0);
        (await env.Scripts.ControlCommitAsync(env.Ctl, 0, MoveTestEnvironment.Id)).Should().Be(0);

        (await env.Db.HashGetAsync(env.Ctl, "dur")).Should().Be("0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlcommit_of_the_same_epoch_under_another_id_conflicts(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.ControlCommitAsync(env.Ctl, 1, "other")).Should().Be(MoveScripts.IdentityConflict);

        (await env.Db.HashGetAsync(env.Ctl, "dur")).Should().Be("0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlcommit_of_a_missing_record_returns_0(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);

        (await env.Scripts.ControlCommitAsync(env.Ctl, 1, MoveTestEnvironment.Id)).Should().Be(0);

        (await env.Db.KeyExistsAsync(env.Ctl)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gate_sets_the_value_and_a_repeat_of_it_returns_1(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x")).Should().Be(1);
        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x")).Should().Be(1);

        (await env.Db.HashGetAsync(env.Ctl, "c:c1")).Should().Be("CLOSED:x");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gate_with_an_expectation_sets_only_while_it_holds(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x")).Should().Be(1);

        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "OPEN:y", expect: "CLOSED:other")).Should().Be(MoveScripts.ExpectationFailed);
        (await env.Db.HashGetAsync(env.Ctl, "c:c1")).Should().Be("CLOSED:x");
        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "OPEN:y", expect: "CLOSED:x")).Should().Be(1);
        (await env.Db.HashGetAsync(env.Ctl, "c:c1")).Should().Be("OPEN:y");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gate_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.GateAsync(env.Ctl, "other", MoveTestEnvironment.Cid, "CLOSED:x")).Should().Be(-1);

        (await env.Db.HashGetAsync(env.Ctl, "c:c1")).Should().Be(env.Gate);
    }

    private static async Task<Dictionary<string, string>> ReadAsync(MoveTestEnvironment env) =>
        (await env.Db.HashGetAllAsync(env.Ctl)).ToDictionary(e => (string)e.Name!, e => (string)e.Value!);
}
