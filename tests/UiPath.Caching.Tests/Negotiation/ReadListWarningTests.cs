using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Config;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Negotiation;

public class ReadListWarningTests
{
    [Fact]
    public void A_force_node_whose_read_list_leaves_json_out_warns_once_that_peers_in_auto_may_still_write_json()
    {
        var logger = new RecordedLogger();
        using var provider = Build(FormatNegotiationMode.Force, logger, ["fast"], ["fast"]);

        _ = provider.GetRequiredService<FormatNegotiation>();

        var warning = logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning).Subject.Message;
        warning.Should().Contain("orders").And.Contain("sessions").And.Contain("Auto").And.Contain("json");
    }

    [Fact]
    public void A_force_node_whose_read_lists_all_include_json_does_not_warn()
    {
        var logger = new RecordedLogger();
        using var provider = Build(FormatNegotiationMode.Force, logger, ["json", "fast"], null);

        _ = provider.GetRequiredService<FormatNegotiation>();

        logger.Records.Should().NotContain(r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public void A_node_in_auto_does_not_warn_because_a_read_list_without_json_cannot_be_built()
    {
        var logger = new RecordedLogger();
        using var provider = Build(FormatNegotiationMode.Auto, logger, ["json", "fast"], null);

        _ = provider.GetRequiredService<FormatNegotiation>();

        logger.Records.Should().NotContain(r => r.Level == LogLevel.Warning);
    }

    private static ServiceProvider Build(FormatNegotiationMode mode, RecordedLogger logger, string[]? orders, string[]? sessions)
    {
        var loggers = Substitute.For<ILoggerFactory>();
        loggers.CreateLogger(Arg.Any<string>()).Returns(logger);
        return new ServiceCollection()
            .AddSingleton(loggers)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1");
                    b.AddRedis(o => o.FormatNegotiation.Mode = mode);
                    b.AddValueSerializer("fast", _ => new PassthroughValueSerializer(0x10));
                },
                o =>
                {
                    o.AppShortName = "readlistwarn";
                    o.Policies["orders"] = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = orders } };
                    o.Policies["sessions"] = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = sessions } };
                })
            .BuildServiceProvider();
    }
}
