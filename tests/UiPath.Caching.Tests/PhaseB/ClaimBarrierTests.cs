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

    private static async Task<RedisResult> AsResult(Task<long> value) => RedisResult.Create(await value.ConfigureAwait(false));

    /// <summary>A database whose scripts, endpoint, role and WAIT answer from the delegates, each given its call number.</summary>
    private sealed class Fake
    {
        public const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private int _identifies;
        private int _roles;

        public Func<int, EndPoint> Identify { get; init; } = _ => A;

        public Func<int, Task<Role>> Role { get; init; } = _ => Task.FromResult(FakeRoles.Master(1));

        public Func<int, Task<long>> Wait { get; init; } = _ => Task.FromResult(1L);

        public Func<int, Task<long>> Step { get; init; } = call => Task.FromResult(call == 0 ? 1L : 2L);

        public int Steps { get; private set; }

        public int Waits { get; private set; }

        public ClaimStore Store(int retries = 3)
        {
            var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, args) =>
                method.Name == nameof(IConnectionMultiplexer.GetServer) && args[0] is EndPoint endpoint ? Server(endpoint) : throw new NotSupportedException(method.Name));
            var db = InterceptingProxy.Wrap<IDatabase>(null!, (method, args) => method.Name switch
            {
                "get_Multiplexer" => mux,
                nameof(IDatabase.IdentifyEndpointAsync) => Task.FromResult<EndPoint?>(Identify(_identifies++)),
                nameof(IDatabase.ScriptEvaluateAsync) => AsResult(Step(Steps++)),
                _ => throw new NotSupportedException(method.Name),
            });
            return new ClaimStore(db, "claimtest:mx:{fake}", useScripts: true, new Version(7, 4)) { BarrierRetries = retries };
        }

        private IServer Server(EndPoint endpoint) => InterceptingProxy.Wrap<IServer>(null!, (method, _) => method.Name switch
        {
            "get_EndPoint" => endpoint,
            nameof(IServer.RoleAsync) => Role(_roles++),
            nameof(IServer.ExecuteAsync) => AsResult(Wait(Waits++)),
            _ => throw new NotSupportedException(method.Name),
        });
    }
}
