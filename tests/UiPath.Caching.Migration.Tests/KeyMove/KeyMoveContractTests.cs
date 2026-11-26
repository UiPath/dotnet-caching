using System.Reflection;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// While a group's keys move, a fill never overwrites, and the remaining life is asked for alone, on the side the reads use.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveContractTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fill_while_writes_are_plain_and_reads_are_the_union_is_refused_and_writes_nothing_whether_the_key_is_there_or_not()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S0");
        kit.Plane.Rules = kit.Plane.Rules with { Reads = ReadRule.UnionNativeFirst };

        // The plain fill would land on the in-place key, which the reads do not prefer and a move can have emptied meanwhile.
        (await kit.Client.TryFillAsync("k", new Item("fill"), null, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        await kit.Db.StringSetAsync(kit.N("k"), "n-value", TimeSpan.FromMinutes(5));
        (await kit.Client.TryFillAsync("k", new Item("fill"), null, Ct)).Outcome.Should().Be(FillOutcome.Failed);

        kit.Plain.Fills.Should().Be(0);
        kit.Plain.Sets.Should().Be(0);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_fill_that_waited_out_a_closed_write_window_into_plain_writes_never_overwrites_a_stored_value_or_reports_added()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(10) });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var moving = kit.Plane.Rules;
        kit.Plane.Rules = moving with { Writes = WriteRule.WaitThenFailClosed, WriteWait = TimeSpan.FromSeconds(5) };
        kit.Plain.Held = new Item("winner");

        var fill = kit.Client.TryFillAsync("k", new Item("mine"), null, Ct).AsTask();
        var admission = await kit.Time.NextTimerWhileRunningAsync(fill, Ct);
        kit.ApplyRules("S0");
        kit.Time.Advance(admission);
        var result = await fill;

        result.Outcome.Should().Be(FillOutcome.Existed);
        result.Existing.Should().Be(new Item("winner"));
        kit.Plain.Sets.Should().Be(0);
    }

    [Fact]
    public async Task A_fill_that_loses_to_bytes_it_cannot_read_reports_unreadable_in_a_managed_state()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.N("k"), "junk:1");

        var result = await kit.Client.TryFillAsync("k", new Item("fill"), null, Ct);

        result.Outcome.Should().Be(FillOutcome.Unreadable);
        (await kit.GetAsync(kit.N("k"))).Should().Be("junk:1");
    }

    [Fact]
    public async Task The_remaining_life_while_keys_move_is_an_expiry_only_read_that_keeps_an_unreadable_value_and_promotes_nothing()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k", "n");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "junk:1", TimeSpan.FromMinutes(10));
        await kit.Db.StringSetAsync(kit.N("n"), "v", TimeSpan.FromMinutes(7));

        var left = await kit.Client.TimeToLiveAsync("k", Ct);
        var native = await kit.Client.ExpireTimeAsync("n", Ct);

        left!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        native.Should().NotBeNull();
        (await kit.GetAsync(kit.L("k"))).Should().Be("junk:1");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse("nothing was promoted");
        (await kit.Client.TimeToLiveAsync("absent", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task The_remaining_life_follows_the_sides_the_read_plan_reads()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");

        // S5 reads only N: a life on L is not its answer.
        await kit.SetStateAsync("S5");
        await kit.Db.StringSetAsync(kit.L("k"), "v", TimeSpan.FromMinutes(10));
        (await kit.Client.ExpireTimeAsync("k", Ct)).Should().BeNull();
        await kit.Db.StringSetAsync(kit.N("k"), "v", TimeSpan.FromMinutes(3));
        (await kit.Client.TimeToLiveAsync("k", Ct))!.Value.Should().BeLessThan(TimeSpan.FromMinutes(4));

        // S1 with nothing promoted reads only L: a life on N is not its answer.
        await kit.SetStateAsync("S1", nPop: 0);
        await kit.Db.KeyDeleteAsync(kit.L("k"));
        (await kit.Client.ExpireTimeAsync("k", Ct)).Should().BeNull();
        await kit.Db.StringSetAsync(kit.L("k"), "v", TimeSpan.FromMinutes(9));
        (await kit.Client.TimeToLiveAsync("k", Ct))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(8));

        // S3 reads the union, N first.
        await kit.SetStateAsync("S3");
        (await kit.Client.TimeToLiveAsync("k", Ct))!.Value.Should().BeLessThan(TimeSpan.FromMinutes(4), "N outranks L");
    }

    [Fact]
    public async Task Unmanaged_operations_delegate_to_the_plain_client_with_its_values_and_expiry()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        var span = TimeSpan.FromMinutes(4);
        var deadline = kit.Clock.GetUtcNow() + span;
        kit.Plain.Expiration = deadline;
        kit.Plain.RemainingLife = span;
        kit.Plain.Held = new Item("previous");
        var (cache, _) = ScriptedCache.Create();
        kit.Plain.PlainBatch = new LibraryBatch<Item>(cache, key => key, null, null);
        IPathClient<Item> client = kit.Client;
        var value = new Item("replacement");

        client.PlainBatch.Should().BeSameAs(kit.Plain.PlainBatch);
        (await client.TryCreateAsync("k", value, deadline, Ct)).Outcome.Should().Be(AddOutcome.Added);
        (await client.TryAddAsync("k", null, deadline, Ct)).Kind.Should().Be(TryAddKind.Created);
        kit.Plain.TryAdds.Should().Be(1);
        (await client.TryFillAsync("k", null, deadline, Ct)).Expiration.Should().Be(deadline);
        kit.Plain.Fills.Should().Be(1);
        (await client.TimeToLiveAsync("k", Ct)).Should().Be(span);
        (await client.ExpireTimeAsync("k", Ct)).Should().Be(deadline);
        (await client.ContainsAsync("k", Ct)).Should().BeNull();
        var swap = await client.SetIfChangedAsync("k", value, EqualityComparer<Item>.Default, deadline, Ct);
        swap.Write.Outcome.Should().Be(WriteOutcome.Committed);
        swap.Change.Outcome.Should().Be(ChangeOutcome.Replaced);
        swap.Change.Expiration.Should().Be(deadline);
        kit.Plain.Held.Should().Be(value);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_managed_fill_stores_with_its_deadline_and_rejects_null_without_writing()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("fill", "null");
        await kit.SetStateAsync("S3");
        IPathClient<Item> client = kit.Client;
        var deadline = kit.Clock.GetUtcNow().AddMinutes(4);

        client.PlainBatch.Should().BeNull();
        (await client.ContainsAsync("fill", Ct)).Should().BeNull();
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await client.SetIfChangedAsync("fill", new Item("wrong"), EqualityComparer<Item>.Default, deadline, Ct));
        (await client.TryAddAsync("null", null, deadline, Ct)).Failure.Outcome.Should().Be(WriteOutcome.NotSent);
        (await client.TryFillAsync("null", null, deadline, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await client.TryFillAsync("null", new Item("expired"), kit.Clock.GetUtcNow().AddMinutes(-1), Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await kit.ExistsAsync(kit.N("null"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.L("null"))).Should().BeFalse();

        var result = await client.TryFillAsync("fill", new Item("value"), deadline, Ct);
        result.Outcome.Should().Be(FillOutcome.Added);
        result.Expiration.Should().Be(deadline);
        (await kit.GetAsync(kit.N("fill"))).Should().Be("value");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("fill"))).Should().BeCloseTo(TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(5));
        (await kit.ExistsAsync(kit.L("fill"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_persistent_native_key_outranks_a_timed_source_key_and_a_failed_expiry_read_reports_unavailability()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.N("k"), "persistent");
        await kit.Db.StringSetAsync(kit.L("k"), "timed", TimeSpan.FromMinutes(2));

        (await kit.Client.ExpireTimeAsync("k", Ct)).Should().BeNull();
        (await kit.Client.TimeToLiveAsync("k", Ct)).Should().BeNull();
        (await kit.GetAsync(kit.N("k"))).Should().Be("persistent");

        kit.Connected = false;
        await Assert.ThrowsAsync<CacheUnavailableException>(async () => await kit.Client.ExpireTimeAsync("k", Ct));
        kit.Connected = true;
        var error = new TimeoutException("The expiry reply was lost.");
        var db = DispatchProxy.Create<IDatabase, ScriptedCache>();
        ((ScriptedCache)(object)db).Answers["KeyTimeToLiveAsync"] = _ => throw error;
        kit.Plane.Data = (kit.Plane.Data ?? throw new InvalidOperationException("The test database is required.")) with { Db = db };

        var failure = await Assert.ThrowsAsync<CacheUnavailableException>(async () => await kit.Client.ExpireTimeAsync("k", Ct));
        failure.InnerException.Should().BeSameAs(error);
        (await kit.GetAsync(kit.L("k"))).Should().Be("timed");
    }
}
