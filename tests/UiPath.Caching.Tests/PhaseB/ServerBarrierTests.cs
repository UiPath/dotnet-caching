using System.Net;
using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>The server barrier over a fake database, transaction and server, so it runs without a replica.</summary>
public sealed class ServerBarrierTests
{
    private static readonly EndPoint A = new DnsEndPoint("a", 6379);
    private static readonly EndPoint B = new DnsEndPoint("b", 6379);

    [Fact]
    public async Task A_wait_that_confirms_ends_the_barrier_after_one_transaction()
    {
        var fake = new Fake();

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(1);
        fake.Waits.Should().Be(1);
    }

    [Fact]
    public async Task The_transaction_runs_the_proof_first_then_the_writes_and_never_follows_a_redirect()
    {
        var fake = new Fake();

        await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false);

        fake.Transactions.Should().Equal(["CLIENT", "INFO", "SET", "EXEC"]);
        fake.InfoArguments.Should().Equal(0);
        fake.ExecFlags.Should().OnlyContain(flags => flags.HasFlag(CommandFlags.NoRedirect) && flags.HasFlag(CommandFlags.DemandMaster));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_demoted_master_is_never_written_to_nor_reported_as_having_no_replica(bool replicaFree)
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Unknown()) };

        (await fake.Barrier(retries: 2, replicaFree).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Waits.Should().Be(0);
        fake.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_master_demoted_after_the_wait_is_not_confirmed()
    {
        // ROLE before the write, then after the WAIT: the second answer of the first attempt is no longer a master.
        var fake = new Fake { Role = call => Task.FromResult(call == 1 ? FakeRoles.Unknown() : FakeRoles.Master(1)) };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2, "the whole transaction and WAIT ran again");
        fake.Waits.Should().Be(2);
    }

    [Fact]
    public async Task A_different_client_id_after_the_wait_is_not_confirmed_though_no_event_was_delivered()
    {
        // The transaction ran on connection 7; after the WAIT the server says the client is 8: a reconnect, whatever the client has reported.
        var fake = new Fake { PostClientId = call => call == 0 ? 8 : 7 };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2);
        fake.Waits.Should().Be(2);
    }

    [Fact]
    public async Task A_run_id_that_changed_with_the_same_client_id_is_not_confirmed()
    {
        // A restarted server can hand out the same client id again, never the same run id.
        var fake = new Fake { PostRunId = call => call == 0 ? "restarted" : Fake.Run };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2);
    }

    [Fact]
    public async Task A_node_demoted_resynced_and_promoted_on_a_surviving_connection_is_not_confirmed()
    {
        // The connection and the process are the same, so the client id and the run id are equal; only the replication id shows that the node started a new history.
        var fake = new Fake { PostReplicationId = call => call == 0 ? "repl-other" : Fake.Repl };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2, "the whole transaction and WAIT ran again");
        fake.Waits.Should().Be(2);
    }

    [Fact]
    public async Task A_proof_without_a_replication_id_is_never_confirmed()
    {
        var fake = new Fake { PostReplicationId = _ => string.Empty };

        (await fake.Barrier(retries: 1).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
    }

    [Fact]
    public async Task A_connection_that_never_proves_the_same_is_never_confirmed()
    {
        var fake = new Fake { PostClientId = call => 100 + call };

        (await fake.Barrier(retries: 2).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Writes.Should().Be(3);
    }

    [Fact]
    public async Task No_replica_with_replicas_expected_is_not_confirmed_after_the_retries()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)) };

        (await fake.Barrier(retries: 3, replicaFree: false).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Writes.Should().Be(4, "attempt 0 and one per retry");
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task No_replica_in_a_replica_free_deployment_needs_no_wait_but_a_matching_proof()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)) };

        (await fake.Barrier(replicaFree: true).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NoReplica);
        fake.Waits.Should().Be(0);
        fake.Writes.Should().Be(1);
    }

    [Fact]
    public async Task No_replica_needs_the_connection_that_wrote_too()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)), PostClientId = call => call == 0 ? 8 : 7 };

        (await fake.Barrier(replicaFree: true).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NoReplica);
        fake.Writes.Should().Be(2, "the first proof did not match, so the writes ran again");
    }

    [Fact]
    public async Task No_replica_is_not_reported_when_the_proof_never_matches()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)), PostRunId = call => $"run-{call}" };

        (await fake.Barrier(retries: 1, replicaFree: true).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
    }

    [Fact]
    public async Task Replicas_seen_and_then_none_are_not_confirmed_even_when_replica_free()
    {
        var fake = new Fake { Role = call => Task.FromResult(FakeRoles.Master(call == 0 ? 1 : 0)), Wait = _ => Task.FromResult(0L) };

        (await fake.Barrier(retries: 2, replicaFree: true).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Waits.Should().Be(1);
    }

    [Fact]
    public async Task A_master_that_moves_between_the_writes_and_the_wait_is_not_waited_on()
    {
        // Each attempt identifies the owner twice: before the transaction and after it. The second answer of attempt 0 differs.
        var fake = new Fake { Identify = call => call == 1 ? B : A };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2);
        fake.Waits.Should().Be(1, "the attempt that saw the owner move did not wait");
    }

    [Fact]
    public async Task A_wait_that_times_out_on_the_client_is_retried_with_the_whole_transaction()
    {
        var fake = new Fake { Wait = call => call == 0 ? Task.FromException<long>(new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent)) : Task.FromResult(1L) };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2);
    }

    [Fact]
    public async Task An_endpoint_that_cannot_be_resolved_is_not_confirmed_and_never_written_or_waited_on()
    {
        var unresolved = new Fake { Identify = _ => null };

        (await unresolved.Barrier(retries: 1, replicaFree: true).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        unresolved.Waits.Should().Be(0);
        unresolved.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_role_that_fails_is_not_confirmed()
    {
        var fake = new Fake { Role = _ => Task.FromException<Role>(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "gone", null, CommandStatus.Unknown)) };

        (await fake.Barrier(retries: 1).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Waits.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_role_that_is_denied_leaves_the_replicas_unknown_and_wait_with_the_proof_decides(bool replicaFree)
    {
        Task<Role> Denied(int _) => Task.FromException<Role>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User u has no permissions to run the 'role' command"));

        var confirming = new Fake { Role = Denied };
        var empty = new Fake { Role = Denied, Wait = _ => Task.FromResult(0L) };
        var moved = new Fake { Role = Denied, PostClientId = call => call == 0 ? 8 : 7 };

        (await confirming.Barrier(replicaFree: replicaFree).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        confirming.Waits.Should().Be(1);
        confirming.Roles.Should().Be(1, "the ROLE parts are skipped once denied");
        (await empty.Barrier(retries: 1, replicaFree).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed, "0 is not a confirmation and never NoReplica");
        (await moved.Barrier(replicaFree: replicaFree).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        moved.Writes.Should().Be(2, "WAIT of 1 confirms only with a matching proof");
    }

    [Theory]
    [InlineData("MOVED 1234 b:6379")]
    [InlineData("ASK 1234 b:6379")]
    public async Task A_redirect_on_the_transaction_means_not_applied_and_a_new_attempt(string redirect)
    {
        var fake = new Fake { Exec = call => call == 0 ? new RedisServerException(RedisErrorKind.None, CommandFlags.None, redirect) : null };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2);
        fake.Waits.Should().Be(1);
    }

    [Fact]
    public async Task A_transaction_aborted_at_queue_time_is_not_applied_and_repeated()
    {
        var fake = new Fake { Exec = call => call == 0 ? new RedisServerException(RedisErrorKind.None, CommandFlags.None, "EXECABORT Transaction discarded because of previous errors.") : null };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2);
        fake.Waits.Should().Be(1);

        var denied = new Fake { Exec = _ => new RedisServerException(RedisErrorKind.None, CommandFlags.None, "EXECABORT Transaction discarded because of: NOPERM") };
        (await denied.Barrier(retries: 1).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        denied.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_transaction_that_stays_redirected_is_not_confirmed_after_the_retries()
    {
        var fake = new Fake { Exec = _ => new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ASK 1234 b:6379") };

        (await fake.Barrier(retries: 2).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Writes.Should().Be(3);
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_transaction_whose_conditions_failed_wrote_nothing_and_is_repeated()
    {
        var fake = new Fake { Committed = call => call > 0 };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2);
        fake.Waits.Should().Be(1);
    }

    [Fact]
    public async Task A_write_that_fails_in_a_failover_is_not_confirmed_never_an_exception()
    {
        var fake = new Fake { Exec = _ => new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "failover", null, CommandStatus.Unknown) };

        (await fake.Barrier(retries: 2).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Writes.Should().Be(3);
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_connection_without_admin_runs_the_writes_once_unbound_and_never_confirms()
    {
        var fake = new Fake { Admin = false };
        var barrier = fake.Barrier(replicaFree: true);

        (await barrier.WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Writes.Should().Be(1);
        fake.Transactions.Should().Equal(["SET", "EXEC"], "nothing is bound without INFO");
        fake.Waits.Should().Be(0);

        (await new Fake { Admin = false, Role = _ => Task.FromResult(FakeRoles.Master(0)) }.Barrier(replicaFree: true).WriteAndWaitAsync(Fake.Write, wayBack: false))
            .Should().Be(BarrierOutcome.NotConfirmed, "not even NoReplica");
    }

    [Theory]
    [InlineData("a:6379", 4000)]
    [InlineData("a:6379,syncTimeout=2000", 1600)]
    [InlineData("a:6379,asyncTimeout=10000", 8000)]
    [InlineData("a:6379,syncTimeout=2000,asyncTimeout=1000", 800)]
    public async Task The_default_timeout_is_eighty_percent_of_the_connections_async_timeout(string configuration, long expected)
    {
        var fake = new Fake { Configuration = $"{configuration},allowAdmin=true" };
        var barrier = fake.Barrier();

        barrier.Timeout.Should().Be(TimeSpan.FromMilliseconds(expected));
        (await barrier.WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.WaitTimeouts.Should().Equal(expected);
    }

    [Fact]
    public void An_explicit_timeout_overrides_the_derived_one()
    {
        var fake = new Fake { Configuration = "a:6379,asyncTimeout=10000,allowAdmin=true" };

        fake.Barrier(timeout: TimeSpan.FromMilliseconds(200)).Timeout.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task A_way_back_skips_the_wait_of_a_master_that_failed_within_the_episode_but_still_writes()
    {
        var fake = new Fake { Wait = _ => Task.FromResult(0L) };
        var barrier = fake.Barrier(retries: 1);

        (await barrier.WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        var waits = fake.Waits;
        var writes = fake.Writes;

        (await barrier.WriteAndWaitAsync(Fake.Write, wayBack: true)).Should().Be(BarrierOutcome.SkippedAfterRecentFailure);
        fake.Waits.Should().Be(waits);
        fake.Writes.Should().Be(writes + 1, "the writes are sent once, without a WAIT");
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task An_info_that_answers_busy_once_is_not_applied_and_the_next_attempt_confirms(int transactionBusy, int postBusy)
    {
        var fake = new Fake { TransactionInfoBusy = transactionBusy, PostInfoBusy = postBusy };

        (await fake.Barrier().WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Writes.Should().Be(2, "the busy attempt was repeated");
    }

    [Fact]
    public async Task An_info_that_stays_busy_is_not_confirmed_once_the_retries_are_spent_and_never_throws()
    {
        var fake = new Fake { TransactionInfoBusy = int.MaxValue };

        (await fake.Barrier(retries: 2).WriteAndWaitAsync(Fake.Write, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Writes.Should().Be(3);
        fake.Waits.Should().Be(0);
    }

    /// <summary>A database, multiplexer, transaction and server whose answers come from delegates, each given its call number.</summary>
    private sealed class Fake
    {
        public const string Run = "run-a";
        public const string Repl = "repl-a";

        private int _identifies;
        private int _posts;

        public Func<int, EndPoint?> Identify { get; init; } = _ => A;

        public string? Configuration { get; init; }

        public bool Admin { get; init; } = true;

        public List<object?> WaitTimeouts { get; } = [];

        public Func<int, Task<Role>> Role { get; init; } = _ => Task.FromResult(FakeRoles.Master(1));

        public Func<int, Task<long>> Wait { get; init; } = _ => Task.FromResult(1L);

        /// <summary>The client id the server reports after the WAIT; the transaction always runs on 7.</summary>
        public Func<int, long> PostClientId { get; init; } = _ => 7;

        public Func<int, string> PostRunId { get; init; } = _ => Run;

        public Func<int, string> PostReplicationId { get; init; } = _ => Repl;

        /// <summary>The exception the transaction's EXEC throws, by write number; null executes it.</summary>
        public Func<int, Exception?> Exec { get; init; } = _ => null;

        /// <summary>Whether the transaction's conditions held, by write number.</summary>
        public Func<int, bool> Committed { get; init; } = _ => true;

        public int Writes { get; private set; }

        public int Waits { get; private set; }

        public int Roles { get; private set; }

        public List<CommandFlags> ExecFlags { get; } = [];

        public List<string> Transactions { get; } = [];

        /// <summary>How many INFO replies in the transaction answer BUSY before they succeed.</summary>
        public int TransactionInfoBusy { get; set; }

        /// <summary>How many INFO replies after the WAIT answer BUSY before they succeed.</summary>
        public int PostInfoBusy { get; set; }

        /// <summary>The number of arguments of every INFO sent in the transaction.</summary>
        public List<int> InfoArguments { get; } = [];

        public static Task Write(ITransaction transaction) => transaction.StringSetAsync("barriertest:{fake}", "v");

        public ServerBarrier Barrier(int retries = 3, bool replicaFree = false, TimeSpan? timeout = null)
        {
            var configuration = Configuration ?? $"a:6379,allowAdmin={Admin}";
            var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, args) => method.Name switch
            {
                nameof(IConnectionMultiplexer.GetServer) when args[0] is EndPoint endpoint => Server(endpoint),
                "get_Configuration" => configuration,
                _ => throw new NotSupportedException(method.Name),
            });
            var db = InterceptingProxy.Wrap<IDatabase>(null!, (method, _) => method.Name switch
            {
                "get_Multiplexer" => mux,
                nameof(IDatabase.IdentifyEndpointAsync) => Task.FromResult(Identify(_identifies++)),
                nameof(IDatabase.CreateTransaction) => Transaction(),
                _ => throw new NotSupportedException(method.Name),
            });
            return timeout is { } explicitTimeout
                ? new ServerBarrier(db, "barriertest:{fake}", TimeProvider.System, replicaFree) { Retries = retries, Timeout = explicitTimeout }
                : new ServerBarrier(db, "barriertest:{fake}", TimeProvider.System, replicaFree) { Retries = retries };
        }

        private static Task<RedisResult> Busy() =>
            Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "BUSY Redis is busy running a script. You can only call SCRIPT KILL or SHUTDOWN NOSAVE."));

        private static Task<RedisResult> Info(string runId, string replicationId) =>
            Task.FromResult(RedisResult.Create((RedisValue)$"# Server\r\nredis_version:7.4.0\r\nrun_id:{runId}\r\n# Replication\r\nrole:master\r\nmaster_replid:{replicationId}\r\n"));

        private static async Task<RedisResult> AsResult(Task<long> value, List<object?>? timeouts = null, object? timeout = null)
        {
            timeouts?.Add(timeout);
            return RedisResult.Create(await value.ConfigureAwait(false));
        }

        private ITransaction Transaction()
        {
            Transactions.Clear();
            return InterceptingProxy.Wrap<ITransaction>(null!, (method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(ITransaction.ExecuteAsync) when args[0] is CommandFlags flags:
                        Transactions.Add("EXEC");
                        ExecFlags.Add(flags);
                        var write = Writes++;
                        return Exec(write) is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(Committed(write));
                    case nameof(ITransaction.ExecuteAsync) when args[0] is "CLIENT":
                        Transactions.Add("CLIENT");
                        return Task.FromResult(RedisResult.Create(7));
                    case nameof(ITransaction.ExecuteAsync) when args[0] is "INFO":
                        Transactions.Add("INFO");
                        InfoArguments.Add(((object[])args[1]!).Length);
                        return TransactionInfoBusy-- > 0 ? Busy() : Info(Run, Repl);
                    case nameof(ITransaction.StringSetAsync):
                        Transactions.Add("SET");
                        return Task.FromResult(true);
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
        }

        private IServer Server(EndPoint endpoint) => InterceptingProxy.Wrap<IServer>(null!, (method, args) => method.Name switch
        {
            "get_EndPoint" => endpoint,
            nameof(IServer.RoleAsync) => Role(Roles++),
            nameof(IServer.ExecuteAsync) => (string)args[0]! switch
            {
                "WAIT" => AsResult(Wait(Waits++), WaitTimeouts, ((object[])args[1]!)[1]),
                "CLIENT" => Task.FromResult(RedisResult.Create(PostClientId(_posts))),
                "INFO" => PostInfoBusy-- > 0 ? Busy() : Info(PostRunId(_posts), PostReplicationId(_posts++)),
                var other => throw new NotSupportedException(other),
            },
            _ => throw new NotSupportedException(method.Name),
        });
    }
}
