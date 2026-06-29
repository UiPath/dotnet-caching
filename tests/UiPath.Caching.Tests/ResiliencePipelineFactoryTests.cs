using System.Reflection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Telemetry;
using Polly.Timeout;
using UiPath.Caching.Polly;

namespace UiPath.Caching.Tests;
public class ResiliencePipelineFactoryTest(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private TelemetryOptions? _telemetryOptions;
    private ResiliencePoliciesOptions _resiliencePoliciesOptions = default!;
    private ILoggerFactory _loggerFactory = default!;
    private ILogger<ResiliencePipeline<bool>> _boolLogger = default!;

    [Fact]
    public void DefaultPipelineConfiguration()
    {
        AssertStrategies(4);
    }

    [Fact]
    public void RethrowCircuitBreakerExceptions()
    {
        _resiliencePoliciesOptions.RethrowCircuitBreakerExceptions = true;
        AssertStrategies(3);
    }

    [Fact]
    public void RequestTimeout()
    {
        _resiliencePoliciesOptions.RequestTimeout = null;
        AssertStrategies(3);
    }

    [Fact]
    public void ExceptionsAllowedBeforeBreaking()
    {
        _resiliencePoliciesOptions.ExceptionsAllowedBeforeBreaking = 1;
        AssertStrategies(3);
    }

    [Fact]
    public void DurationOfBreak()
    {
        _resiliencePoliciesOptions.DurationOfBreak = TimeSpan.Zero;
        AssertStrategies(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void RetryCount(int? count)
    {
        _resiliencePoliciesOptions.RetryCount = count;
        AssertStrategies(3);
    }



    [Fact]
    public void AllDisabled()
    {
        _resiliencePoliciesOptions.TelemetryEnabled = true;
        _resiliencePoliciesOptions.DurationOfBreak = TimeSpan.Zero;
        _resiliencePoliciesOptions.ExceptionsAllowedBeforeBreaking = 0;
        _resiliencePoliciesOptions.RequestTimeout = null;
        _resiliencePoliciesOptions.RetryCount = null;
        _resiliencePoliciesOptions.RethrowCircuitBreakerExceptions = true;
        var resiliencePipelineFactory = _fixture.Create<ResiliencePipelineFactory>();
        var pipeline = resiliencePipelineFactory.Create<bool>("read", false);
        var x = typeof(ResiliencePipeline<bool>).GetProperty("Component", BindingFlags.Instance | BindingFlags.NonPublic);
        var component = x!.GetValue(pipeline);
         component!.GetType().Name.Should().Be("NullComponent");
    }

    [Fact]
    public void AllEnabled()
    {
        _resiliencePoliciesOptions.Enabled = true;
        _resiliencePoliciesOptions.TelemetryEnabled = true;
        _resiliencePoliciesOptions.DurationOfBreak = TimeSpan.FromMinutes(1);
        _resiliencePoliciesOptions.ExceptionsAllowedBeforeBreaking = 10;
        _resiliencePoliciesOptions.RequestTimeout = TimeSpan.FromMinutes(10);
        _resiliencePoliciesOptions.RetryCount = 10;
        _resiliencePoliciesOptions.RethrowCircuitBreakerExceptions = false;
        AssertStrategies(4);
    }

    [Fact]
    public void Disabled()
    {
        _resiliencePoliciesOptions.Enabled = false;
        var resiliencePipelineFactory = _fixture.Create<ResiliencePipelineFactory>();
        var pipeline = resiliencePipelineFactory.Create("read", false);
        var x = typeof(ResiliencePipeline<bool>).GetProperty("Component", BindingFlags.Instance | BindingFlags.NonPublic);
        var component = x!.GetValue(pipeline);
        component!.GetType().Name.Should().Be("NullComponent");
    }

    [Fact]
    public async Task Pipeline_works_as_expected()
    {
        List<string?> logMessages = [];
        _resiliencePoliciesOptions.RetryCount = 1;
        _resiliencePoliciesOptions.RequestTimeout = TimeSpan.FromMilliseconds(50);
        _resiliencePoliciesOptions.ExceptionsAllowedBeforeBreaking = 2;
        _resiliencePoliciesOptions.DurationOfBreak = TimeSpan.FromMilliseconds(500);
        _resiliencePoliciesOptions.RethrowCircuitBreakerExceptions = false;
        _boolLogger.When(x => x.Log(Arg.Any<LogLevel>(), Arg.Any<EventId>(), Arg.Any<Arg.AnyType>(), Arg.Any<Exception?>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>()))
            .Do(x =>
            {
                var logLevel = x.ArgAt<LogLevel>(0);
                var state = x.ArgAt<object>(2);
                logLevel.Should().Be(LogLevel.Warning);
                var message = state.ToString();
                logMessages.Add(message);
            });
        var timeoutFunc = new Func<CancellationToken, ValueTask<bool>>(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        });
        var exceptionFunc = new Func<CancellationToken, ValueTask<bool>>(token =>
        {
            throw new Exception();
        });
        var successFunc = new Func<CancellationToken, ValueTask<bool>>(token =>
        {
            return new ValueTask<bool>(true);
        });
        var resiliencePipelineFactory = _fixture.Create<ResiliencePipelineFactory>();
        var pipeline = resiliencePipelineFactory.Create("read", false);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(testContextAccessor.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(5));

        var act = async () => await pipeline.ExecuteAsync(timeoutFunc, guard.Token);
        await act.Should().ThrowAsync<TimeoutRejectedException>();
        bool? actual = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                actual = await pipeline.ExecuteAsync(exceptionFunc, testContextAccessor.Current.CancellationToken);
                if(actual is not null)
                {
                    break;
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }
        actual.Should().BeFalse();
        actual = null;
        await Task.Delay(250, testContextAccessor.Current.CancellationToken);
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(100, testContextAccessor.Current.CancellationToken);
            //we are in a circuit breaker state => no exception is thrown, returning default value
            actual = await pipeline.ExecuteAsync(successFunc, testContextAccessor.Current.CancellationToken);
            if(actual == true)
            {
                // circuit breaker is closed
                break;
            }
        }
        actual.Should().BeTrue();

        logMessages.Should().NotBeEmpty();
        logMessages.Should().Contain(log => log.Contains("Execution timed out after"));
        logMessages.Should().Contain(log => log.Contains("OnRetry, Attempt:"));
        logMessages.Should().Contain(log => log.Contains("CircuitBreaker OnOpened"));
        logMessages.Should().Contain(log => log.Contains("OnFallback"));
        logMessages.Should().Contain(log => log.Contains("OnClosed"));
        logMessages.Should().Contain(log => log.Contains("OnHalfOpened"));
    }

    private void AssertStrategies(int count)
    {
        var resiliencePipelineFactory = _fixture.Create<ResiliencePipelineFactory>();
        var pipeline = resiliencePipelineFactory.Create("read", false);
        var x = typeof(ResiliencePipeline<bool>).GetProperty("Component", BindingFlags.Instance | BindingFlags.NonPublic);
        var component = x!.GetValue(pipeline);
        var strategies = component!.GetType().GetProperty("Components", BindingFlags.Instance | BindingFlags.Public)!.GetValue(component) as IEnumerable<object>;
        strategies.Should().NotBeNull().And.HaveCount(count);
        pipeline.Should().NotBeNull();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _telemetryOptions = new TelemetryOptions
        {
        };
        _fixture.Inject(_telemetryOptions);
        _resiliencePoliciesOptions = new ResiliencePoliciesOptions();
        var monitor = Substitute.For<IOptionsMonitor<ResiliencePoliciesOptions>>();
        monitor.CurrentValue.Returns(_ => _resiliencePoliciesOptions);
        monitor.Get(Arg.Any<string>()).Returns(_ => _resiliencePoliciesOptions);
        _fixture.Inject(monitor);
        _loggerFactory = _fixture.Freeze<ILoggerFactory>();
        _boolLogger = _fixture.Freeze<ILogger<ResiliencePipeline<bool>>>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).ReturnsForAnyArgs(_boolLogger);
        return ValueTask.CompletedTask;
    }
}
