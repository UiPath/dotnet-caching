using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Tests;

public class RedisProfilerTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private RedisProfiler? _sut = null;
    private ISystemClock _clock = default!;
    private DateTimeOffset _now;
    private IProfiledCommandProcessor _profiledCommandProcessor = default!;
    private IProfilingSessionCommandReader _profilingSessionCommandReader = default!;
    private ILogger<RedisProfiler> _logger = default!;
    private RedisConnectionOptions _redisConnectionOptions = default!;

    private RedisProfiler Sut => _sut ??= _fixture.Create<RedisProfiler>();


    [Fact]
    public void Profiler_with_default_session()
    {
        _redisConnectionOptions.ProfilerHasDefaultSession = true;
        var sessionId = _fixture.Create<string>();
        var disposableAction = Sut.CreateSession(sessionId);
        Sut.Count.Should().Be(1);
        var session = Sut.GetSession();
        sessionId.Should().Be(session?.UserToken?.ToString());
        disposableAction.Dispose();
        session = Sut.GetSession();
        "default".Should().Be(session?.UserToken?.ToString());
        Sut.Count.Should().Be(0);
    }

    [Fact]
    public void Profiler_with_no_default_session()
    {
        _redisConnectionOptions.ProfilerHasDefaultSession = false;
        var sessionId = _fixture.Create<string>();
        var disposableAction = Sut.CreateSession(sessionId);
        Sut.Count.Should().Be(1);
        var session = Sut.GetSession();
        sessionId.Should().Be(session?.UserToken?.ToString());
        disposableAction.Dispose();
         session = Sut.GetSession();
        session.Should().BeNull();
        Sut.Count.Should().Be(0);
    }

    [Fact]
    public void Dispose_works_as_expected()
    {
        var sessionId = _fixture.Create<string>();
        using (Sut.CreateSession(sessionId))
        {
            Sut.Dispose();
            Sut.GetSession().Should().BeNull();
        }
    }

    [Fact]
    public async Task Remove_old_sessions_Lifespan()
    {
        _redisConnectionOptions.ProfilerSessionMaxLifespan = _redisConnectionOptions.ProfilerFlushInterval.Multiply(2);
        _redisConnectionOptions.ProfilerSessionMaxChecks = null;
        _redisConnectionOptions.ProfilerHasDefaultSession = false;
        var sessionId = _fixture.Create<string>();
        using (Sut.CreateSession(sessionId))
        {
            for(int i = 0; i < 100; i++)
            {
                _now = _now.Add(_redisConnectionOptions.ProfilerFlushInterval).AddSeconds(100);
                if(Sut.Count == 0)
                {
                    break;
                }
                await Task.Delay(_redisConnectionOptions.ProfilerFlushInterval);
            }
            Sut.GetSession().Should().BeNull();
        }
    }

    [Fact]
    public async Task Remove_old_sessions_MaxChecks()
    {
        _redisConnectionOptions.ProfilerSessionMaxLifespan = null;
        _redisConnectionOptions.ProfilerSessionMaxChecks = 2;
        _redisConnectionOptions.ProfilerHasDefaultSession = false;
        var sessionId = _fixture.Create<string>();
        using (Sut.CreateSession(sessionId))
        {
            for (int i = 0; i < 100; i++)
            {
                _now = _now.Add(_redisConnectionOptions.ProfilerFlushInterval).AddSeconds(100);
                if (Sut.Count == 0)
                {
                    break;
                }
                await Task.Delay(_redisConnectionOptions.ProfilerFlushInterval);
            }
            Sut.GetSession().Should().BeNull();
        }
    }

    [Fact]
    public void NoMaxSettings()
    {
        _redisConnectionOptions.ProfilerSessionMaxLifespan = null;
        _redisConnectionOptions.ProfilerSessionMaxChecks = null;
        var act = () => Sut.GetSession();
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Dispose_twice()
    {
        Sut.Dispose();
        Sut.Dispose();
    }

    [Fact]
    public void Processor_throws()
    {
        _profiledCommandProcessor.WhenForAnyArgs(x => x.Process(Arg.Any<IProfiledCommand>(), Arg.Any<string>())).Throw<Exception>();
        var sessionId = _fixture.Create<string>();
        var disposableAction = Sut.CreateSession(sessionId);
        disposableAction.Dispose();
        Sut.Count.Should().Be(0);
        _profiledCommandProcessor.Received().Process(Arg.Any<IProfiledCommand>(), Arg.Any<string>());
    }

    [Fact]
    public void Create_Session_when_disposed()
    {
        var sut = Sut;
        sut.Dispose();
        Disposable.Empty.Should().Be(sut.CreateSession(_fixture.Create<string>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" ")]
    public void Create_session_when_no_sessionId(string? sessionId)
    {
        _redisConnectionOptions.ProfilerEnabled = true;
        var dispose =  Sut.CreateSession(sessionId);
        Sut.Count.Should().Be(0);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _clock = _fixture.Freeze<ISystemClock>();
        _now = DateTimeOffset.UtcNow;
        _clock.UtcNow.Returns(callInfo => _now);
        _profiledCommandProcessor = _fixture.Freeze<IProfiledCommandProcessor>();
        _profilingSessionCommandReader = _fixture.Freeze<IProfilingSessionCommandReader>();
        _logger = NullLogger<RedisProfiler>.Instance;
        _fixture.Inject(_logger);
        _redisConnectionOptions = new RedisConnectionOptions
        {
            ProfilerEnabled = true,
            ProfilerFlushInterval = TimeSpan.FromMilliseconds(100),
            ProfilerHasDefaultSession = true
        };
        _fixture.Inject(Options.Create(_redisConnectionOptions));
        return Task.CompletedTask;
    }
}
