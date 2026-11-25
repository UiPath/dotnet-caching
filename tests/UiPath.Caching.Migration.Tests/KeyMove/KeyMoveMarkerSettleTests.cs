using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The outcome of a marked write that was rerun, the permit of a logical write, and what the writer's later steps and retries leave behind.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveMarkerSettleTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rerun_whose_first_step_is_not_sent_after_an_attempt_applied_is_Unknown_and_not_NotSent()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(5) });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.SourceDelete && nth == 1)
            {
                await kit.MoveGateAsync();
                kit.ApplyRules("S3");
            }
            else if (step == SchedulePointStep.Bump && nth == 2)
            {
                kit.Connected = false;
            }
        };

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1", "the first attempt applied it");
    }

    [Fact]
    public async Task A_write_whose_reruns_are_exhausted_after_an_attempt_applied_is_Unknown()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { MaxReruns = 0 });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                await kit.MoveGateAsync();
            }
        };

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("replan")]
    [InlineData("refused")]
    public async Task A_rerun_that_the_state_no_longer_admits_after_an_attempt_applied_is_Unknown_and_never_throws(string path)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { MaxReruns = 1, AdmissionPoll = TimeSpan.Zero });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step != SchedulePointStep.SourceDelete || nth != 1)
            {
                return;
            }

            switch (path)
            {
                case "closed":
                    await kit.SetStateAsync("S2", nPop: 0);
                    break;
                case "replan":
                    await kit.MoveGateAsync();
                    break;
                default:
                    await kit.SetStateAsync("S3");
                    kit.Plane.RefuseEntry = true;
                    break;
            }
        };

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
    }

    [Fact]
    public async Task A_logical_write_holds_one_permit_from_before_its_first_step_until_it_returns_and_across_a_rerun()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(5) });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var held = new List<(SchedulePointStep Step, int Permits)>();
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step is SchedulePointStep.Bump or SchedulePointStep.SourceDelete)
            {
                held.Add((step, kit.Client.Permits.InFlight));
            }

            if (step == SchedulePointStep.SourceDelete && nth == 1)
            {
                await kit.MoveGateAsync();
                kit.ApplyRules("S3");
            }
        };

        await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        held.Should().Equal((SchedulePointStep.Bump, 1), (SchedulePointStep.SourceDelete, 1), (SchedulePointStep.Bump, 1), (SchedulePointStep.SourceDelete, 1));
        kit.Client.Permits.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task The_permits_bound_the_logical_writes_a_node_has_outstanding()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { NoOriginWriteInflightMax = 1 });
        await kit.PrepareAsync("a", "b");
        await kit.SetStateAsync("S3");
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Client.Permits.Blocked = () => blocked.TrySetResult();
        Task<WriteResult>? second = null;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete && second is null)
            {
                second = kit.Client.SetAsync("b", new Item("v2"), null, Ct).AsTask();
                var signal = await Task.WhenAny(blocked.Task, second).WaitAsync(Ct);
                signal.Should().BeSameAs(blocked.Task, "the second write must wait for a permit");
                second.IsCompleted.Should().BeFalse("the first write still holds the only permit");
            }
        };

        var first = await kit.Client.SetAsync("a", new Item("v1"), null, Ct);
        var other = await second!.WaitAsync(Ct);

        first.Outcome.Should().Be(WriteOutcome.Committed);
        other.Outcome.Should().Be(WriteOutcome.Committed);
    }

    [Fact]
    public async Task The_caches_of_a_node_share_one_queue_and_one_set_of_permits()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        using var other = new KeyMoveClient<Item>(new KeyMoveClientParts<Item>(kit.Group, "other-cache", true, kit.L, kit.N, ItemCodec.Instance, ItemCodec.Instance, kit.Plain, kit.Plane, kit.Clock, () => TimeSpan.FromSeconds(30)) { RunRetriesInBackground = false });

        other.Unmarks.Should().BeSameAs(kit.Client.Unmarks);
        other.Permits.Should().BeSameAs(kit.Client.Permits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_marked_set_with_no_reply_keeps_its_permit_until_it_cannot_apply(bool scripts)
    {
        var clock = new ObservedClock(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true, clock: clock, options: new KeyMoveOptions { NoOriginWriteInflightMax = 1 });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        kit.Plane.Data = Faulty(kit, scripts);

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Client.Permits.InFlight.Should().Be(1);
        var free = kit.Client.Permits.WaitAsync(Ct);
        await clock.NextTimerAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(5));
        var nextCheck = await clock.NextTimerAsync(Ct);
        kit.Client.Permits.InFlight.Should().Be(1, "inside the deadline and the clock bound, or for a transaction with no deadline until its connection changes");
        clock.Advance(nextCheck + TimeSpan.FromSeconds(2));
        if (!scripts)
        {
            nextCheck = await clock.NextTimerAsync(Ct);
            kit.Client.Permits.InFlight.Should().Be(1, "a transaction has no deadline");
            kit.ConnectionEvent();
            clock.Advance(nextCheck);
        }

        await free.WaitAsync(Ct);
        kit.Client.Permits.Release();
        kit.Client.Permits.InFlight.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retry_of_the_second_step_queues_the_unmark_when_it_succeeds(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.SetAsync("k", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Schedule.Hook = null;
        kit.Connected = true;
        kit.Client.Unmarks.Count.Should().Be(0, "L has not been deleted yet");

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        kit.Client.Unmarks.Count.Should().Be(1);
        await kit.Client.Unmarks.RunDueAsync(all: true);
        (await kit.MarkerAsync("k")).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_retry_of_a_NoOrigin_delete_removes_the_marker_too(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Client.SetAsync("k", new Item("v1"), null, Ct);
        (await kit.MarkerAsync("k")).Should().NotBeNull();
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Schedule.Hook = null;
        kit.Connected = true;

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.MarkerAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task A_key_whose_marker_field_would_be_too_long_fails_closed_before_any_step()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { MarkerKeyMax = 8 });
        using var meter = new MeterCollector(kit.Cid);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        kit.Schedule.Steps.Should().BeEmpty();
        meter.Count("cache.keymove.marker_key_too_long").Should().Be(1);
        kit.Client.Permits.InFlight.Should().Be(0);
    }

    private static KeyMoveData Faulty(KeyMoveKit kit, bool scripts)
    {
        var failure = new TimeoutException("the connection broke");
        var db = InterceptingProxy.Wrap(kit.Db, (m, _) =>
        {
            if (scripts && m.Name == nameof(IDatabase.ScriptEvaluateAsync))
            {
                throw failure;
            }

            if (!scripts && m.Name == nameof(IDatabase.CreateTransaction))
            {
                var inner = kit.Db.CreateTransaction();
                return InterceptingProxy.Wrap(inner, (tm, _) => tm.Name == nameof(ITransaction.ExecuteAsync) ? throw failure : InterceptingProxy.Proceed);
            }

            return InterceptingProxy.Proceed;
        });
        var version = kit.Mux.GetServers().Where(s => !s.IsReplica).Select(s => s.Version).Min() ?? new Version(7, 0);
        return new KeyMoveData(db, new MoveScripts(db, scripts, version), kit.Names, () => kit.Events, () => true);
    }
}
