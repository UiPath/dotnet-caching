using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The batch form of the server barrier over a fake database, transaction and server: the single form's rules, for several transactions and one WAIT.</summary>
public sealed class ServerBarrierManyTests
{
    private static readonly EndPoint A = new DnsEndPoint("a", 6379);

    [Fact]
    public async Task One_wait_confirms_all_transactions_each_beginning_with_the_client_id_and_info()
    {
        var fake = new Fake { Batch = 3 };

        (await fake.Barrier().WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Waits.Should().Be(1);
        fake.Transactions.Should().Equal(["CLIENT", "INFO", "SET", "EXEC", "CLIENT", "INFO", "SET", "EXEC", "CLIENT", "INFO", "SET", "EXEC"]);
        fake.InfoArguments.Should().Equal(0, 0, 0);
        fake.ExecFlags.Should().HaveCount(3).And.OnlyContain(f => f.HasFlag(CommandFlags.NoRedirect) && f.HasFlag(CommandFlags.DemandMaster));
    }

    [Fact]
    public async Task A_transaction_that_ran_on_another_connection_than_the_first_is_not_confirmed_and_all_are_sent_again()
    {
        var fake = new Fake { Batch = 3, ClientIdOf = (attempt, index) => attempt == 0 && index == 2 ? 9 : 7 };

        (await fake.Barrier().WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Attempts.Should().Be(2);
        fake.Waits.Should().Be(1, "the first attempt never reached its WAIT");
    }

    [Fact]
    public async Task A_batch_whose_transactions_never_share_a_connection_is_not_confirmed()
    {
        var fake = new Fake { ClientIdOf = (attempt, index) => 100 + (attempt * 10) + index };

        (await fake.Barrier(retries: 2).WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Attempts.Should().Be(3);
        fake.Waits.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_transaction_with_the_same_client_id_but_another_run_id_is_not_confirmed_and_all_are_sent_again(int odd)
    {
        var fake = new Fake { Batch = 3, RunIdOf = (attempt, index) => attempt == 0 && index == odd ? "other-server" : Fake.Run };

        (await fake.Barrier().WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Attempts.Should().Be(2, "client ids coincide across servers, so the whole proof of every transaction is compared");
        fake.Waits.Should().Be(1, "the first attempt never reached its WAIT");
    }

    [Fact]
    public async Task A_batch_split_by_master_waits_once_per_master_on_its_own_server_and_confirms_only_when_every_part_is()
    {
        var other = new DnsEndPoint("b", 6379);
        var fake = new Fake { Batch = 2, EndpointOf = key => key.ToString().Contains(":b", StringComparison.Ordinal) ? other : A };

        (await fake.Barrier().WriteManyByKeyAndWaitAsync(Fake.Keyed("a0", "b0", "a1", "b1"), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Waits.Should().Be(2);
        fake.WaitEndpoints.Should().BeEquivalentTo<EndPoint>([A, other]);
        fake.Attempts.Should().Be(2, "one batch of two transactions per master");

        var failing = new Fake { Batch = 2, EndpointOf = key => key.ToString().Contains(":b", StringComparison.Ordinal) ? other : A, Wait = call => Task.FromResult(call == 0 ? 1L : 0L) };

        (await failing.Barrier(retries: 1).WriteManyByKeyAndWaitAsync(Fake.Keyed("a0", "b0", "a1", "b1"), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
    }

    [Fact]
    public async Task A_keyed_batch_in_which_one_master_committed_and_another_committed_nothing_is_a_partial_application()
    {
        var other = new DnsEndPoint("b", 6379);

        // Master A's part is attempt 0 and commits; master B's conditions fail on every attempt, so B commits nothing.
        var fake = new Fake { Batch = 2, EndpointOf = key => key.ToString().Contains(":b", StringComparison.Ordinal) ? other : A, Committed = (attempt, _) => attempt == 0 };
        var barrier = fake.Barrier(retries: 1);

        (await barrier.WriteManyByKeyAndWaitAsync(Fake.Keyed("a0", "b0", "a1", "b1"), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.WaitEndpoints.Should().BeEquivalentTo<EndPoint>([A], "only A committed, so only A waits");
        barrier.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("keyed batch was partly applied").And.Contain("1 of 2 masters");

        // Neither master committed: nothing applied, so no partial application is reported.
        var none = new Fake { Batch = 2, EndpointOf = key => key.ToString().Contains(":b", StringComparison.Ordinal) ? other : A, Committed = (_, _) => false };
        var untouched = none.Barrier(retries: 1);

        (await untouched.WriteManyByKeyAndWaitAsync(Fake.Keyed("a0", "b0", "a1", "b1"), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        untouched.LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_keyed_batch_whose_later_master_throws_after_an_earlier_one_committed_is_a_partial_application(bool admin)
    {
        var other = new DnsEndPoint("b", 6379);
        var fake = new Fake { Admin = admin, Batch = 1, EndpointOf = key => key.ToString().Contains(":b", StringComparison.Ordinal) ? other : A };
        var barrier = fake.Barrier(replicaFree: true);

        // Master A's part commits; master B's queue callback is not a transient error: it is not hidden, but A's commit is recorded first.
        (await barrier.WriteManyByKeyAndWaitAsync(KeyedThrowingAtB(new InvalidOperationException("bug")), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Applied.Should().Be(1);
        barrier.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("keyed batch was partly applied").And.Contain("1 of 2 masters");

        // Cancellation surfaces as cancellation, after the partial application is recorded.
        var cancelled = new Fake { Admin = admin, Batch = 1, EndpointOf = key => key.ToString().Contains(":b", StringComparison.Ordinal) ? other : A };
        var cancelling = cancelled.Barrier(replicaFree: true);

        await cancelling.Invoking(b => b.WriteManyByKeyAndWaitAsync(KeyedThrowingAtB(new OperationCanceledException()), wayBack: false)).Should().ThrowAsync<OperationCanceledException>();

        cancelled.Applied.Should().Be(1);
        cancelling.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("keyed batch was partly applied");

        // Nothing committed before the throw: the error is not ours to hide, and nothing is recorded.
        var first = new Fake { Admin = admin, Batch = 1, EndpointOf = key => key.ToString().Contains(":b", StringComparison.Ordinal) ? other : A };
        var untouched = first.Barrier(replicaFree: true);
        IReadOnlyList<KeyedWrite> bFirst = [.. KeyedThrowingAtB(new InvalidOperationException("bug")).Reverse()];

        await untouched.Invoking(b => b.WriteManyByKeyAndWaitAsync(bFirst, wayBack: false)).Should().ThrowAsync<InvalidOperationException>().WithMessage("bug");

        untouched.LastError.Should().BeNull();
    }

    [Fact]
    public async Task An_unbound_set_whose_later_transaction_throws_after_another_committed_is_partly_applied_and_cancellation_surfaces()
    {
        var fake = new Fake { Admin = false };
        var barrier = fake.Barrier(replicaFree: true);

        (await barrier.WriteManyAndWaitAsync(WritesThrowingAt(1, new InvalidOperationException("bug")), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Applied.Should().Be(1);
        barrier.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("partly applied: 1 of 2");

        var cancelled = new Fake { Admin = false };
        var cancelling = cancelled.Barrier(replicaFree: true);

        await cancelling.Invoking(b => b.WriteManyAndWaitAsync(WritesThrowingAt(1, new OperationCanceledException()), wayBack: false)).Should().ThrowAsync<OperationCanceledException>();

        cancelling.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("partly applied: 1 of 2");
    }

    [Fact]
    public async Task A_keyed_batch_that_stays_on_one_master_is_one_batch_and_one_wait()
    {
        var fake = new Fake { Batch = 3 };

        (await fake.Barrier().WriteManyByKeyAndWaitAsync(Fake.Keyed("a0", "a1", "a2"), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Waits.Should().Be(1);
        fake.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("run")]
    [InlineData("replication")]
    [InlineData("role")]
    public async Task A_proof_that_differs_after_the_wait_is_not_confirmed_and_the_whole_batch_is_sent_again(string changed)
    {
        var fake = new Fake
        {
            PostClientId = call => changed == "client" && call == 0 ? 8 : 7,
            PostRunId = call => changed == "run" && call == 0 ? "restarted" : Fake.Run,
            PostReplicationId = call => changed == "replication" && call == 0 ? "other" : Fake.Repl,
            Role = call => Task.FromResult(changed == "role" && call == 1 ? FakeRoles.Unknown() : FakeRoles.Master(1)),
        };

        (await fake.Barrier().WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_proof_without_a_replication_id_is_never_confirmed()
    {
        var fake = new Fake { PostReplicationId = _ => string.Empty };

        (await fake.Barrier(retries: 1).WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
    }

    [Fact]
    public async Task A_synchronous_failure_while_building_a_later_transaction_settles_the_started_ones_and_replays_none()
    {
        // Transaction 0 is sent and loses its reply after it applied; transaction 1 throws while it is being built.
        var fake = new Fake
        {
            ApplyOnFailure = true,
            Exec = (_, index) => index == 0 ? new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "failover", null, CommandStatus.Sent) : null,
        };
        var barrier = fake.Barrier(retries: 2);

        (await barrier.WriteManyAndWaitAsync(WritesThatThrowAt(1), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Attempts.Should().Be(1, "the lost reply of transaction 0 may mean it applied, so nothing is sent again");
        fake.Applied.Should().Be(1);
        barrier.LastError.Should().BeOfType<RedisConnectionException>().Which.Message.Should().Contain("failover");

        // Transaction 0 commits; transaction 1 throws while it is being built: the set is partly applied, and never sent again.
        var committed = new Fake();
        var partial = committed.Barrier(retries: 2);

        (await partial.WriteManyAndWaitAsync(WritesThatThrowAt(1), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        committed.Attempts.Should().Be(1);
        committed.Applied.Should().Be(1);
        partial.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("partly applied: 1 of 2");

        // Nothing started before the throw: nothing applied, so the whole set is sent again.
        var first = new Fake();
        var calls = 0;
        IReadOnlyList<Action<ITransaction>> once = [t => { if (calls++ == 0) { throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "not built", null, CommandStatus.WaitingToBeSent); } _ = t.StringSetAsync("barriertest:{fake}:0", "v"); }];

        (await first.Barrier(retries: 2).WriteManyAndWaitAsync(once, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
    }

    [Fact]
    public async Task An_unbound_set_in_which_one_transaction_committed_and_another_had_a_condition_fail_is_partly_applied()
    {
        var fake = new Fake { Admin = false, Batch = 2, Committed = (_, index) => index == 0 };
        var barrier = fake.Barrier(replicaFree: true);

        (await barrier.WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Transactions.Should().Equal(["SET", "EXEC", "SET", "EXEC"], "every transaction runs once, and none again");
        barrier.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("partly applied: 1 of 2");

        // Every transaction committed: not partly applied, and no error.
        var all = new Fake { Admin = false, Batch = 2 };
        var whole = all.Barrier(replicaFree: true);

        (await whole.WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        whole.LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(CommandStatus.Unknown)]
    [InlineData(CommandStatus.Sent)]
    public async Task An_exec_whose_reply_never_arrived_is_not_replayed_even_when_the_other_transactions_committed(CommandStatus status)
    {
        var fake = new Fake
        {
            Batch = 3,
            ApplyOnFailure = true,
            Exec = (_, index) => index == 1 ? new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "failover", null, status) : null,
        };
        var barrier = fake.Barrier(retries: 2);

        (await barrier.WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Attempts.Should().Be(1);
        fake.Applied.Should().Be(3, "every write applied once and none is sent again");
        fake.Waits.Should().Be(0);
        barrier.LastError.Should().BeOfType<RedisConnectionException>();
    }

    [Theory]
    [InlineData("EXECABORT Transaction discarded because of previous errors.")]
    [InlineData("MOVED 1234 b:6379")]
    [InlineData("ASK 1234 b:6379")]
    public async Task A_batch_in_which_every_transaction_certainly_applied_nothing_repeats_whole(string error)
    {
        var fake = new Fake { Batch = 3, Exec = (attempt, _) => attempt == 0 ? new RedisServerException(RedisErrorKind.None, CommandFlags.None, error) : null };

        (await fake.Barrier().WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Attempts.Should().Be(2);
        fake.Waits.Should().Be(1);
    }

    [Theory]
    [InlineData("EXECABORT Transaction discarded because of previous errors.")]
    [InlineData("MOVED 1234 b:6379")]
    [InlineData("ASK 1234 b:6379")]
    public async Task A_transaction_that_failed_after_another_committed_is_a_partial_application_and_is_never_replayed(string error)
    {
        var fake = new Fake { Batch = 3, Exec = (attempt, index) => attempt == 0 && index == 1 ? new RedisServerException(RedisErrorKind.None, CommandFlags.None, error) : null };
        var barrier = fake.Barrier(retries: 2);

        (await barrier.WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Attempts.Should().Be(1, "the committed transactions are never sent again");
        fake.Applied.Should().Be(2);
        fake.Waits.Should().Be(0);
        barrier.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("partly applied");
    }

    [Fact]
    public async Task A_batch_in_which_no_transaction_was_ever_sent_repeats_whole()
    {
        var fake = new Fake
        {
            Batch = 3,
            Exec = (attempt, _) => attempt == 0 ? new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down", null, CommandStatus.WaitingToBeSent) : null,
        };

        (await fake.Barrier().WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_transaction_that_was_never_sent_while_another_committed_is_a_partial_application()
    {
        var fake = new Fake
        {
            Batch = 3,
            Exec = (attempt, index) => attempt == 0 && index == 2 ? new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down", null, CommandStatus.WaitingToBeSent) : null,
        };
        var barrier = fake.Barrier();

        (await barrier.WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Attempts.Should().Be(1);
        barrier.LastError.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task A_batch_whose_conditions_all_failed_wrote_nothing_and_repeats_whole()
    {
        var fake = new Fake { Committed = (attempt, _) => attempt > 0 };

        (await fake.Barrier().WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_transaction_whose_conditions_failed_after_another_committed_is_a_partial_application_and_is_never_replayed()
    {
        var fake = new Fake { Committed = (attempt, index) => attempt > 0 || index != 1 };
        var barrier = fake.Barrier();

        (await barrier.WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Attempts.Should().Be(1);
        fake.Waits.Should().Be(0);
        barrier.LastError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("partly applied");
    }

    [Fact]
    public async Task A_batch_that_stays_redirected_is_not_confirmed_after_the_retries()
    {
        var fake = new Fake { Exec = (_, _) => new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ASK 1234 b:6379") };

        (await fake.Barrier(retries: 2).WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Attempts.Should().Be(3);
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_queued_command_that_fails_after_exec_succeeded_is_not_confirmed_nor_replayed()
    {
        var fake = new Fake { Batch = 3, Failing = (_, index) => index == 1 };
        var barrier = fake.Barrier();

        (await barrier.WriteManyAndWaitAsync(Fake.Writes(3), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Attempts.Should().Be(1);
        fake.Waits.Should().Be(0);
        barrier.LastError.Should().BeOfType<RedisServerException>().Which.Message.Should().Contain("no such key");
    }

    [Fact]
    public async Task A_queued_command_loses_its_fire_and_forget_flag_and_every_queued_task_is_observed()
    {
        var fake = new Fake();

        (await fake.Barrier().WriteManyAndWaitAsync(
            [
                t => _ = t.StringSetAsync("barriertest:{fake}:a", "v", flags: CommandFlags.FireAndForget | CommandFlags.DemandMaster),
                t => _ = t.StringSetAsync("barriertest:{fake}:b", "v", flags: CommandFlags.FireAndForget),
            ],
            wayBack: false)).Should().Be(BarrierOutcome.Confirmed);

        fake.QueuedFlags.Should().Equal([CommandFlags.DemandMaster, CommandFlags.None]);

        var failing = new Fake { Failing = (_, index) => index == 0 };
        (await failing.Barrier().WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().NotBe(BarrierOutcome.Confirmed, "a failure nothing awaited is still seen");
    }

    [Fact]
    public async Task A_way_back_after_a_recent_failure_sends_every_transaction_once_without_a_wait()
    {
        var fake = new Fake { Wait = _ => Task.FromResult(0L) };
        var barrier = fake.Barrier(retries: 1);
        (await barrier.WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        var waits = fake.Waits;
        var attempts = fake.Attempts;

        (await barrier.WriteManyAndWaitAsync(Fake.Writes(2), wayBack: true)).Should().Be(BarrierOutcome.SkippedAfterRecentFailure);

        fake.Waits.Should().Be(waits);
        fake.Attempts.Should().Be(attempts + 1);
        fake.Applied.Should().BeGreaterThanOrEqualTo(2 * (attempts + 1));
    }

    [Fact]
    public async Task A_connection_without_admin_runs_every_transaction_once_unbound_and_never_confirms()
    {
        var fake = new Fake { Admin = false };

        (await fake.Barrier(replicaFree: true).WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);

        fake.Transactions.Should().Equal(["SET", "EXEC", "SET", "EXEC"]);
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task No_replica_in_a_replica_free_deployment_needs_no_wait_but_a_matching_proof()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)) };

        (await fake.Barrier(replicaFree: true).WriteManyAndWaitAsync(Fake.Writes(2), wayBack: false)).Should().Be(BarrierOutcome.NoReplica);
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_batch_needs_at_least_one_transaction()
    {
        var fake = new Fake();

        await fake.Barrier().Invoking(b => b.WriteManyAndWaitAsync([], wayBack: false)).Should().ThrowAsync<ArgumentException>();
    }

    // A keyed write for master A, then one for master B whose queue callback throws the error.
    private static IReadOnlyList<KeyedWrite> KeyedThrowingAtB(Exception error) =>
    [
        new KeyedWrite("barriertest:{fake}:a0", t => _ = t.StringSetAsync("barriertest:{fake}:a0", "v")),
        new KeyedWrite("barriertest:{fake}:b0", _ => throw error),
    ];

    // Two transactions; the queue callback of the one at the index throws the error.
    private static IReadOnlyList<Action<ITransaction>> WritesThrowingAt(int index, Exception error) =>
        [.. Enumerable.Range(0, 2).Select<int, Action<ITransaction>>(i => t =>
        {
            if (i == index)
            {
                throw error;
            }

            _ = t.StringSetAsync($"barriertest:{{fake}}:{i}", "v");
        })];

    // Two transactions; the queue callback of the one at the index throws a transient error while the transaction is being built.
    private static IReadOnlyList<Action<ITransaction>> WritesThatThrowAt(int index) =>
        [.. Enumerable.Range(0, 2).Select<int, Action<ITransaction>>(i => t =>
        {
            if (i == index)
            {
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "not built", null, CommandStatus.WaitingToBeSent);
            }

            _ = t.StringSetAsync($"barriertest:{{fake}}:{i}", "v");
        })];

    private sealed class Fake
    {
        public const string Run = "run-a";
        public const string Repl = "repl-a";

        private int _created;
        private int _posts;
        private int _roles;
        private int _attempt = -1;

        public bool Admin { get; init; } = true;

        /// <summary>How many transactions a batch has; the fake counts attempts by it.</summary>
        public int Batch { get; init; } = 2;

        public Func<int, Task<Role>> Role { get; init; } = _ => Task.FromResult(FakeRoles.Master(1));

        public Func<int, Task<long>> Wait { get; init; } = _ => Task.FromResult(1L);

        /// <summary>The client id a transaction reports, by attempt and transaction index.</summary>
        public Func<int, int, long> ClientIdOf { get; init; } = (_, _) => 7;

        /// <summary>The run id a transaction's INFO reports, by attempt and transaction index.</summary>
        public Func<int, int, string> RunIdOf { get; init; } = (_, _) => Run;

        /// <summary>The master a key is routed to.</summary>
        public Func<RedisKey, EndPoint> EndpointOf { get; init; } = _ => A;

        public Func<int, long> PostClientId { get; init; } = _ => 7;

        public Func<int, string> PostRunId { get; init; } = _ => Run;

        public Func<int, string> PostReplicationId { get; init; } = _ => Repl;

        /// <summary>The exception the EXEC of a transaction throws, by attempt and index; null executes it.</summary>
        public Func<int, int, Exception?> Exec { get; init; } = (_, _) => null;

        public Func<int, int, bool> Committed { get; init; } = (_, _) => true;

        /// <summary>Whether a queued command of the transaction fails at run time after EXEC.</summary>
        public Func<int, int, bool> Failing { get; init; } = (_, _) => false;

        public bool ApplyOnFailure { get; init; }

        public int Attempts => _attempt + 1;

        public int Applied { get; private set; }

        public int Waits { get; private set; }

        public List<EndPoint> WaitEndpoints { get; } = [];

        public List<string> Transactions { get; } = [];

        public List<CommandFlags> ExecFlags { get; } = [];

        public List<CommandFlags> QueuedFlags { get; } = [];

        public List<int> InfoArguments { get; } = [];

        public static IReadOnlyList<Action<ITransaction>> Writes(int count) =>
            [.. Enumerable.Range(0, count).Select<int, Action<ITransaction>>(i => t => _ = t.StringSetAsync($"barriertest:{{fake}}:{i}", "v"))];

        public static IReadOnlyList<KeyedWrite> Keyed(params string[] names) =>
            [.. names.Select(n => new KeyedWrite($"barriertest:{{fake}}:{n}", t => _ = t.StringSetAsync($"barriertest:{{fake}}:{n}", "v")))];

        public ServerBarrier Barrier(int retries = 3, bool replicaFree = false)
        {
            var configuration = $"a:6379,allowAdmin={Admin}";
            var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, args) => method.Name switch
            {
                nameof(IConnectionMultiplexer.GetServer) when args[0] is EndPoint endpoint => Server(endpoint),
                "get_Configuration" => configuration,
                _ => throw new NotSupportedException(method.Name),
            });
            var db = InterceptingProxy.Wrap<IDatabase>(null!, (method, args) => method.Name switch
            {
                "get_Multiplexer" => mux,
                nameof(IDatabase.IdentifyEndpointAsync) => Task.FromResult<EndPoint?>(EndpointOf((RedisKey)args[0]!)),
                nameof(IDatabase.CreateTransaction) => Transaction(),
                _ => throw new NotSupportedException(method.Name),
            });
            return new ServerBarrier(db, "barriertest:{fake}", TimeProvider.System, replicaFree) { Retries = retries };
        }

        private static Task<RedisResult> Info(string runId, string replicationId) =>
            Task.FromResult(RedisResult.Create((RedisValue)$"# Server\r\nredis_version:7.4.0\r\nrun_id:{runId}\r\n# Replication\r\nrole:master\r\nmaster_replid:{replicationId}\r\n"));

        private ITransaction Transaction()
        {
            // The transactions of an attempt are created in order; index 0 starts the next attempt.
            var index = _created++ % Batch;
            if (index == 0)
            {
                _attempt++;
            }

            return Create(index);
        }

        private ITransaction Create(int index)
        {
            return InterceptingProxy.Wrap<ITransaction>(null!, (method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(ITransaction.ExecuteAsync) when args[0] is CommandFlags flags:
                        Transactions.Add("EXEC");
                        ExecFlags.Add(flags);
                        if (Exec(_attempt, index) is { } failure)
                        {
                            Applied += ApplyOnFailure ? 1 : 0;
                            return Task.FromException<bool>(failure);
                        }

                        var committed = Committed(_attempt, index);
                        Applied += committed ? 1 : 0;
                        return Task.FromResult(committed);
                    case nameof(ITransaction.ExecuteAsync) when args[0] is "CLIENT":
                        Transactions.Add("CLIENT");
                        return Task.FromResult(RedisResult.Create(ClientIdOf(_attempt, index)));
                    case nameof(ITransaction.ExecuteAsync) when args[0] is "INFO":
                        Transactions.Add("INFO");
                        InfoArguments.Add(((object[])args[1]!).Length);
                        return Info(RunIdOf(_attempt, index), Repl);
                    case nameof(ITransaction.StringSetAsync):
                        Transactions.Add("SET");
                        QueuedFlags.Add(args.OfType<CommandFlags>().Single());
                        return Failing(_attempt, index)
                            ? Task.FromException<bool>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ERR no such key"))
                            : Task.FromResult(true);
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
        }

        private IServer Server(EndPoint endpoint) => InterceptingProxy.Wrap<IServer>(null!, (method, args) => method.Name switch
        {
            "get_EndPoint" => endpoint,
            nameof(IServer.RoleAsync) => Role(_roles++),
            nameof(IServer.ExecuteAsync) => (string)args[0]! switch
            {
                "WAIT" => WaitAsync(endpoint),
                "CLIENT" => Task.FromResult(RedisResult.Create(PostClientId(_posts))),
                "INFO" => Info(PostRunId(_posts), PostReplicationId(_posts++)),
                var other => throw new NotSupportedException(other),
            },
            _ => throw new NotSupportedException(method.Name),
        });

        private async Task<RedisResult> WaitAsync(EndPoint endpoint)
        {
            WaitEndpoints.Add(endpoint);
            return RedisResult.Create(await Wait(Waits++).ConfigureAwait(false));
        }
    }
}
