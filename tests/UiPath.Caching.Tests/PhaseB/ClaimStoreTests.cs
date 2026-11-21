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
            _mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
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

        (await new ClaimOperator(Store(), evidence).ReleaseAsync("fm:g1", value, "n", default)).Should().Be(ClaimCommandOutcome.NeedsCertify);
        (await Db.HashExistsAsync(_key, "fm:g1")).Should().BeTrue();

        evidence.ShowsTerminal = false;
        (await new ClaimOperator(Store(), evidence).ReleaseAsync("fm:g1", value, "n", default)).Should().Be(ClaimCommandOutcome.Done);
        (await Db.HashExistsAsync(_key, "fm:g1")).Should().BeFalse();
    }

    [Fact]
    public async Task CertifyClaim_closes_an_open_field_on_S5_evidence_and_sets_the_pointer()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, "fm:g1", $"open|{T1}|{Fmt}|");
        var claim = new ClaimOperator(Store(), new FakeEvidence { Terminal = new ClaimTerminal(42, Swept: true) });

        (await claim.CertifyAsync("fm:g1", default)).Should().Be(ClaimCommandOutcome.Done);

        ((string?)await Db.HashGetAsync(_key, "fm:g1")).Should().Be($"closed|{T1}|{Fmt}|42:S5");
        ((string?)await Db.HashGetAsync(_key, "cur:fm")).Should().Be($"g1|{T1}|2|1");
        (await claim.CertifyAsync("fm:g1", default)).Should().Be(ClaimCommandOutcome.Refused, "a closed field needs no certificate");
    }

    [Fact]
    public async Task CertifyClaim_idles_on_S0_evidence_and_refuses_without_evidence()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, "rt:r", $"open|{T1}|rt|");

        (await new ClaimOperator(Store(), new FakeEvidence()).CertifyAsync("rt:r", default)).Should().Be(ClaimCommandOutcome.NoEvidence);
        (await new ClaimOperator(Store(), new FakeEvidence { Terminal = new ClaimTerminal(7, Swept: false) }).CertifyAsync("rt:r", default)).Should().Be(ClaimCommandOutcome.Done);

        ((string?)await Db.HashGetAsync(_key, "rt:r")).Should().Be($"idle|{T1}|rt|7:S0");
    }

    [Fact]
    public async Task RemoveGroup_drops_the_groups_fields_and_keeps_the_pointer()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await Db.HashSetAsync(_key, [new HashEntry("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), new HashEntry("prv:g1", "-"), new HashEntry("cur:fm", $"g1|{T1}|2|1"), new HashEntry("rt:other", $"open|{T1}|rt|")]);

        (await new ClaimOperator(Store(), new FakeEvidence()).RemoveGroupAsync("g1", "n")).Should().Be(ClaimCommandOutcome.Done);

        (await Db.HashKeysAsync(_key)).Select(k => (string)k!).Should().BeEquivalentTo(["cur:fm", "rt:other"]);
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

        public Task<ClaimTerminal?> ReadTerminalAsync(string group, CancellationToken cancellationToken) => Task.FromResult(Terminal);

        public Task<bool> ShowsTerminalAsync(string group, CancellationToken cancellationToken) => Task.FromResult(ShowsTerminal);
    }
}
