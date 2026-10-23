using System.Net;
using Microsoft.Extensions.Options;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;
using static UiPath.Caching.Redis.ProfiledCommandExtensions;

namespace UiPath.Caching.Tests;
public class ProfiledCommandProcessorTest : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ProfiledCommandProcessor? _sut = null;
    private RecordingTelemetryProvider _telemetryProvider = default!;
    private IProfiledCommand _profiledCommand = default!;
    private Lazy<RedisProfileFetcher> _oldFetcherLazy = default!;

    private ProfiledCommandProcessor Sut => _sut ??= _fixture.Create<ProfiledCommandProcessor>();

    [Theory]
    [InlineData("PING", "PING")]
    [InlineData("PING", "ping")]
    public void CommandProcessor_DeniedCommand_NotEmitted(string commandName, string denyListEntry)
    {
        _profiledCommand.Command.Returns(commandName);
        _fixture.Inject<IOptions<RedisConnectionOptions>>(
            Options.Create(new RedisConnectionOptions { ProfilerCommandDenyList = [denyListEntry] }));
        _sut = null;

        Sut.Process(_profiledCommand, _fixture.Create<string>());

        _telemetryProvider.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void CommandProcessor_AllowedCommand_Emitted()
    {
        _profiledCommand.Command.Returns("GET");
        _fixture.Inject<IOptions<RedisConnectionOptions>>(
            Options.Create(new RedisConnectionOptions { ProfilerCommandDenyList = ["PING"] }));
        _sut = null;

        Sut.Process(_profiledCommand, _fixture.Create<string>());

        _telemetryProvider.Dependencies.Should().ContainSingle();
    }

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
    [Theory]
    [InlineData("cache.redis.example:10000,password=secret,ssl=true", "cache.redis.example:10000")]
    [InlineData("cache.redis.example", "cache.redis.example:6379")]
    [InlineData("cache.redis.example,ssl=true", "cache.redis.example:6380")]
    [InlineData("a.example:7000,b.example:7001", "a.example:7000,b.example:7001")]
    [InlineData("10.0.0.1:6379,unknownKey=1", "10.0.0.1:6379")]
    public void The_target_is_the_configured_server_and_the_node_that_served_the_command_a_property(string connectionString, string target)
    {
        _profiledCommand.EndPoint.Returns(new IPEndPoint(IPAddress.Parse("20.101.230.97"), 8503));
        _fixture.Inject<IOptions<RedisConnectionOptions>>(Options.Create(new RedisConnectionOptions { ConnectionString = connectionString }));

        Sut.Process(_profiledCommand, null);

        var dependency = _telemetryProvider.Dependencies.Should().ContainSingle().Which;
        dependency.Target.Should().Be(target);
        dependency.Properties.Should().Contain(ProfiledCommandProcessor.NodeField, "20.101.230.97:8503");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("password=secret")]
    public void Without_a_configured_server_the_node_is_the_target(string connectionString)
    {
        _profiledCommand.EndPoint.Returns(new DnsEndPoint("node.example", 8503));
        _fixture.Inject<IOptions<RedisConnectionOptions>>(Options.Create(new RedisConnectionOptions { ConnectionString = connectionString }));

        Sut.Process(_profiledCommand, null);

        var dependency = _telemetryProvider.Dependencies.Should().ContainSingle().Which;
        dependency.Target.Should().Be("node.example:8503");
        dependency.Properties.Should().Contain(ProfiledCommandProcessor.NodeField, "node.example:8503");
    }

    [Fact]
    public void A_command_with_no_node_reports_no_node()
    {
        _profiledCommand.EndPoint.Returns((EndPoint?)null);

        Sut.Process(_profiledCommand, null);

        var dependency = _telemetryProvider.Dependencies.Should().ContainSingle().Which;
        dependency.Target.Should().Be(ProfiledCommandProcessor.UnknownTarget);
        dependency.Properties.Should().NotContainKey(ProfiledCommandProcessor.NodeField);
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
        _fixture.Inject<IOptions<RedisConnectionOptions>>(Options.Create(new RedisConnectionOptions()));
        _profiledCommand = _fixture.Freeze<IProfiledCommand>();
        _oldFetcherLazy = FetcherLazy;
        FetcherLazy = new(() => new RedisProfileFetcher
        {
            CommandAndKey = cmd => _fixture.Create<string>(),
            Message = cmd => _fixture.Create<string>(),
            ProfiledCommandType = _profiledCommand.GetType(),
        });
        return ValueTask.CompletedTask;
    }
}
