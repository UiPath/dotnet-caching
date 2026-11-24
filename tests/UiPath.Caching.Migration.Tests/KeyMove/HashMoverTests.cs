using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The mover's side of a hash cache: what each end of the hash promotion becomes for the runner, on the real scripts.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class HashMoverTests(RedisFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_hash_in_L_is_promoted_and_counts_as_moved()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        var target = new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion);

        var result = await target.MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.Moved.Should().BeTrue();
        (await kit.AllFieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        target.CacheId.Should().Be(kit.Kit.Cid);
        target.NoOrigin.Should().BeFalse();
    }

    [Fact]
    public async Task A_hash_that_is_gone_from_L_is_drained_and_not_counted_as_moved()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.Should().Be(MoveResult.Drain);
    }

    [Fact]
    public async Task A_hash_above_the_field_cap_is_left_out_as_too_large()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), noOrigin: true, maxFields: 2);
        await Seed(kit, "S3");

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be("too_large");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_hash_whose_key_cannot_be_mapped_is_left_out_as_unreadable()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), mappable: _ => false);
        await Seed(kit, "S3");

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be("unreadable");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_promotion_that_cannot_be_sent_is_left_out_as_unavailable()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        kit.Kit.Connected = false;

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be("unavailable");
        kit.Kit.Schedule.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_runner_that_holds_another_identity_moves_nothing()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, "older", AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be("identity");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        kit.Kit.Schedule.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_state_that_no_longer_promotes_ends_the_key_as_an_identity_change()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S1");

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be("identity");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_runner_that_lost_its_lease_leaves_the_key_uncounted_for_the_next_owner()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, new Lost(), null, Ct);

        result.SkipReason.Should().Be(MoveResult.OwnershipLost);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        kit.Kit.Schedule.Steps.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(SchedulePointStep.StageHash), false)]
    [InlineData(nameof(SchedulePointStep.Publish), false)]
    [InlineData(nameof(SchedulePointStep.CompareDelete), true)]
    public async Task Losing_ownership_before_a_hash_write_leaves_that_write_unsent(string step, bool published)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        var ownership = new Ownership();
        kit.Kit.Schedule.Hook = (reached, _) =>
        {
            ownership.HasLease &= reached.ToString() != step;
            return Task.CompletedTask;
        };

        var result = await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, ownership, null, Ct);

        result.SkipReason.Should().Be(MoveResult.OwnershipLost);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().Be(published);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        await using var staging = kit.Kit.Mux.GetServer(kit.Kit.Mux.GetEndPoints()[0]).KeysAsync(pattern: kit.N("k") + ":stg:*").GetAsyncEnumerator(Ct);
        (await staging.MoveNextAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Losing_ownership_or_cancelling_between_staging_chunks_deletes_the_partial_hash(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var ownership = new Ownership();
        var stagingStarted = false;
        RedisKey staging = default;
        long fieldsStaged = 0;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), interceptDb: db =>
            InterceptingProxy.Wrap(db, (method, _) =>
                stagingStarted && method.Name == nameof(IDatabase.CreateTransaction)
                    ? WrapStaging(db)
                    : InterceptingProxy.Proceed));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var fields = Enumerable.Range(0, 513).ToDictionary(i => $"field{i}", _ => "value");
        await kit.SetAsync(kit.L("k"), fields);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            stagingStarted = step == SchedulePointStep.StageHash;
            return Task.CompletedTask;
        };

        var moving = Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, ownership, null, cancellation.Token).AsTask();

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        }
        else
        {
            (await moving).SkipReason.Should().Be(MoveResult.OwnershipLost);
        }

        fieldsStaged.Should().Be(512);
        (await kit.Db.KeyExistsAsync(staging)).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(fields);

        ITransaction WrapStaging(IDatabase db)
        {
            var transaction = db.CreateTransaction();
            return InterceptingProxy.Wrap(transaction, (method, args) =>
            {
                if (method.Name == nameof(ITransaction.HashSetAsync))
                {
                    staging = (RedisKey)args[0]!;
                }

                return method.Name == nameof(ITransaction.ExecuteAsync)
                    ? ExecuteChunkAsync(db, transaction)
                    : InterceptingProxy.Proceed;
            });
        }

        async Task<bool> ExecuteChunkAsync(IDatabase db, ITransaction transaction)
        {
            var committed = await transaction.ExecuteAsync(CommandFlags.DemandMaster);
            fieldsStaged = await db.HashLengthAsync(staging, CommandFlags.DemandMaster);
            if (cancel)
            {
                await cancellation.CancelAsync();
            }
            else
            {
                ownership.HasLease = false;
            }

            return committed;
        }
    }

    [Fact]
    public async Task The_hash_publish_charges_the_bytes_of_the_stored_field_names_and_values()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["été"] = "猫" });
        var ownership = new Ownership();

        var result = await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, ownership, null, Ct);

        result.Bytes.Should().Be(8);
        ownership.Bytes.Should().Be(8);
    }

    [Theory]
    [InlineData(true, "oom")]
    [InlineData(false, "timeout")]
    public async Task A_hash_publish_reports_memory_refusals_and_timeouts_to_the_runner(bool memoryRefusal, string reason)
    {
        var publishing = false;
        Exception? failure = null;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), interceptDb: db => InterceptingProxy.Wrap(db, (method, _) =>
        {
            if (publishing && method.Name == nameof(IDatabase.ScriptEvaluateAsync) && failure is not null)
            {
                throw failure;
            }

            return InterceptingProxy.Proceed;
        }));
        await Seed(kit, "S3");
        failure = memoryRefusal
            ? await Record.ExceptionAsync(() => kit.Db.ScriptEvaluateAsync("return redis.error_reply('OOM command not allowed')"))
            : new TimeoutException();
        failure.Should().NotBeNull();
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            publishing = step == SchedulePointStep.Publish;
            return Task.CompletedTask;
        };

        var result = await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be(reason);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, "oom")]
    [InlineData(false, "timeout")]
    public async Task A_hash_staging_write_reports_thrown_and_transaction_refused_failures(bool memoryRefusal, string reason)
    {
        var staging = false;
        var refused = false;
        Exception failure = memoryRefusal
            ? new RedisServerException(RedisErrorKind.OutOfMemory, CommandFlags.DemandMaster, "OOM command not allowed")
            : new TimeoutException();
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), interceptDb: db => InterceptingProxy.Wrap(db, (method, _) =>
            staging && method.Name == nameof(IDatabase.CreateTransaction)
                ? InterceptingProxy.Wrap(db.CreateTransaction(), (command, _) => command.Name switch
                {
                    nameof(ITransaction.HashSetAsync) => FailWrite(),
                    nameof(ITransaction.KeyExpireAsync) => Task.FromResult(false),
                    nameof(ITransaction.ExecuteAsync) => Task.FromResult(false),
                    _ => InterceptingProxy.Proceed,
                })
                : InterceptingProxy.Proceed));
        await Seed(kit, "S3");
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            staging = step == SchedulePointStep.StageHash;
            return Task.CompletedTask;
        };

        var target = Target(kit);
        var thrown = await target.MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);
        refused = true;
        var rejected = await target.MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        thrown.SkipReason.Should().Be(reason);
        rejected.SkipReason.Should().Be(reason);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        Task FailWrite() => refused ? Task.FromException(failure) : throw failure;
    }

    [Theory]
    [InlineData(true, "oom")]
    [InlineData(false, "abandoned")]
    public async Task An_aborted_staging_transaction_checks_for_a_hidden_memory_refusal(bool memoryRefusal, string reason)
    {
        var staging = false;
        var checkedMemory = false;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), interceptDb: db =>
            InterceptingProxy.Wrap(db, (method, _) => !staging ? InterceptingProxy.Proceed : method.Name switch
            {
                nameof(IDatabase.CreateTransaction) => InterceptingProxy.Wrap(db.CreateTransaction(), (command, _) => command.Name switch
                {
                    nameof(ITransaction.HashSetAsync) => Task.CompletedTask,
                    nameof(ITransaction.KeyExpireAsync) => Task.FromResult(false),
                    nameof(ITransaction.ExecuteAsync) => Task.FromException<bool>(Failure(RedisErrorKind.ExecAbort, "EXECABORT Transaction discarded")),
                    _ => InterceptingProxy.Proceed,
                }),
                nameof(IDatabase.StringSetAsync) => CheckMemory(),
                _ => InterceptingProxy.Proceed,
            }));
        await Seed(kit, "S3");
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            staging = step == SchedulePointStep.StageHash;
            return Task.CompletedTask;
        };

        var result = await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be(reason);
        checkedMemory.Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        Task<bool> CheckMemory()
        {
            checkedMemory = true;
            return memoryRefusal
                ? Task.FromException<bool>(Failure(RedisErrorKind.OutOfMemory, "OOM command not allowed"))
                : Task.FromResult(false);
        }

        static RedisServerException Failure(RedisErrorKind kind, string message) => new(kind, CommandFlags.DemandMaster, message);
    }

    [Fact]
    public async Task A_newer_native_hash_drains_a_different_legacy_hash()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        var native = new Dictionary<string, string> { ["a"] = "newer" };
        await kit.SetAsync(kit.N("k"), native);

        var result = await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.Should().Be(MoveResult.Drain);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.AllFieldsAsync(kit.N("k"))).Should().BeEquivalentTo(native);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_legacy_hash_changed_after_its_snapshot_survives_the_duplicate_drain(bool nativeArrivesAtPublish)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        var native = new Dictionary<string, string> { ["a"] = "newer" };
        if (!nativeArrivesAtPublish)
        {
            await kit.SetAsync(kit.N("k"), native);
        }

        var changeAt = nativeArrivesAtPublish ? SchedulePointStep.Publish : SchedulePointStep.CompareDelete;
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == changeAt)
            {
                await kit.Db.HashSetAsync(kit.L("k"), "a", "changed");
                await kit.SetAsync(kit.N("k"), native);
            }
        };

        var request = new HashPromotion<Item>.MoveRequest(kit.Kit.Id, TimeSpan.Zero, AlwaysOwned.Instance);
        var result = await kit.Client.Promotion.PromoteForMoverAsync("k", request, Ct);

        result.Status.Should().Be(HashPromotionStatus.Changing);
        kit.Kit.Schedule.Steps.Should().Contain(SchedulePointStep.CompareDelete);
        (await kit.Db.HashGetAsync(kit.L("k"), "a")).Should().Be((RedisValue)"changed");
        (await kit.AllFieldsAsync(kit.N("k"))).Should().BeEquivalentTo(native);
    }

    [Fact]
    public async Task A_retry_drains_the_duplicate_after_the_first_compare_delete_loses()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        var refuse = true;
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.CompareDelete && refuse)
            {
                refuse = false;
                await kit.Db.HashSetAsync(kit.L("k"), "temporary", "value");
            }

            if (step == SchedulePointStep.Capture && !refuse)
            {
                await kit.Db.HashDeleteAsync(kit.L("k"), "temporary");
            }
        };

        var result = await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.Drained.Should().BeTrue();
        result.Bytes.Should().Be(6);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.AllFieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
    }

    [Fact]
    public async Task A_null_only_hash_is_retryable_when_the_compare_delete_loses()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), inPlaceNulls: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.Db.HashSetAsync(kit.L("k"), "first", string.Empty);
        var attempts = 0;
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.CompareDelete)
            {
                await kit.Db.HashSetAsync(kit.L("k"), $"late{++attempts}", string.Empty);
            }
        };

        var result = await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be("changing");
        (await kit.Db.HashLengthAsync(kit.L("k"))).Should().Be(4);
        kit.Kit.Schedule.Hook = null;
        (await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct)).Should().Be(MoveResult.Drain);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_hash_move_reports_the_source_ttl_and_its_request_time()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        var seen = new List<(long Ttl, long At)>();

        var result = await Target(kit).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, (ttl, at) => seen.Add((ttl, at)), Ct);

        result.Moved.Should().BeTrue();
        var observed = seen.Should().ContainSingle().Subject;
        observed.At.Should().Be(Start.ToUnixTimeMilliseconds());
        observed.Ttl.Should().BeInRange(1, (long)TimeSpan.FromMinutes(5).TotalMilliseconds);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_hash_with_an_origin_and_a_short_ttl_is_left_for_reads(int maxFields)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), maxFields: maxFields);
        await Seed(kit, "S3");

        var result = await Target(kit).MoveAsync("k", TimeSpan.FromMinutes(10), kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be("short_ttl");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task The_hash_dry_run_classifies_and_measures_keys_without_writing()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), inPlaceNulls: true, maxFields: 2);
        await kit.Kit.PrepareAsync("valid", "large", "junk", "empty", "duplicate");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("valid"), new Dictionary<string, string> { ["field"] = "value" }, TimeSpan.FromMinutes(5));
        await kit.SetAsync(kit.L("large"), Original);
        await kit.Db.HashSetAsync(kit.L("junk"), "field", "junk:value");
        await kit.Db.HashSetAsync(kit.L("empty"), "field", string.Empty);
        await kit.SetAsync(kit.L("duplicate"), new Dictionary<string, string> { ["field"] = "value" });
        await kit.SetAsync(kit.N("duplicate"), new Dictionary<string, string> { ["field"] = "value" });
        var target = Target(kit);

        (await target.ProbeAsync("valid", TimeSpan.Zero, true, Ct)).Should().Be(new DryRunProbe(DryRunClasses.Movable, 10, 10));
        (await target.ProbeAsync("valid", TimeSpan.Zero, false, Ct)).Should().Be(new DryRunProbe(DryRunClasses.Movable, 10, null));
        (await target.ProbeAsync("valid", TimeSpan.FromMinutes(10), true, Ct)).Should().Be(new DryRunProbe(DryRunClasses.ShortTtl, 10, null));
        (await target.ProbeAsync("large", TimeSpan.Zero, true, Ct)).Class.Should().Be(DryRunClasses.TooLarge);
        (await target.ProbeAsync("junk", TimeSpan.Zero, true, Ct)).Class.Should().Be(DryRunClasses.Unreadable);
        (await target.ProbeAsync("empty", TimeSpan.Zero, true, Ct)).Class.Should().Be(DryRunClasses.NullNotCached);
        (await target.ProbeAsync("duplicate", TimeSpan.Zero, true, Ct)).Class.Should().Be(DryRunClasses.Duplicate);
        (await target.ProbeAsync("absent", TimeSpan.Zero, true, Ct)).Class.Should().Be(DryRunClasses.Absent);
        kit.Kit.Schedule.Steps.Should().OnlyContain(step => step == SchedulePointStep.ReadSource || step == SchedulePointStep.ConfirmDestination);
        (await kit.Kit.ExistsAsync(kit.N("valid"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_mover_promotion_rejects_a_new_identity_even_when_the_new_mode_can_promote()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        var move = new HashPromotion<Item>.MoveRequest(kit.Kit.Id, TimeSpan.Zero, AlwaysOwned.Instance);
        await kit.Kit.SetStateAsync("S3");

        var result = await kit.Client.Promotion.PromoteForMoverAsync("k", move, Ct);

        result.Status.Should().Be(HashPromotionStatus.Replan);
        kit.Kit.Schedule.Steps.Should().BeEmpty();
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_hash_that_keeps_changing_under_the_promotion_is_tried_three_times_and_left_for_the_next_pass()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await Seed(kit, "S3");
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.CompareDelete)
            {
                await kit.Db.HashSetAsync(kit.L("k"), "late" + Guid.NewGuid().ToString("N")[..6], "x");
            }
        };

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.SkipReason.Should().Be("changing");
        kit.Kit.Schedule.Steps.Count(s => s == SchedulePointStep.Capture).Should().Be(3);
    }

    [Fact]
    public async Task A_live_marker_is_answered_by_the_handler_of_the_cache_and_the_older_hash_is_drained()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), noOrigin: true);
        await Seed(kit, "S3");
        await kit.Db.HashSetAsync(kit.Kit.Control("k"), kit.Kit.Field("k"), $"{kit.Kit.Id}|n0");

        var result = await new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.Should().Be(MoveResult.Drain);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("the helper of the client deleted the older copy");
        (await kit.Kit.MarkerAsync("k")).Should().BeNull();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_supersession_handler_of_the_promotion_decides_what_a_live_marker_does_to_the_key(bool unavailable)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start), noOrigin: true);
        await Seed(kit, "S3");
        await kit.Db.HashSetAsync(kit.Kit.Control("k"), kit.Kit.Field("k"), $"{kit.Kit.Id}|n0");
        var promotion = new HashPromotion<Item>(kit.Client.Parts, new HashReads<Item>(kit.Client.Parts), kit.Client.Hints, new Answer(unavailable ? SupersessionAction.Skip : SupersessionAction.Retry));

        var result = await new HashMover<Item>(kit.Client.Parts, promotion).MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        if (unavailable)
        {
            result.SkipReason.Should().Be("unavailable");
        }
        else
        {
            result.Should().Be(MoveResult.Drain);
        }
    }

    [Fact]
    public async Task A_target_needs_its_parts_and_its_promotion()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));

        ((Func<object>)(() => new HashMover<Item>(null!, kit.Client.Promotion))).Should().Throw<ArgumentNullException>();
        ((Func<object>)(() => new HashMover<Item>(kit.Client.Parts, null!))).Should().Throw<ArgumentNullException>();
    }

    private static HashMover<Item> Target(KeyMoveHashKit kit) => new(kit.Client.Parts, kit.Client.Promotion);

    private static async Task Seed(KeyMoveHashKit kit, string state)
    {
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state);
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(5));
    }

    private sealed class Ownership : IMoveOwnership
    {
        public bool HasLease { get; set; } = true;

        public bool StillHeld => true;

        public long Bytes { get; private set; }

        public ValueTask<bool> HoldsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(HasLease);

        public void Published(long bytes) => Bytes += bytes;
    }

    private sealed class Lost : IMoveOwnership
    {
        public bool StillHeld => false;

        public ValueTask<bool> HoldsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public void Published(long bytes)
        {
        }
    }

    private sealed class Answer(SupersessionAction action) : ISupersessionHandler
    {
        public Task<SupersessionAction> OnSupersededAsync(SupersededKey context, CancellationToken cancellationToken) => Task.FromResult(action);
    }
}
