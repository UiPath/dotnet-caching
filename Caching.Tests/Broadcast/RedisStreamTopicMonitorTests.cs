using System.Reflection;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests;
public class RedisStreamHealthMaintainerTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private IRedisConnector _redisConnector = default!;
    private IDatabase _database = default!;
    private ICachingTelemetryProvider _telemetryProvider = default!;
    private RedisStreamsTopicOptions _streamOptions = default!;
    private RedisCacheOptions _redisCacheOptions = default!;
    private CacheOptions _cacheOptions = default!;
    private ILogger<RedisStreamHealthMaintainer> _logger = default!;
    private string _lastGeneratedId = default!;
    private StreamGroupInfo[] _streamGroupInfos = [];
    private StreamConsumerInfo[] _streamConsumerInfos = [];
    private RedisStreamHealthMaintainer? _sut;
    private bool _ownLock = true;
    private RedisValue _quarantineValue = RedisValue.Null;
    private bool _successStreamDeleteConsumerGroup = true; 
    private RedisValue _clientsResponse = @"""
id=18734367 addr=40.125.125.100:5120 fd=14 name=automation-solutions-service-primary-7c5445785d-rrrq2 age=3970 idle=1 flags=N db=0 sub=0 psub=0 multi=-1 qbuf=0 qbuf-free=0 argv-mem=0 obl=0 oll=0 omem=0 tot-mem=20496 ow=0 owmem=0 events=r cmd=ping user=default lib-name=SE.Redis lib-ver=2.7.33.41805 numops=3981

id=18750907 addr=20.62.229.253:30725 fd=48 name=automation-solutions-service-primary-659df94c58-rt78s age=2887 idle=1 flags=N db=0 sub=0 psub=0 multi=-1 qbuf=0 qbuf-free=0 argv-mem=0 obl=0 oll=0 omem=0 tot-mem=20496 ow=0 owmem=0 events=r cmd=ping user=default lib-name=SE.Redis lib-ver=2.7.33.41805 numops=2898
id=18750908 addr=20.62.229.253:30726 fd=47 name=automation-solutions-service-primary-659df94c58-rt78s age=2887 idle=16 flags=P db=0 sub=2 psub=0 multi=-1 qbuf=0 qbuf-free=0 argv-mem=0 obl=0 oll=0 omem=0 tot-mem=20496 ow=0 owmem=0 events=r cmd=ping user=default lib-name=SE.Redis lib-ver=2.7.33.41805 numops=104
""";

    private RedisValue[] _streams = ["stream1", "stream2"];

    private RedisStreamHealthMaintainer Sut => _sut ??= new RedisStreamHealthMaintainer(
        _redisConnector,
        _telemetryProvider,
        Options.Create(_streamOptions),
        Options.Create(_redisCacheOptions),
        Options.Create(_cacheOptions),
        _logger);

    [Fact]
    public async Task Works_as_expected_when_locked()
    {
        _ownLock = false;
        await Sut.CheckStreamsAsync(CancellationToken.None);
        await _database.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<object>());
        _telemetryProvider.DidNotReceive().TrackMetric(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<IDictionary<string, string>?>());
    }

    [Fact]
    public async Task Works_as_expected_when_no_lock()
    {
        Sut.Initialize();
        await Sut.CheckStreamsAsync(CancellationToken.None);
        await _database.Received().ExecuteAsync(Arg.Any<string>(), Arg.Any<object>());
        _telemetryProvider.Received().TrackMetric(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<IDictionary<string, string>?>());
    }

    [Fact]
    public async Task DeleteStreamsWithNoConsumerGroups()
    {
        Sut.Initialize();
        await Sut.CheckStreamsAsync(CancellationToken.None);
        await _database.Received().ExecuteAsync(Arg.Is<string>(s => s == "DEL"), Arg.Any<ICollection<object>?>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().StreamGroupInfoAsync(Arg.Any<RedisKey>(), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Quarantine_Unknown_consumer_group()
    {
        var g1 = GenerateGroupInfo(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMilliseconds(10)), 0);
        _streamGroupInfos = [g1];
        Sut.Initialize();
        await Sut.CheckStreamsAsync(CancellationToken.None);
        await _database.Received().HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Consumer_group_delete()
    {
        var g1 = GenerateGroupInfo(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMilliseconds(10)), 0);
        _streamGroupInfos = [g1];
        _quarantineValue = DateTimeOffset.UtcNow.Subtract(_streamOptions.MaintainerQuarantineInterval).Subtract(TimeSpan.FromMilliseconds(10)).ToString("O");
        Sut.Initialize();
        await Sut.CheckStreamsAsync(CancellationToken.None);
        await _database.Received().StreamDeleteConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
        await _database.Received().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Consumer_group_info()
    {
        var g1 = GenerateGroupInfo(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMilliseconds(10)), 1);
        _streamGroupInfos = [g1];
        var c1 = GenerateConsumerInfo();
        _streamConsumerInfos = [c1];
        Sut.Initialize();
        await Sut.CheckStreamsAsync(CancellationToken.None);
        await _database.DidNotReceive().StreamDeleteConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
        await _database.Received().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
        await _database.DidNotReceive().ExecuteAsync(Arg.Is<string>(s => s == "XTRIM"), Arg.Any<ICollection<object>?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Trim_stream()
    {
        var g1 = GenerateGroupInfo(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMilliseconds(10)), 1);
        _streamGroupInfos = [g1];
        var c1 = GenerateConsumerInfo();
        _streamConsumerInfos = [c1];
        _redisConnector.Version.Returns(Version.Parse("7.0.0"));
        Sut.Initialize();
        await Sut.CheckStreamsAsync(CancellationToken.None);
        await _database.DidNotReceive().StreamDeleteConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
        await _database.Received().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
        await _database.Received().ExecuteAsync(Arg.Is<string>(s => s == "XTRIM"), Arg.Any<ICollection<object>?>(), Arg.Any<CommandFlags>());
    }



    [Fact]
    public async Task OnException()
    {
        _database.ClearReceivedCalls();
        _database.StreamInfoAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new Exception());

        Func<Task> act = async () => await Sut.CheckStreamsAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }


    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _redisConnector = _fixture.Freeze<IRedisConnector>();
        _database = _fixture.Freeze<IDatabase>();
        _redisConnector.Database.Returns(_database);

        _telemetryProvider = _fixture.Freeze<ICachingTelemetryProvider>();
        _streamOptions = new RedisStreamsTopicOptions
        {
            TrackStatistics = true,
            MaintainerEnabled = true,
            MaintainerCheckInterval = TimeSpan.FromMilliseconds(100)
        };
        _redisCacheOptions = new RedisCacheOptions
        {
            Enabled = true,
        };
        _cacheOptions = new CacheOptions
        {
            Enabled = true,
            AppShortName = "tst",
        };
        _logger = _fixture.Freeze<ILogger<RedisStreamHealthMaintainer>>();
        _lastGeneratedId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-0";

        _database.StreamInfoAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(c => GenerateStreamInfo(_streamGroupInfos.Length));
        
        _database.StreamGroupInfoAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(c => _streamGroupInfos);

        _database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(c => _ownLock);

        _database.ExecuteAsync(Arg.Is<string>(s => s == "CLIENT"), Arg.Is<object[]>(a => a.OfType<string>().FirstOrDefault() == "LIST"))
            .Returns(c => RedisResult.Create(_clientsResponse));
        _database.ExecuteAsync(Arg.Is<string>(s => s == "SCAN"), Arg.Any<ICollection<object>?>(), Arg.Any<CommandFlags>())
            .Returns(c =>
            {
                var entry1 = RedisResult.Create((RedisValue)0);
                var entry2 = RedisResult.Create(_streams);
                return RedisResult.Create([entry1, entry2]);
            });

        _database.ExecuteAsync(Arg.Is<string>(s => s == "DEL"), Arg.Any<ICollection<object>?>(), Arg.Any<CommandFlags>())
            .Returns(c => RedisResult.Create(true));

        _database.ExecuteAsync(Arg.Is<string>(s => s == "XTRIM"), Arg.Any<ICollection<object>?>(), Arg.Any<CommandFlags>())
            .Returns(c => RedisResult.Create(_fixture.Create<ulong>()));

        _database.HashGetAsync(Arg.Is<RedisKey>(k => k.ToString().Contains("quarantine", StringComparison.OrdinalIgnoreCase)), Arg.Any<RedisValue>(), CommandFlags.PreferReplica)
            .Returns(c => _quarantineValue);

        _database.StreamDeleteConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(c => _successStreamDeleteConsumerGroup);

        _database.StreamConsumerInfoAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(c => _streamConsumerInfos);

        return Task.CompletedTask;
    }

    private StreamInfo GenerateStreamInfo(int? groupsCount = null)
    {
        //int length, int radixTreeKeys, int radixTreeNodes, int groups, StreamEntry firstEntry, StreamEntry lastEntry, RedisValue lastGeneratedId
        object?[] args = [
               (object?)_fixture.Create<int>(),
                       (object?)_fixture.Create<int>(),
                       (object?)_fixture.Create<int>(),
                       (object?) groupsCount ?? _fixture.Create<int>(),
                       (object?)_fixture.Create<StreamEntry>(),
                       (object?)_fixture.Create<StreamEntry>(),
                       (object?)(RedisValue)_lastGeneratedId
            ];

        var streamInfo = (StreamInfo)Activator.CreateInstance(typeof(StreamInfo), BindingFlags.Instance | BindingFlags.NonPublic, null, args, null)!;
        return streamInfo;
    }

    private StreamGroupInfo GenerateGroupInfo(DateTimeOffset lastGenerated, int? consumerCount = null)
    {
        /// string name, int consumerCount, int pendingMessageCount, string? lastDeliveredId, long? entriesRead, long? lag)
        object?[] args = [
               (object?)_fixture.Create<string>(),
           (object?) consumerCount ?? _fixture.Create<int>(),
           (object?)_fixture.Create<int>(),
           (object?)$"{lastGenerated.ToUnixTimeMilliseconds()}-0",
           (object?)_fixture.Create<long?>(),
           (object?)_fixture.Create<long?>()];
        return (StreamGroupInfo)Activator.CreateInstance(typeof(StreamGroupInfo), BindingFlags.Instance | BindingFlags.NonPublic, null, args, null)!;
    }

    private StreamConsumerInfo GenerateConsumerInfo()
    {
        /// string name, int pendingMessageCount, long idleTimeInMilliseconds
        object?[] args = [
               (object?)_fixture.Create<string>(),
           (object?)_fixture.Create<int>(),
           (object?)_fixture.Create<long>()];
        return (StreamConsumerInfo)Activator.CreateInstance(typeof(StreamConsumerInfo), BindingFlags.Instance | BindingFlags.NonPublic, null, args, null)!;
    }

}
