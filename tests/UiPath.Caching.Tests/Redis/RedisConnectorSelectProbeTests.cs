using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectorSelectProbeTests
{
    [Fact]
    public async Task Probe_IsSkipped_WhenDatabaseIsZero()
    {
        var server = new Server(selected: 0, ignoresSelect: true);

        await server.RunAsync();

        server.Written.Should().BeEmpty();
        server.Log.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_IsSkipped_WhenDatabaseIsTheDefault()
    {
        var server = new Server(selected: -1, ignoresSelect: true);

        await server.RunAsync();

        server.Written.Should().BeEmpty();
        server.Log.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_IsSkipped_OnACluster()
    {
        var server = new Server(selected: 3, ignoresSelect: true, cluster: true);

        await server.RunAsync();

        server.Written.Should().BeEmpty();
        server.Log.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_LogsNothing_WhenTheServerKeepsDatabasesApart()
    {
        var server = new Server(selected: 3, ignoresSelect: false);

        await server.RunAsync();

        server.Written.Should().ContainSingle();
        server.Log.Records.Should().BeEmpty();
        server.Deleted.Should().Contain(3);
    }

    [Fact]
    public async Task Probe_LogsOneError_WhenTheServerIgnoresSelect()
    {
        var server = new Server(selected: 3, ignoresSelect: true);

        await server.RunAsync();

        server.Log.Records.Should().ContainSingle(record => record.Level == LogLevel.Error && record.Message.Contains("database 3 shares its keys with database 0"));
        server.Deleted.Should().Contain(3).And.Contain(0);
    }

    [Fact]
    public async Task Probe_LogsDebugOnly_WhenTheWriteIsRefused()
    {
        var server = new Server(selected: 3, ignoresSelect: false, writeFault: new InvalidOperationException("ERR invalid database index"));

        await server.RunAsync();

        server.Log.Records.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task Probe_LogsDebugOnly_WhenTheCheckTimesOut()
    {
        var server = new Server(selected: 3, ignoresSelect: true, existsFault: new TimeoutException("timeout"));

        await server.RunAsync();

        server.Log.Records.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task Probe_RunsOnce_AcrossReconnectsOfTheConnector()
    {
        var server = new Server(selected: 3, ignoresSelect: false);
        var connector = server.NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        await connector.SelectProbe!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var reconnected = new TaskCompletionSource();
        connector.OnReconnected += (_, _) => reconnected.TrySetResult();

        connector.ForceReconnect();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        connector.Dispose();

        server.Written.Should().ContainSingle();
    }

    [Fact]
    public async Task Probe_DoesNotThrow_WhenTheConnectorIsDisposedDuringIt()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server(selected: 3, ignoresSelect: true, writeGate: gate.Task);
        var connector = server.NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        await server.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connector.Dispose();
        gate.SetResult(true);
        await connector.SelectProbe!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        server.Log.Records.Should().NotContain(record => record.Level >= LogLevel.Warning);
    }

    private sealed class Server
    {
        private readonly IConnectionMultiplexer _multiplexer = Substitute.For<IConnectionMultiplexer>();
        private readonly object _gate = new();
        private readonly Dictionary<int, HashSet<string>> _keys = [];

        public Server(int selected, bool ignoresSelect, bool cluster = false, Exception? writeFault = null, Exception? existsFault = null, Task? writeGate = null)
        {
            _multiplexer.GetEndPoints(Arg.Any<bool>()).Returns([new System.Net.DnsEndPoint("localhost", 6379)]);
            var redisServer = Substitute.For<IServer>();
            redisServer.ServerType.Returns(cluster ? ServerType.Cluster : ServerType.Standalone);
            _multiplexer.GetServer(Arg.Any<System.Net.EndPoint>(), Arg.Any<object>()).Returns(redisServer);
            _multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(call =>
            {
                var requested = call.ArgAt<int>(0);
                var database = requested == -1 ? selected : requested;
                return NewDatabase(database, ignoresSelect ? 0 : database, writeFault, existsFault, writeGate);
            });
        }

        public RecordedLogger Log { get; } = new();

        public List<int> Written { get; } = [];

        public List<int> Deleted { get; } = [];

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RedisConnector NewConnector()
        {
            var options = Options.Create(new RedisConnectionOptions { ConnectionString = "localhost:6379", EnableHangDetection = false });
            var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
            return new RedisConnector(NullTelemetryProvider.Instance, optionsProvider, new Factory(_multiplexer), options, null, null, null, Log);
        }

        public async Task RunAsync()
        {
            using var connector = NewConnector();
            await connector.ConnectAsync(TestContext.Current.CancellationToken);
            await connector.SelectProbe!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        private IDatabase NewDatabase(int reported, int keyspace, Exception? writeFault, Exception? existsFault, Task? writeGate)
        {
            var database = Substitute.For<IDatabase>();
            database.Database.Returns(reported);
            database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(async call =>
            {
                WriteStarted.TrySetResult();
                if (writeGate is not null)
                {
                    await writeGate;
                }

                if (writeFault is not null)
                {
                    throw writeFault;
                }

                lock (_gate)
                {
                    Written.Add(reported);
                    (_keys.TryGetValue(keyspace, out var set) ? set : _keys[keyspace] = []).Add(call.ArgAt<RedisKey>(0).ToString());
                }

                return true;
            });
            database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(call =>
            {
                if (existsFault is not null)
                {
                    throw existsFault;
                }

                lock (_gate)
                {
                    return Task.FromResult(_keys.TryGetValue(keyspace, out var set) && set.Contains(call.ArgAt<RedisKey>(0).ToString()));
                }
            });
            database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(call =>
            {
                lock (_gate)
                {
                    Deleted.Add(reported);
                    return Task.FromResult(_keys.TryGetValue(keyspace, out var set) && set.Remove(call.ArgAt<RedisKey>(0).ToString()));
                }
            });
            return database;
        }
    }

    private sealed class Factory(IConnectionMultiplexer multiplexer) : IConnectionMultiplexerFactory
    {
        public ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default) => new(multiplexer);
    }
}
