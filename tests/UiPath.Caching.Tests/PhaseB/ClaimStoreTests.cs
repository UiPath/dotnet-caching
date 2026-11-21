using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ClaimStoreTests(RedisContainerFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.";
    private const string T1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Fmt = "fmt:1>2";

    private readonly RedisKey _key = $"claimtest:mx:{{{Guid.NewGuid():N}}}";
    private ConnectionMultiplexer? _mux;

    private IDatabase Db => _mux!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _mux = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mux is not null)
        {
            await Db.KeyDeleteAsync(_key);
            await _mux.DisposeAsync();
        }
    }

    [Fact]
    public void The_claim_key_is_the_library_prefix_mx_and_a_tag_of_the_lowercased_app()
    {
        new MoveKeyNames(string.Empty, "app:", cluster: true).Claim("App").Should().Be("app:mx:{app}");
        var act = () => new MoveKeyNames(string.Empty, "app:", cluster: true).Claim("my app");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Admission_parses_moves_and_the_pointer_and_flags_anything_malformed()
    {
        var record = ClaimRecord.From(
        [
            new("fm:g1", $"closed|{T1}|{Fmt}|9:S5"),
            new("rt:r", $"open|{T1}|rt|"),
            new("cur:fm", $"g1|{T1}|2|1"),
        ]);

        record.Malformed.Should().BeFalse();
        record.Pointer!.Group.Should().Be("g1");
        record.Live.Select(m => m.Key).Should().Equal("rt:r");
        ClaimRecord.From([new("rt:r", $"open|{T1}|rt|\n")]).Malformed.Should().BeTrue();
        ClaimRecord.From([new("zz:r", $"open|{T1}|rt|")]).Malformed.Should().BeTrue();
        ClaimRecord.From([new("cur:fm", "x")]).Malformed.Should().BeTrue();
    }

    [Fact]
    public async Task The_admission_read_comes_from_the_record()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, [new HashEntry("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), new HashEntry("cur:fm", $"g1|{T1}|2|1")]);

        var record = await Store().ReadAsync();

        record.Moves.Should().ContainKey("fm:g1");
        record.Pointer!.ToId.Should().Be("2");
    }

    [Fact]
    public async Task A_step_on_a_master_without_replicas_needs_no_wait()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(await ReplicasAsync() == 0, "The test server has replicas.");

        var result = await Store().OpenAsync("rt:a", T1, "rt", restore: true);

        result.Should().Be(new ClaimResult(1, ClaimDurability.NoReplica));
    }

    [Fact]
    public async Task A_step_on_a_master_with_a_replica_is_confirmed_by_wait()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(await ReplicasAsync() > 0, "The test server has no replica.");

        var store = Store();
        (await store.OpenAsync("rt:a", T1, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        (await store.OpenAsync("rt:a", T1, "rt", restore: true)).Should().Be(new ClaimResult(2, ClaimDurability.Confirmed), "the repeat rewrites the field, so its WAIT is not vacuous");
    }

    [Fact]
    public async Task A_fallback_whose_record_keeps_changing_gives_up_with_an_unknown_outcome()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= MoveScripts.FallbackMinimum, "The fallback needs Redis 6.0.9.");
        await Db.HashSetAsync(_key, "rt:z", $"idle|{T1}|rt|");
        var flip = 0;
        var churning = InterceptingProxy.Wrap(Db, (method, _) =>
        {
            if (method.Name == nameof(IDatabase.HashGetAllAsync))
            {
                // Another writer changes the record after every read, so every transaction aborts.
                var token = (Interlocked.Increment(ref flip) % 2 == 0 ? 'a' : 'b').ToString().PadRight(32, 'c');
                var read = Db.HashGetAllAsync(_key, CommandFlags.DemandMaster);
                return read.ContinueWith(
                    async done =>
                    {
                        await Db.HashSetAsync(_key, "rt:z", $"idle|{token}|rt|");
                        return done.Result;
                    },
                    TaskScheduler.Default).Unwrap();
            }

            return InterceptingProxy.Proceed;
        });
        var store = new ClaimStore(churning, _key, useScripts: false, fixture.ServerVersion);

        (await store.PendAsync("rt:a", T1, "rt", string.Empty)).Should().Be(ClaimCodes.FallbackExhausted);
        (await Db.HashExistsAsync(_key, "rt:a")).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseClaim_refuses_an_open_field_whose_group_shows_a_terminal_state()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var value = $"open|{T1}|{Fmt}|";
        await Db.HashSetAsync(_key, "fm:g1", value);
        var evidence = new FakeEvidence { ShowsTerminal = true };

        (await new ClaimOperator(Store(), evidence).ReleaseAsync("fm:g1", value, "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.NeedsCertify);
        (await Db.HashExistsAsync(_key, "fm:g1")).Should().BeTrue();

        evidence.ShowsTerminal = false;
        (await new ClaimOperator(Store(), evidence).ReleaseAsync("fm:g1", value, "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Done);
        (await Db.HashExistsAsync(_key, "fm:g1")).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_repeat_rewrites_the_field_so_its_wait_has_a_write_to_confirm(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var admin = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        var config = admin.GetServer(admin.GetEndPoints()[0]);
        var before = (await config.ConfigGetAsync("notify-keyspace-events")).Single().Value;
        await config.ConfigSetAsync("notify-keyspace-events", "Kh");
        await Db.HashSetAsync(_key, "rt:a", $"pend|{T1}|rt|");
        try
        {
            var events = 0;
            await admin.GetSubscriber().SubscribeAsync(RedisChannel.Literal($"__keyspace@0__:{_key}"), (_, message) =>
            {
                if (message == "hset")
                {
                    Interlocked.Increment(ref events);
                }
            });

            // The master answers ROLE with one replica and WAIT with 0, so every attempt repeats the step against the real server.
            var real = _mux!.GetServer(Db.IdentifyEndpoint(_key)!);
            var server = InterceptingProxy.Wrap(real, (method, _) => method.Name switch
            {
                nameof(IServer.RoleAsync) => Task.FromResult(FakeRoles.Master(1)),
                nameof(IServer.ExecuteAsync) => Task.FromResult(RedisResult.Create(0)),
                _ => InterceptingProxy.Proceed,
            });
            var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(_mux, (method, _) => method.Name == nameof(IConnectionMultiplexer.GetServer) ? server : InterceptingProxy.Proceed);
            var db = InterceptingProxy.Wrap(Db, (method, _) => method.Name == "get_Multiplexer" ? mux : InterceptingProxy.Proceed);

            var result = await new ClaimStore(db, _key, scripts, fixture.ServerVersion) { BarrierRetries = 2 }.OpenAsync("rt:a", T1, "rt", restore: false);

            result.Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
            var until = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref events) < 3 && DateTime.UtcNow < until)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
            Volatile.Read(ref events).Should().Be(3, "the step writes once and each of the 2 repeats rewrites the field");
        }
        finally
        {
            await config.ConfigSetAsync("notify-keyspace-events", before);
        }
    }

    [Fact]
    public async Task A_durable_step_for_a_user_denied_scripts_finds_the_cause_of_the_discarded_transaction_and_takes_the_bound_fallback()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var user = $"claimnoscript{Guid.NewGuid():N}"[..24];
        var acl = _mux!.GetServer(_mux.GetEndPoints()[0]);
        await acl.ExecuteAsync("ACL", "SETUSER", user, "on", ">claimpw", "~*", "&*", "+@all", "-@scripting");
        try
        {
            await using var denied = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},user={user},password=claimpw,allowAdmin=true");
            var store = new ClaimStore(denied.GetDatabase(), _key, useScripts: true, fixture.ServerVersion);

            var result = await store.OpenAsync("rt:a", T1, "rt", restore: true);

            result.Code.Should().Be(1);
            result.Durability.Should().BeOneOf([ClaimDurability.Confirmed, ClaimDurability.NoReplica], "the fallback's transaction is bound like the script's");
            store.UsesScripts.Should().BeFalse("the EXECABORT was traced to the refused script");
            (await Db.HashGetAsync(_key, "rt:a")).Should().Be($"open|{T1}|rt|");
        }
        finally
        {
            await acl.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    [Fact]
    public async Task ReleaseClaim_refuses_a_prv_pointer_while_its_group_has_a_move_field()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var previous = $"g0|{T1}|1|0";
        await Db.HashSetAsync(_key, [new("fm:g1", $"open|{T1}|{Fmt}|"), new("prv:g1", previous)]);
        var claim = new ClaimOperator(Store(), new FakeEvidence());

        (await claim.ReleaseAsync("prv:g1", previous, "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Refused, "the later idle of fm:g1 restores it");
        (await Db.HashGetAsync(_key, "prv:g1")).Should().Be(previous);

        await Db.HashDeleteAsync(_key, "fm:g1");
        (await claim.ReleaseAsync("prv:g1", previous, "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Done);
        (await Db.HashExistsAsync(_key, "prv:g1")).Should().BeFalse();
    }

    [Fact]
    public async Task CertifyClaim_closes_an_open_field_on_S5_evidence_and_sets_the_pointer()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, "fm:g1", $"open|{T1}|{Fmt}|");
        var claim = new ClaimOperator(Store(), new FakeEvidence { Terminal = new ClaimTerminal(42, Swept: true) });

        (await claim.CertifyAsync("fm:g1", $"open|{T1}|{Fmt}|", "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Done);

        ((string?)await Db.HashGetAsync(_key, "fm:g1")).Should().Be($"closed|{T1}|{Fmt}|42:S5");
        ((string?)await Db.HashGetAsync(_key, "cur:fm")).Should().Be($"g1|{T1}|2|1");
        (await claim.CertifyAsync("fm:g1", $"closed|{T1}|{Fmt}|42:S5", "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Refused, "a closed field needs no certificate");
    }

    [Fact]
    public async Task CertifyClaim_idles_on_S0_evidence_and_refuses_without_evidence()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, "rt:r", $"open|{T1}|rt|");

        var shown = $"open|{T1}|rt|";
        (await new ClaimOperator(Store(), new FakeEvidence()).CertifyAsync("rt:r", shown, "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.NoEvidence);
        var claim = new ClaimOperator(Store(), new FakeEvidence { Terminal = new ClaimTerminal(7, Swept: false) });
        (await claim.CertifyAsync("rt:r", shown, string.Empty, TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Refused, "the operator must confirm");
        (await claim.CertifyAsync("rt:r", $"pend|{T1}|rt|", "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Changed, "the value is no longer the one shown");
        (await claim.CertifyAsync("rt:r", shown, "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Done);

        ((string?)await Db.HashGetAsync(_key, "rt:r")).Should().Be($"idle|{T1}|rt|7:S0");
    }

    [Fact]
    public async Task RemoveGroup_drops_the_groups_fields_and_keeps_the_pointer()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, [new HashEntry("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), new HashEntry("prv:g1", "-"), new HashEntry("cur:fm", $"g1|{T1}|2|1"), new HashEntry("rt:other", $"open|{T1}|rt|")]);

        (await new ClaimOperator(Store(), new FakeEvidence()).RemoveGroupAsync("g1", "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.NotRemovable);
        (await new ClaimOperator(Store(), new FakeEvidence { Removable = true }).RemoveGroupAsync("g1", "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Done);

        (await Db.HashKeysAsync(_key)).Select(k => (string)k!).Should().BeEquivalentTo(["cur:fm", "rt:other"]);
    }

    [Fact]
    public async Task RemoveGroup_refuses_a_live_field()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, [new HashEntry("fm:g1", $"open|{T1}|{Fmt}|"), new HashEntry("prv:g1", "-")]);

        (await new ClaimOperator(Store(), new FakeEvidence { Removable = true }).RemoveGroupAsync("g1", "n", TestContext.Current.CancellationToken)).Should().Be(ClaimCommandOutcome.Live);

        (await Db.HashLengthAsync(_key)).Should().Be(2);
    }

    [Fact]
    public async Task RemoveGroup_stops_before_the_pointer_when_a_field_changed()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, [new HashEntry("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), new HashEntry("prv:g1", "-")]);
        var racing = InterceptingProxy.Wrap(Db, (method, args) =>
        {
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args.Length > 2 && args[2] is RedisValue[] values && values.Length > 0 && values[0] == "fm:g1")
            {
                // A REOPEN lands between the operator's read and the drop.
                Db.HashSet(_key, "fm:g1", $"open|{T1}|{Fmt}|");
            }

            return InterceptingProxy.Proceed;
        });

        var outcome = await new ClaimOperator(new ClaimStore(racing, _key, useScripts: true, fixture.ServerVersion), new FakeEvidence { Removable = true }).RemoveGroupAsync("g1", "n", TestContext.Current.CancellationToken);

        outcome.Should().Be(ClaimCommandOutcome.Changed);
        (await Db.HashExistsAsync(_key, "prv:g1")).Should().BeTrue("the pointer stays while its move is not removed");
    }

    private ClaimStore Store() => new(Db, _key, useScripts: true, fixture.ServerVersion);

    private async Task<int> ReplicasAsync()
    {
        var server = _mux!.GetServer(await Db.IdentifyEndpointAsync(_key) ?? throw new InvalidOperationException());
        return await server.RoleAsync() is Role.Master master ? master.Replicas.Count : 0;
    }

    private sealed class FakeEvidence : IClaimEvidence
    {
        public bool ShowsTerminal { get; set; }

        public ClaimTerminal? Terminal { get; set; }

        public bool Removable { get; set; }

        public Task<ClaimTerminal?> ReadTerminalAsync(string group, CancellationToken cancellationToken) => Task.FromResult(Terminal);

        public Task<bool> ShowsTerminalAsync(string group, CancellationToken cancellationToken) => Task.FromResult(ShowsTerminal);

        public Task<bool> RemovableAsync(string group, CancellationToken cancellationToken) => Task.FromResult(Removable);
    }
}
