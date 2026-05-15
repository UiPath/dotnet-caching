using StackExchange.Redis.Profiling;
using UiPath.Platform.Caching.Telemetry;
using UiPath.Platform.Caching.Tests.Telemetry;
using static UiPath.Platform.Caching.Redis.ProfiledCommandExtensions;

namespace UiPath.Platform.Caching.Tests;
public class ProfiledCommandProcessorTest : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ProfiledCommandProcessor? _sut = null;
    private RecordingTelemetryProvider _telemetryProvider = default!;
    private IProfiledCommand _profiledCommand = default!;
    private Lazy<RedisProfileFetcher> _oldFetcherLazy = default!;

    private ProfiledCommandProcessor Sut => _sut ??= _fixture.Create<ProfiledCommandProcessor>();

    [Fact]
    public void CommandProcessor_Null()
    {
        var sessionId = _fixture.Create<string>();
        Sut.Process(_profiledCommand, sessionId);

        _telemetryProvider.Dependencies.Should().ContainSingle();
        var properties = _telemetryProvider.Dependencies[0].Properties;
        properties.Should().NotBeNull();
        properties.Should().ContainKey(ProfiledCommandProcessor.TelemetryTypeField);
        properties.Should().ContainKey(ProfiledCommandProcessor.CreationToEnqueuedField);
        properties.Should().ContainKey(ProfiledCommandProcessor.EnqueuedToSendingField);
        properties.Should().ContainKey(ProfiledCommandProcessor.SentToResponseField);
        properties.Should().ContainKey(ProfiledCommandProcessor.ResponseToCompletionField);
        properties.Should().ContainKey(ProfiledCommandProcessor.FlagsField);
        properties.Should().ContainKey(ProfiledCommandProcessor.ProfileSessionIdField);
        properties.Should().ContainKey(ProfiledCommandProcessor.RetransmissionOfField);
        properties.Should().ContainKey(ProfiledCommandProcessor.RetransmissionReasonField);
    }
    public ValueTask DisposeAsync()
    {
        FetcherLazy = _oldFetcherLazy;
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _telemetryProvider = new RecordingTelemetryProvider();
        _fixture.Inject<ICachingTelemetryProvider>(_telemetryProvider);
        _profiledCommand = _fixture.Freeze<IProfiledCommand>();
        _oldFetcherLazy = FetcherLazy;
        FetcherLazy = new(() => new RedisProfileFetcher
        {
            CommandAndKey = cmd => _fixture.Create<string>(),
            Message = cmd => _fixture.Create<string>(),
            ProfiledCommandType = _profiledCommand.GetType()
        });
        return ValueTask.CompletedTask;
    }
}
