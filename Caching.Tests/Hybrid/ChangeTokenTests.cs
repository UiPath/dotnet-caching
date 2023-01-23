using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests.Hybrid;

public class ChangeTokenTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private string _key = default!;
    private Channel _channel = default!;
    private IChannelSubscriber _subscriber = default!;
    private TestEventFormatterProxy _formatter = default!;
    private Uri? _source = null;

    private ChangeToken? _sut = null;
    private ChangeToken Sut => _sut ??= new ChangeToken(_key, _channel, _subscriber, _source, _fixture.Freeze<ILogger<ChangeToken>>());

    [Fact]
    public void Verify_ActiveChangeCallbacks()
    {
        Sut.ActiveChangeCallbacks.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public void OnNext_NoChanges_wheniInvalid_event(TestClearCacheEvent cloudEvent)
    {
        object? actualState = null;
        var callbackCalled = false;
        var expectedState = _fixture.Create<object>();
        Action<object?> callback = (state) =>
        {
            callbackCalled = true;
            actualState = state;
        };
        var d = Sut.RegisterChangeCallback(callback, expectedState);

        Sut.OnNext(cloudEvent);
        Sut.HasChanged.Should().BeFalse();
        callbackCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData("urn:machine", false)]
    [InlineData("urn:another-machine", true)]
    [InlineData(null, true)]
    public void OnNext_Changes_when_corect_key(string source, bool hasChanged)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            _source = new Uri(source);
        }

        object? actualState = null;
        var callbackCalled = false;
        var expectedState = _fixture.Create<object>();
        Action<object?> callback = (state) =>
        {
            callbackCalled = true;
            actualState = state;
        };

        var d = Sut.RegisterChangeCallback(callback, expectedState);
        var cloudEVent = new TestClearCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new ClearCacheEventData(_key)
        };
        Sut.OnNext(cloudEVent);
        Sut.HasChanged.Should().Be(hasChanged);
        callbackCalled.Should().Be(hasChanged);
    }


    [Fact]
    public void OnComplete()
    {
        object? actualState = null;
        var callbackCalled = false;
        var expectedState = _fixture.Create<object>();
        Action<object?> callback = (state) =>
        {
            callbackCalled = true;
            actualState = state;
        };

        var d = Sut.RegisterChangeCallback(callback, expectedState);
        Sut.OnCompleted();
        Sut.HasChanged.Should().BeFalse();
        callbackCalled.Should().BeFalse();
    }

    [Fact]
    public void OnError()
    {
        object? actualState = null;
        var callbackCalled = false;
        var expectedState = _fixture.Create<object>();
        Action<object?> callback = (state) =>
        {
            callbackCalled = true;
            actualState = state;
        };

        var d = Sut.RegisterChangeCallback(callback, expectedState);
        Sut.OnError(_fixture.Create<Exception>());
        Sut.HasChanged.Should().BeTrue();
        callbackCalled.Should().BeTrue();
    }

    [Fact]
    public void Dispose_token()
    {
        var disposable = _fixture.Create<IDisposable>();
        _subscriber.Subscribe(_channel, Arg.Any<IObserver<IClearCacheEvent>>())
            .Returns(disposable);
        Sut.Dispose();
        disposable.Received(1).Dispose();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _key = _fixture.Freeze<string>();
        _channel = (Channel)_fixture.Create<string>();
        _fixture.Inject(_channel);
        _fixture.Freeze<ILogger<ChangeToken>>();
        _subscriber = _fixture.Freeze<IChannelSubscriber>();
        _formatter = new TestEventFormatterProxy();
        _fixture.Inject<IEventFormatterProxy>(_formatter);
        return Task.CompletedTask;
    }

    public static IEnumerable<object[]> InvalidEvents() => new TestClearCacheEvent[]
    {
        new TestClearCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Data = new ClearCacheEventData(Guid.NewGuid().ToString())
        },
        new TestClearCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new ClearCacheEventData(Guid.NewGuid().ToString())
        },
        new TestClearCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = null
        },
        new TestClearCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
        },
        new TestClearCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new ClearCacheEventData(Guid.NewGuid().ToString())
        },

        new TestClearCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = null
        }
    }.Select(cv => new object[] { cv });
}
