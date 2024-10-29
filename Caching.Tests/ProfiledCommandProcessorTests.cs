using StackExchange.Redis.Profiling;
using UiPath.Platform.Telemetry;
using static UiPath.Platform.Caching.Redis.ProfiledCommandExtensions;

namespace UiPath.Platform.Caching.Tests;
public class ProfiledCommandProcessorTest : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private ProfiledCommandProcessor? _sut = null;
    private ITelemetryProvider _telemetryProvider = default!;
    private IProfiledCommand _profiledCommand = default!;
    private Lazy<RedisProfileFetcher> _oldFetcherLazy = default!;

    private ProfiledCommandProcessor Sut => _sut ??= _fixture.Create<ProfiledCommandProcessor>();

    [Fact]
    public void CommandProcessor_Null()
    {
        IDictionary<string, string>? properties = null;
        var sessionId = _fixture.Create<string>();
        _telemetryProvider.WhenForAnyArgs(x => x.TrackDependency(
            type: Arg.Any<string>(),
            name: Arg.Any<string>(),
            target: Arg.Any<string>(),
            data: Arg.Any<string>(),
            startTime: Arg.Any<DateTimeOffset>(),
            duration: Arg.Any<TimeSpan>(),
            resultCode: Arg.Any<string>(),
            success: Arg.Any<bool>(),
            properties: Arg.Any<IDictionary<string, string>?>()))
            .Do(callInfo =>
            {
                properties = callInfo.Arg<IDictionary<string, string>?>();
            });
        Sut.Process(_profiledCommand, sessionId);
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
    public Task DisposeAsync()
    {
        FetcherLazy = _oldFetcherLazy;
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _telemetryProvider = _fixture.Freeze<ITelemetryProvider>();
        _profiledCommand = _fixture.Freeze<IProfiledCommand>();
        _oldFetcherLazy = FetcherLazy;
        FetcherLazy = new(() => new RedisProfileFetcher
        {
            CommandAndKey = cmd => _fixture.Create<string>(),
            Message = cmd => _fixture.Create<string>(),
            ProfiledCommandType = _profiledCommand.GetType()
        });
        return Task.CompletedTask;
    }
}
