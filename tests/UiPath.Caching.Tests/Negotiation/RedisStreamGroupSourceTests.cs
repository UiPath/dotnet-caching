using StackExchange.Redis;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Tests.Negotiation;

public class RedisStreamGroupSourceTests
{
    private static readonly RedisKey Stream = "app:st:orders";

    [Theory]
    [InlineData("NOGROUP No such consumer group 'g' for key name 'app:st:orders'")]
    [InlineData("ERR no such key")]
    public async Task A_group_deleted_between_listing_and_reading_has_no_consumer(string error)
    {
        var database = Substitute.For<IDatabase>();
        var connector = Substitute.For<IRedisConnector>();
        connector.Database.Returns(database);
#pragma warning disable CS0618 // The only public constructor of a server error.
        database.StreamConsumerInfoAsync(Stream, "g", Arg.Any<CommandFlags>()).Returns(Task.FromException<StreamConsumerInfo[]>(new RedisServerException(error)));
#pragma warning restore CS0618

        (await new RedisStreamGroupSource(connector).IdleAsync(Stream, "g", TestContext.Current.CancellationToken)).Should().BeNull();
    }
}
