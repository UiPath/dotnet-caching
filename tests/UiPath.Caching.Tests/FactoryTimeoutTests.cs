using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

public class FactoryTimeoutTests(ITestContextAccessor testContextAccessor)
{
    private const string TimedOutEvent = "cache.factory.timed_out";

    [Fact]
    public async Task RunAsync_still_throws_TimeoutException_when_the_telemetry_sink_refuses_the_record()
    {
        var telemetry = new RefusingTelemetryProvider(TimedOutEvent);

        Func<Task> act = () => RunTimingOutFactoryAsync(telemetry);

        await act.Should().ThrowAsync<TimeoutException>("a refused record must not replace the exception the caller is documented to get");
        telemetry.Exceptions.Should().Contain(RefusingTelemetryProvider.Failure, "the refusal is reported rather than swallowed");
    }

    [Fact]
    public async Task RunAsync_records_the_timeout_when_the_telemetry_sink_accepts_it()
    {
        var telemetry = new RefusingTelemetryProvider("some.other.event");

        Func<Task> act = () => RunTimingOutFactoryAsync(telemetry);

        await act.Should().ThrowAsync<TimeoutException>();
        telemetry.Events.Should().Contain(TimedOutEvent);
        telemetry.Exceptions.Should().BeEmpty();
    }

    private Task<string> RunTimingOutFactoryAsync(RefusingTelemetryProvider telemetry) =>
        FactoryTimeout.RunAsync<string>(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "unreachable";
            },
            TimeSpan.FromMilliseconds(20),
            new CacheKey("k"),
            "cache",
            telemetry,
            testContextAccessor.Current.CancellationToken);
}
