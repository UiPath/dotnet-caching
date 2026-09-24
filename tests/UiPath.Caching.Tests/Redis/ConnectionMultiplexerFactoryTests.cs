using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

public class ConnectionMultiplexerFactoryTests
{
    [Fact]
    public async Task A_supplied_factory_is_awaited_rather_than_blocked_on()
    {
        var connected = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = Create(new RedisConnectionOptions { ConnectionFactory = (_, _) => new ValueTask<IConnectionMultiplexer>(connected.Task) });

        var pending = sut.CreateAsync(new ConfigurationOptions(), TestContext.Current.CancellationToken);

        pending.IsCompleted.Should().BeFalse("the factory has not finished connecting");
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        connected.SetResult(multiplexer);
        (await pending).Should().BeSameAs(multiplexer);
    }

    [Fact]
    public async Task A_supplied_factory_gets_the_options_and_the_token()
    {
        var configuration = new ConfigurationOptions();
        using var cancellation = new CancellationTokenSource();
        (ConfigurationOptions Options, CancellationToken Token)? seen = null;
        var sut = Create(new RedisConnectionOptions
        {
            ConnectionFactory = (options, token) =>
            {
                seen = (options, token);
                return new ValueTask<IConnectionMultiplexer>(Substitute.For<IConnectionMultiplexer>());
            },
        });

        await sut.CreateAsync(configuration, cancellation.Token);

        seen!.Value.Options.Should().BeSameAs(configuration);
        seen.Value.Token.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task The_profiler_is_registered_on_a_supplied_factory_connection()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var sut = Create(new RedisConnectionOptions
        {
            ProfilerEnabled = true,
            ConnectionFactory = (_, _) => new ValueTask<IConnectionMultiplexer>(multiplexer),
        });

        await sut.CreateAsync(new ConfigurationOptions(), TestContext.Current.CancellationToken);

        multiplexer.Received(1).RegisterProfiler(Arg.Any<Func<ProfilingSession?>>());
    }

    private static ConnectionMultiplexerFactory Create(RedisConnectionOptions options) =>
        new(Options.Create(options), Substitute.For<IRedisProfiler>());
}
