using System.Net;
using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>The server barrier over a fake database and server, so it runs without a replica.</summary>
public sealed class ServerBarrierTests
{
    private static readonly EndPoint A = new DnsEndPoint("a", 6379);
    private static readonly EndPoint B = new DnsEndPoint("b", 6379);

    [Fact]
    public async Task A_wait_that_confirms_ends_the_barrier_without_a_reissue()
    {
        var fake = new Fake();

        (await fake.Barrier().WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Reissues.Should().Be(0);
        fake.Waits.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_demoted_master_is_retried_and_never_reported_as_having_no_replica(bool replicaFree)
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Unknown()) };

        (await fake.Barrier(retries: 2, replicaFree).WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Waits.Should().Be(0);
        fake.Reissues.Should().Be(2);
    }

    [Fact]
    public async Task No_replica_with_replicas_expected_is_not_confirmed_after_the_retries()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)) };

        (await fake.Barrier(retries: 3, replicaFree: false).WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Reissues.Should().Be(3, "the reissue count equals the retries");
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task No_replica_in_a_replica_free_deployment_needs_no_wait()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)) };

        (await fake.Barrier(replicaFree: true).WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NoReplica);
        fake.Waits.Should().Be(0);
        fake.Reissues.Should().Be(0);
    }

    [Fact]
    public async Task Replicas_seen_and_then_none_are_not_confirmed_even_when_replica_free()
    {
        var fake = new Fake { Role = call => Task.FromResult(FakeRoles.Master(call == 0 ? 1 : 0)), Wait = _ => Task.FromResult(0L) };

        (await fake.Barrier(retries: 2, replicaFree: true).WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Waits.Should().Be(1);
    }

    [Fact]
    public async Task A_master_that_moves_between_the_reissue_and_the_wait_is_not_waited_on()
    {
        // Attempt 0 identifies once (WAIT gives 0). Attempt 1 identifies before and after the reissue; the second answer differs. Attempt 2 is stable.
        var fake = new Fake { Wait = call => Task.FromResult(call == 0 ? 0L : 1L), Identify = call => call == 2 ? B : A };

        (await fake.Barrier().WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Reissues.Should().Be(2);
        fake.Waits.Should().Be(2, "the attempt that saw the master move did not wait");
    }

    [Fact]
    public async Task A_wait_that_times_out_on_the_client_is_retried()
    {
        var fake = new Fake { Wait = call => call == 0 ? Task.FromException<long>(new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent)) : Task.FromResult(1L) };

        (await fake.Barrier().WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Reissues.Should().Be(1);
    }

    [Fact]
    public async Task An_endpoint_that_cannot_be_resolved_is_not_confirmed_and_never_waited_on()
    {
        var unresolved = new Fake { Identify = _ => null };

        (await unresolved.Barrier(retries: 1, replicaFree: true).WaitAsync(A, unresolved.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        unresolved.Waits.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_role_that_is_denied_or_fails_leaves_the_replicas_unknown_and_wait_decides(bool replicaFree)
    {
        Task<Role> Denied(int _) => Task.FromException<Role>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User u has no permissions to run the 'role' command"));
        Task<Role> Failed(int _) => Task.FromException<Role>(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "gone", null, CommandStatus.Unknown));

        foreach (var role in new[] { Denied, Failed })
        {
            var confirming = new Fake { Role = role };
            var empty = new Fake { Role = role, Wait = _ => Task.FromResult(0L) };
            var failing = new Fake { Role = role, Wait = _ => Task.FromException<long>(new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent)) };

            (await confirming.Barrier(replicaFree: replicaFree).WaitAsync(A, confirming.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
            confirming.Waits.Should().Be(1);
            (await empty.Barrier(retries: 1, replicaFree).WaitAsync(A, empty.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed, "0 is not a confirmation and never NoReplica");
            empty.Waits.Should().Be(2);
            (await failing.Barrier(retries: 1, replicaFree).WaitAsync(A, failing.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        }
    }

    [Fact]
    public async Task A_write_that_went_to_a_master_that_is_no_longer_the_owner_is_not_confirmed_at_attempt_0()
    {
        // The old master took the write, then rejoined as a replica: it now answers ROLE with one replica and WAIT with 1, for a write the new master lacks.
        var fake = new Fake { Identify = call => call == 0 ? B : A };

        (await fake.Barrier().WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Reissues.Should().Be(1, "attempt 0 was not confirmed, so the writes were sent again to the new owner");
        fake.Waits.Should().Be(1, "only the attempt whose three endpoints agree waited");

        var stuck = new Fake { Identify = _ => B };
        (await stuck.Barrier(retries: 0).WaitAsync(A, stuck.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        stuck.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_write_to_an_unknown_endpoint_is_not_confirmed_at_attempt_0()
    {
        var fake = new Fake();

        (await fake.Barrier(retries: 0).WaitAsync(wroteTo: null, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task The_target_is_the_owners_master_endpoint_and_null_when_it_cannot_be_resolved()
    {
        (await new Fake().Barrier().TargetAsync()).Should().Be(A);
        (await new Fake { Identify = _ => null }.Barrier().TargetAsync()).Should().BeNull();
        (await new Fake { Identify = _ => throw new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent) }.Barrier().TargetAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("a:6379", 4000)]
    [InlineData("a:6379,syncTimeout=2000", 1600)]
    [InlineData("a:6379,asyncTimeout=10000", 8000)]
    [InlineData("a:6379,syncTimeout=2000,asyncTimeout=1000", 800)]
    public async Task The_default_timeout_is_eighty_percent_of_the_connections_async_timeout(string configuration, long expected)
    {
        var fake = new Fake { Configuration = configuration };
        var barrier = fake.Barrier();

        barrier.Timeout.Should().Be(TimeSpan.FromMilliseconds(expected));
        (await barrier.WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.WaitTimeouts.Should().Equal(expected);
    }

    [Fact]
    public void An_explicit_timeout_overrides_the_derived_one()
    {
        var fake = new Fake { Configuration = "a:6379,asyncTimeout=10000" };

        fake.Barrier(timeout: TimeSpan.FromMilliseconds(200)).Timeout.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task A_way_back_skips_a_master_that_failed_within_the_episode()
    {
        var fake = new Fake { Wait = _ => Task.FromResult(0L) };
        var barrier = fake.Barrier(retries: 1);

        (await barrier.WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        var waits = fake.Waits;

        (await barrier.WaitAsync(A, fake.Reissue, wayBack: true)).Should().Be(BarrierOutcome.SkippedAfterRecentFailure);
        fake.Waits.Should().Be(waits);
    }

    [Fact]
    public async Task A_reissue_that_fails_in_a_failover_is_not_confirmed_never_an_exception()
    {
        var fake = new Fake { Wait = _ => Task.FromResult(0L), ReissueFails = true };

        (await fake.Barrier(retries: 2).WaitAsync(A, fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Reissues.Should().Be(2);
        fake.Waits.Should().Be(1, "a reissue that failed wrote nothing to wait for");
    }

    /// <summary>A database, multiplexer and server whose endpoint, role and WAIT answer from delegates, each given its call number.</summary>
    private sealed class Fake
    {
        private int _identifies;
        private int _roles;

        public Func<int, EndPoint?> Identify { get; init; } = _ => A;

        public string Configuration { get; init; } = "a:6379";

        public List<object?> WaitTimeouts { get; } = [];

        public Func<int, Task<Role>> Role { get; init; } = _ => Task.FromResult(FakeRoles.Master(1));

        public Func<int, Task<long>> Wait { get; init; } = _ => Task.FromResult(1L);

        public int Waits { get; private set; }

        public int Reissues { get; private set; }

        public bool ReissueFails { get; init; }

        public Task Reissue()
        {
            Reissues++;
            return ReissueFails ? Task.FromException(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "failover", null, CommandStatus.Unknown)) : Task.CompletedTask;
        }

        public ServerBarrier Barrier(int retries = 3, bool replicaFree = false, TimeSpan? timeout = null)
        {
            var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, args) =>
                method.Name switch
                {
                    nameof(IConnectionMultiplexer.GetServer) when args[0] is EndPoint endpoint => Server(endpoint),
                    "get_Configuration" => Configuration,
                    _ => throw new NotSupportedException(method.Name),
                });
            var db = InterceptingProxy.Wrap<IDatabase>(null!, (method, _) => method.Name switch
            {
                "get_Multiplexer" => mux,
                nameof(IDatabase.IdentifyEndpointAsync) => Task.FromResult(Identify(_identifies++)),
                _ => throw new NotSupportedException(method.Name),
            });
            return timeout is { } explicitTimeout
                ? new ServerBarrier(db, "barriertest:{fake}", TimeProvider.System, replicaFree) { Retries = retries, Timeout = explicitTimeout }
                : new ServerBarrier(db, "barriertest:{fake}", TimeProvider.System, replicaFree) { Retries = retries };
        }

        private static async Task<RedisResult> AsResult(Task<long> value, List<object?> timeouts, object? timeout)
        {
            timeouts.Add(timeout);
            return RedisResult.Create(await value.ConfigureAwait(false));
        }

        private IServer Server(EndPoint endpoint) => InterceptingProxy.Wrap<IServer>(null!, (method, args) => method.Name switch
        {
            "get_EndPoint" => endpoint,
            nameof(IServer.RoleAsync) => Role(_roles++),
            nameof(IServer.ExecuteAsync) => AsResult(Wait(Waits++), WaitTimeouts, ((object[])args[1]!)[1]),
            _ => throw new NotSupportedException(method.Name),
        });
    }
}
