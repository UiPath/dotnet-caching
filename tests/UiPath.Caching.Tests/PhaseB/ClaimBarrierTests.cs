using System.Net;
using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>The per-attempt barrier of the claim steps over a fake server and database, so it runs without a replica.</summary>
public sealed class ClaimBarrierTests
{
    private static readonly EndPoint A = new DnsEndPoint("a", 6379);
    private static readonly EndPoint B = new DnsEndPoint("b", 6379);

    [Fact]
    public async Task A_wait_that_confirms_ends_after_one_step()
    {
        var fake = new Fake();

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(1);
    }

    [Fact]
    public async Task A_wait_that_never_confirms_repeats_the_step_and_keeps_the_first_code()
    {
        var fake = new Fake { Wait = _ => Task.FromResult(0L) };

        (await fake.Store(retries: 2).OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
        fake.Steps.Should().Be(3);
        fake.Waits.Should().Be(3);
    }

    [Fact]
    public async Task A_master_that_moves_between_the_write_and_the_wait_is_not_waited_on()
    {
        // Each attempt identifies the master twice: before the write, and before the WAIT.
        var fake = new Fake { Identify = call => call == 1 ? B : A };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(2, "the step is repeated on the master that holds the key now");
        fake.Waits.Should().Be(1);
    }

    [Fact]
    public async Task A_node_that_is_no_longer_a_master_is_not_taken_for_one_without_replicas()
    {
        var fake = new Fake { Role = call => Task.FromResult(call == 0 ? FakeRoles.Unknown() : FakeRoles.Master(1)) };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(2);
    }

    [Fact]
    public async Task A_role_that_cannot_be_read_is_not_confirmed()
    {
        var fake = new Fake { Role = _ => Task.FromException<Role>(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "gone", null, CommandStatus.Unknown)) };

        (await fake.Store(retries: 1).OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_master_without_replicas_needs_no_wait()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)) };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.NoReplica));
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task Replicas_that_drop_to_zero_after_one_was_seen_are_not_confirmed()
    {
        var fake = new Fake { Role = call => Task.FromResult(FakeRoles.Master(call == 0 ? 1 : 0)), Wait = _ => Task.FromResult(0L) };

        (await fake.Store(retries: 1).OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
    }

    [Fact]
    public async Task A_repeat_that_throws_keeps_the_first_success_as_not_confirmed()
    {
        var fake = new Fake
        {
            Wait = _ => Task.FromResult(0L),
            Step = call => call == 0 ? Task.FromResult(1L) : Task.FromException<long>(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "failover", null, CommandStatus.Unknown)),
        };

        (await fake.Store(retries: 2).ReopenAsync("rt:a", Fake.Token)).Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
        fake.Waits.Should().Be(1, "a failed repeat wrote nothing to wait for");
    }

    [Fact]
    public async Task A_repeat_that_no_longer_succeeds_stops_as_not_confirmed()
    {
        var fake = new Fake { Wait = _ => Task.FromResult(0L), Step = call => Task.FromResult(call == 0 ? 1L : ClaimCodes.Refused) };

        (await fake.Store(retries: 3).IdleAsync("rt:a", Fake.Token)).Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
        fake.Steps.Should().Be(2);
    }

    [Fact]
    public async Task A_wait_that_times_out_on_the_client_is_repeated()
    {
        var fake = new Fake { Wait = call => call == 0 ? Task.FromException<long>(new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent)) : Task.FromResult(1L) };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(2);
    }

    [Fact]
    public async Task A_refused_step_needs_no_wait()
    {
        var fake = new Fake { Step = _ => Task.FromResult(ClaimCodes.Refused) };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(ClaimCodes.Refused, ClaimDurability.NotApplicable));
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_wait_on_another_connection_than_the_write_is_not_confirmed_even_with_no_event_delivered()
    {
        // The write's transaction ran on connection 7; after the WAIT the server says the client is 8: a reconnect happened, whatever the client reported.
        var fake = new Fake { PostClientId = call => call == 0 ? 8 : 7 };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(2, "the whole write and WAIT ran again");
    }

    [Fact]
    public async Task A_server_that_restarted_between_the_write_and_the_wait_is_not_confirmed_even_with_the_same_client_id()
    {
        // A to B to A with A restarted: the client id can repeat on the new process, the run id cannot.
        var fake = new Fake { PostRunId = call => call == 0 ? "restarted" : Fake.Run };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(2);
    }

    [Fact]
    public async Task A_master_demoted_during_the_wait_is_not_confirmed()
    {
        // ROLE before the write, then after the WAIT: the second answer of the first attempt is no longer a master.
        var fake = new Fake { Role = call => Task.FromResult(call == 1 ? FakeRoles.Unknown() : FakeRoles.Master(1)) };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(2);
    }

    [Fact]
    public async Task A_master_without_replicas_is_no_replica_only_on_the_connection_that_wrote()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)), PostClientId = call => call == 0 ? 8 : 7 };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.NoReplica));
        fake.Steps.Should().Be(2);
    }

    [Fact]
    public async Task A_bound_write_runs_in_one_transaction_with_the_connection_proof_and_never_follows_a_redirect()
    {
        var fake = new Fake();

        await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true);

        fake.Transactions.Should().Equal(["CLIENT", "INFO", "EVAL", "EXEC"]);
        fake.StepFlags.Should().OnlyContain(flags => flags.HasFlag(CommandFlags.NoRedirect) && flags.HasFlag(CommandFlags.DemandMaster));
    }

    [Theory]
    [InlineData("MOVED 1234 b:6379")]
    [InlineData("ASK 1234 b:6379")]
    public async Task A_redirected_first_write_is_not_applied_and_starts_the_attempt_over(string redirect)
    {
        var fake = new Fake { Step = call => call == 0 ? Task.FromException<long>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, redirect)) : Task.FromResult(1L) };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(2);
    }

    [Fact]
    public async Task A_write_that_stays_redirected_fails_after_the_retries()
    {
        var fake = new Fake { Step = _ => Task.FromException<long>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ASK 1234 b:6379")) };

        var act = () => fake.Store(retries: 2).OpenAsync("rt:a", Fake.Token, "rt", restore: true);

        await act.Should().ThrowAsync<RedisServerException>("a key that stays mid-migration is unavailable, never written elsewhere");
        fake.Steps.Should().Be(3);
    }

    [Fact]
    public async Task A_connection_without_admin_cannot_prove_a_wait_so_it_is_never_confirmed()
    {
        var fake = new Fake { Admin = false };

        (await fake.Store(retries: 1).OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
        fake.Transactions.Should().BeEmpty("nothing is bound without INFO");
    }

    [Fact]
    public async Task A_master_demoted_resynced_and_promoted_on_a_surviving_connection_is_not_confirmed()
    {
        // The connection and the process survive, so the client id and run id repeat; the full resync gave the master a new replication id.
        var fake = new Fake { PostReplicationId = call => call == 0 ? "resynced" : Fake.Repl };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Steps.Should().Be(2);
    }

    [Fact]
    public async Task A_transaction_discarded_because_the_script_is_refused_takes_the_fallback()
    {
        // The server answers EXECABORT; only the probe sent alone shows the script is refused.
        var fake = new Fake { Abort = call => call == 0, Probe = () => throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'eval' command") };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.FallbackWrites.Should().Be(1, "the fallback's watched transaction carried the write, bound like the script");
    }

    [Fact]
    public async Task A_transaction_discarded_because_the_key_is_redirected_starts_the_attempt_over()
    {
        var fake = new Fake { Abort = call => call == 0, Probe = () => throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ASK 1234 b:6379") };

        (await fake.Store().OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task A_transaction_discarded_because_the_proof_is_refused_runs_unbound_and_is_never_confirmed()
    {
        var fake = new Fake { Abort = call => call == 0, ProofRefused = true };

        (await fake.Store(retries: 1).OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.NotConfirmed));
        fake.Steps.Should().BeGreaterThan(0, "the write still runs, only unproven");
    }

    [Fact]
    public async Task A_diagnosis_that_fails_once_for_a_transient_reason_latches_nothing_and_the_store_confirms_later()
    {
        // The first transaction is discarded, and its diagnosis meets BUSY: that says nothing about the proof, so the next attempt is bound again.
        var fake = new Fake { Abort = call => call == 0, DiagnosisBusy = 1 };
        var store = fake.Store();

        (await store.OpenAsync("rt:a", Fake.Token, "rt", restore: true)).Should().Be(new ClaimResult(1, ClaimDurability.Confirmed));
        fake.Transactions.Should().Contain("CLIENT", "the confirming attempt was bound");
        (await store.ReopenAsync("rt:a", Fake.Token)).Durability.Should().Be(ClaimDurability.Confirmed, "the store still proves its steps");
    }

    [Fact]
    public async Task A_transaction_discarded_for_a_cause_its_parts_do_not_show_is_retried_and_then_fails()
    {
        var fake = new Fake { Abort = _ => true };

        var act = () => fake.Store(retries: 2).OpenAsync("rt:a", Fake.Token, "rt", restore: true);

        await act.Should().ThrowAsync<ClaimNotAppliedException>();
        fake.Aborts.Should().Be(3);
    }

    private static async Task<RedisResult> AsResult(Task<long> value) => RedisResult.Create(await value.ConfigureAwait(false));

    /// <summary>A database whose scripts, endpoint, role, WAIT and connection proof answer from the delegates, each given its call number.</summary>
    private sealed class Fake
    {
        public const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string Run = "run-a";
        public const string Repl = "repl-a";

        private int _identifies;
        private int _roles;
        private int _posts;
        private int _transactions;

        public Func<int, EndPoint> Identify { get; init; } = _ => A;

        public Func<int, Task<Role>> Role { get; init; } = _ => Task.FromResult(FakeRoles.Master(1));

        public Func<int, Task<long>> Wait { get; init; } = _ => Task.FromResult(1L);

        public Func<int, Task<long>> Step { get; init; } = call => Task.FromResult(call == 0 ? 1L : 2L);

        /// <summary>The client id the server reports after the WAIT; the write's transaction always runs on 7.</summary>
        public Func<int, long> PostClientId { get; init; } = _ => 7;

        public Func<int, string> PostRunId { get; init; } = _ => Run;

        public Func<int, string> PostReplicationId { get; init; } = _ => Repl;

        /// <summary>Whether the transaction with this number is discarded with EXECABORT.</summary>
        public Func<int, bool> Abort { get; init; } = _ => false;

        /// <summary>The probe script sent alone after an EXECABORT.</summary>
        public Func<long> Probe { get; init; } = () => 1;

        public bool ProofRefused { get; init; }

        /// <summary>How many diagnosis reads answer BUSY before they succeed.</summary>
        public int DiagnosisBusy { get; set; }

        public int Aborts { get; private set; }

        public int FallbackWrites { get; private set; }

        public bool Admin { get; init; } = true;

        public int Steps { get; private set; }

        public int Waits { get; private set; }

        public List<CommandFlags> StepFlags { get; } = [];

        public List<string> Transactions { get; } = [];

        public ClaimStore Store(int retries = 3)
        {
            var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, args) => method.Name switch
            {
                nameof(IConnectionMultiplexer.GetServer) when args[0] is EndPoint endpoint => Server(endpoint),
                "get_Configuration" => $"a:6379,allowAdmin={Admin}",
                _ => throw new NotSupportedException(method.Name),
            });
            var db = InterceptingProxy.Wrap<IDatabase>(null!, (method, args) => method.Name switch
            {
                "get_Multiplexer" => mux,
                nameof(IDatabase.IdentifyEndpointAsync) => Task.FromResult<EndPoint?>(Identify(_identifies++)),
                nameof(IDatabase.ScriptEvaluateAsync) when args[0] is "return 1" => ProbeResult(),
                nameof(IDatabase.ScriptEvaluateAsync) => Evaluate((CommandFlags)args[3]!),
                nameof(IDatabase.CreateTransaction) => Transaction(),
                nameof(IDatabase.ExecuteAsync) => Diagnose(),
                nameof(IDatabase.HashGetAllAsync) => Task.FromResult(Array.Empty<HashEntry>()),
                _ => throw new NotSupportedException(method.Name),
            });
            return new ClaimStore(db, "claimtest:mx:{fake}", useScripts: true, new Version(7, 4)) { BarrierRetries = retries };
        }

        private static Task<RedisResult> Info(string runId, string replicationId) =>
            Task.FromResult(RedisResult.Create((RedisValue)$"# Server\r\nredis_version:7.4.0\r\nrun_id:{runId}\r\n\r\n# Replication\r\nrole:master\r\nmaster_replid:{replicationId}\r\n"));

        private Task<RedisResult> Diagnose()
        {
            if (ProofRefused)
            {
                return Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'client|id' command"));
            }

            if (DiagnosisBusy > 0)
            {
                DiagnosisBusy--;
                return Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "BUSY Redis is busy running a script. You can only call SCRIPT KILL or SHUTDOWN NOSAVE."));
            }

            return Task.FromResult(RedisResult.Create(7));
        }

        private Task<RedisResult> ProbeResult()
        {
            try
            {
                return Task.FromResult(RedisResult.Create(Probe()));
            }
            catch (Exception ex)
            {
                return Task.FromException<RedisResult>(ex);
            }
        }

        private Task<RedisResult> Evaluate(CommandFlags flags)
        {
            StepFlags.Add(flags);
            return AsResult(Step(Steps++));
        }

        private ITransaction Transaction()
        {
            Transactions.Clear();
            var number = _transactions++;
            var abort = Abort(number);
            return InterceptingProxy.Wrap<ITransaction>(null!, (method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(ITransaction.ExecuteAsync) when args.Length == 1 && args[0] is CommandFlags:
                        Transactions.Add("EXEC");
                        if (abort)
                        {
                            Aborts++;
                            return Task.FromException<bool>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "EXECABORT Transaction discarded because of previous errors."));
                        }

                        return Task.FromResult(true);
                    case nameof(ITransaction.AddCondition):
                        return null;
                    case nameof(ITransaction.HashSetAsync):
                        if (!abort)
                        {
                            FallbackWrites++;
                        }

                        return Task.FromResult(true);
                    case nameof(ITransaction.HashDeleteAsync):
                        return Task.FromResult(true);
                    case nameof(ITransaction.ExecuteAsync) when args[0] is "CLIENT":
                        Transactions.Add("CLIENT");
                        return Task.FromResult(RedisResult.Create(7));
                    case nameof(ITransaction.ExecuteAsync) when args[0] is "INFO":
                        Transactions.Add("INFO");
                        return Info(Run, Repl);
                    case nameof(ITransaction.ScriptEvaluateAsync):
                        Transactions.Add("EVAL");

                        // A discarded transaction runs nothing it queued.
                        return abort
                            ? Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "EXECABORT Transaction discarded because of previous errors."))
                            : Evaluate((CommandFlags)args[3]!);
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
                "WAIT" => AsResult(Wait(Waits++)),
                "CLIENT" => Task.FromResult(RedisResult.Create(PostClientId(_posts))),
                "INFO" => Info(PostRunId(_posts), PostReplicationId(_posts++)),
                var other => throw new NotSupportedException(other),
            },
            _ => throw new NotSupportedException(method.Name),
        });
    }
}
