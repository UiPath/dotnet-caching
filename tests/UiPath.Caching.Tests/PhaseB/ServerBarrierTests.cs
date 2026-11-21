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

        (await fake.Barrier().WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Reissues.Should().Be(0);
        fake.Waits.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_demoted_master_is_retried_and_never_reported_as_having_no_replica(bool replicaFree)
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Unknown()) };

        (await fake.Barrier(retries: 2, replicaFree).WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Waits.Should().Be(0);
        fake.Reissues.Should().Be(2);
    }

    [Fact]
    public async Task No_replica_with_replicas_expected_is_not_confirmed_after_the_retries()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)) };

        (await fake.Barrier(retries: 3, replicaFree: false).WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Reissues.Should().Be(3, "the reissue count equals the retries");
        fake.Waits.Should().Be(0);
    }

    [Fact]
    public async Task No_replica_in_a_replica_free_deployment_needs_no_wait()
    {
        var fake = new Fake { Role = _ => Task.FromResult(FakeRoles.Master(0)) };

        (await fake.Barrier(replicaFree: true).WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NoReplica);
        fake.Waits.Should().Be(0);
        fake.Reissues.Should().Be(0);
    }

    [Fact]
    public async Task Replicas_seen_and_then_none_are_not_confirmed_even_when_replica_free()
    {
        var fake = new Fake { Role = call => Task.FromResult(FakeRoles.Master(call == 0 ? 1 : 0)), Wait = _ => Task.FromResult(0L) };

        (await fake.Barrier(retries: 2, replicaFree: true).WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Waits.Should().Be(1);
    }

    [Fact]
    public async Task A_master_that_moves_between_the_reissue_and_the_wait_is_not_waited_on()
    {
        // Attempt 0 identifies once (WAIT gives 0). Attempt 1 identifies before and after the reissue; the second answer differs. Attempt 2 is stable.
        var fake = new Fake { Wait = call => Task.FromResult(call == 0 ? 0L : 1L), Identify = call => call == 2 ? B : A };

        (await fake.Barrier().WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Reissues.Should().Be(2);
        fake.Waits.Should().Be(2, "the attempt that saw the master move did not wait");
    }

    [Fact]
    public async Task A_wait_that_times_out_on_the_client_is_retried()
    {
        var fake = new Fake { Wait = call => call == 0 ? Task.FromException<long>(new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent)) : Task.FromResult(1L) };

        (await fake.Barrier().WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
        fake.Reissues.Should().Be(1);
    }

    [Fact]
    public async Task A_role_that_cannot_be_read_or_an_endpoint_that_cannot_be_resolved_is_not_confirmed()
    {
        var failing = new Fake { Role = _ => Task.FromException<Role>(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "gone", null, CommandStatus.Unknown)) };
        var unresolved = new Fake { Identify = _ => null };

        (await failing.Barrier(retries: 1, replicaFree: true).WaitAsync(failing.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        (await unresolved.Barrier(retries: 1, replicaFree: true).WaitAsync(unresolved.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        failing.Waits.Should().Be(0);
        unresolved.Waits.Should().Be(0);
    }

    [Fact]
    public async Task A_way_back_skips_a_master_that_failed_within_the_episode()
    {
        var fake = new Fake { Wait = _ => Task.FromResult(0L) };
        var barrier = fake.Barrier(retries: 1);

        (await barrier.WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        var waits = fake.Waits;

        (await barrier.WaitAsync(fake.Reissue, wayBack: true)).Should().Be(BarrierOutcome.SkippedAfterRecentFailure);
        fake.Waits.Should().Be(waits);
    }

    /// <summary>A database, multiplexer and server whose endpoint, role and WAIT answer from delegates, each given its call number.</summary>
    [Fact]
    public async Task A_reissue_that_fails_in_a_failover_is_not_confirmed_never_an_exception()
    {
        var fake = new Fake { Wait = _ => Task.FromResult(0L), ReissueFails = true };

        (await fake.Barrier(retries: 2).WaitAsync(fake.Reissue, wayBack: false)).Should().Be(BarrierOutcome.NotConfirmed);
        fake.Reissues.Should().Be(2);
        fake.Waits.Should().Be(1, "a reissue that failed wrote nothing to wait for");
    }

    private sealed class Fake
    {
        private int _identifies;
        private int _roles;

        public Func<int, EndPoint?> Identify { get; init; } = _ => A;

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

        public ServerBarrier Barrier(int retries = 3, bool replicaFree = false)
        {
            var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, args) =>
                method.Name == nameof(IConnectionMultiplexer.GetServer) && args[0] is EndPoint endpoint ? Server(endpoint) : throw new NotSupportedException(method.Name));
            var db = InterceptingProxy.Wrap<IDatabase>(null!, (method, _) => method.Name switch
            {
                "get_Multiplexer" => mux,
                nameof(IDatabase.IdentifyEndpointAsync) => Task.FromResult(Identify(_identifies++)),
                _ => throw new NotSupportedException(method.Name),
            });
            return new ServerBarrier(db, "barriertest:{fake}", TimeProvider.System, replicaFree) { Retries = retries };
        }

        private static async Task<RedisResult> AsResult(Task<long> value) => RedisResult.Create(await value.ConfigureAwait(false));

        private IServer Server(EndPoint endpoint) => InterceptingProxy.Wrap<IServer>(null!, (method, _) => method.Name switch
        {
            "get_EndPoint" => endpoint,
            nameof(IServer.RoleAsync) => Role(_roles++),
            nameof(IServer.ExecuteAsync) => AsResult(Wait(Waits++)),
            _ => throw new NotSupportedException(method.Name),
        });
    }
}
