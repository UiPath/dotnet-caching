using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

[Collection("RedisIntegration")]
public sealed class SettleBoundDiTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_settle_bound_does_not_wait_for_a_connection_that_is_still_being_made()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var provider = Build("127.0.0.1:1,connectTimeout=20000", "asyncTimeout=60000", services =>
            services.PostConfigure<RedisConnectionOptions>(options => options.ConnectionFactory = (_, token) =>
            {
                entered.TrySetResult();
                return new ValueTask<IConnectionMultiplexer>(pending.Task.WaitAsync(token));
            }));
        var connector = provider.GetRequiredService<IRedisConnector>();
        var connecting = connector.ConnectAsync(cancel.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(Ct);
            var bound = RouterServiceCollectionExtensions.LibrarySettleWithin(provider, null);
            var read = Task.Run(() => bound(), Ct);

            (await read.WaitAsync(Ct)).Should().BeGreaterThanOrEqualTo(LibrarySettleBound.Ceiling, "nothing is known yet, so the largest bound is taken");
            connecting.IsCompleted.Should().BeFalse("the lookup must not block on the connect");
        }
        finally
        {
            await cancel.CancelAsync();
            pending.TrySetCanceled(cancel.Token);
        }
    }

    [Fact]
    public async Task The_settle_bound_keeps_what_a_configurator_set_after_the_connection_is_gone()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        await using var provider = Build(fixture.ConnectionString, null, services => services.PostConfigure<RedisConnectionOptions>(options =>
            options.ConnectionFactory = async (configuration, token) =>
            {
                configuration.AsyncTimeout = 45000;
                return await ConnectionMultiplexer.ConnectAsync(configuration);
            }));
        var connector = provider.GetRequiredService<IRedisConnector>();
        await connector.ConnectAsync(Ct);
        var bound = RouterServiceCollectionExtensions.LibrarySettleWithin(provider, null);

        bound().Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(46), "the multiplexer's own configuration, which the connection string does not show");
        connector.Dispose();

        bound().Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(46), "a disconnected connector does not bring back the shorter configured value");
    }

    [Fact]
    public void A_connection_timeout_beyond_the_horizon_fails_the_router_at_startup()
    {
        using var provider = Build("127.0.0.1:1", "asyncTimeout=100000000", null);

        var create = () => provider.GetRequiredService<IRouter>();

        create.Should().Throw<InvalidOperationException>().WithMessage("*longer than the 24 hours*");
    }

    private static ServiceProvider Build(string connectionString, string? extraParams, Action<IServiceCollection>? more)
    {
        var app = $"sb{Guid.NewGuid():N}"[..10];
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = app,
            ["Caching:SourceUri"] = $"urn:settle-{app}",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = connectionString,
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
        };
        if (extraParams is not null)
        {
            settings["Caching:Connections:Redis:ConnectionStringExtraParams"] = extraParams;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(
            configuration,
            router => router
                .Add(new RouterCacheRegistration<Item>
                {
                    Name = "items",
                    Group = "g",
                    Source = source,
                    RedisKey = key => key,
                })
                .UseLibraryWhen(_ => false)
                .UseEnvelope(new LegacyEnvelopeProxy()));
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
