namespace UiPath.Platform.Caching.Tests.Broadcast;

public class ConnectionStateMonitorTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private IConnectionState[] _connectionStates = default!;

    private ConnectionStateMonitor? _sut = null;

    private ConnectionStateMonitor Sut => _sut ??= _fixture.Create<ConnectionStateMonitor>();

    [Fact]
    public void Works_as_expected()
    {
        var _isConnected0 = true;
        var _isConnected1 = true;
        _connectionStates[0].IsConnected.Returns(_ => _isConnected0);
        _connectionStates[1].IsConnected.Returns(_ => _isConnected1);
        Sut.IsConnected.Should().BeTrue();
        _isConnected0 = false;
        _connectionStates[0].OnConnectionFailed += Raise.Event();
        Sut.IsConnected.Should().BeFalse();
        _connectionStates[0].OnConnectionRestored += Raise.Event();
        _isConnected0 = true;
        Sut.IsConnected.Should().BeTrue();
        _isConnected0 = false;
        _connectionStates[0].OnReconnected += Raise.Event();
        Sut.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Works_as_expected_when_no_events()
    {
        var _isConnected0 = false;
        var _isConnected1 = true;
        _connectionStates[0].IsConnected.Returns(_ => _isConnected0);
        _connectionStates[1].IsConnected.Returns(_ => _isConnected1);
        Sut.IsConnected.Should().BeFalse();
        _isConnected0 = true;

        await WaitUntilAsync(() => Sut.IsConnected, TimeSpan.FromSeconds(30), testContextAccessor.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(20, token);
        }
        throw new TimeoutException($"Predicate was not satisfied within {timeout}.");
    }

    [Fact]
    public void Dispose_works_as_expected()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _connectionStates = _fixture.CreateMany<IConnectionState>(2).ToArray();
        _fixture.Inject(_connectionStates);
        _fixture.Inject(TimeSpan.FromMilliseconds(100));
        return ValueTask.CompletedTask;
    }
}

