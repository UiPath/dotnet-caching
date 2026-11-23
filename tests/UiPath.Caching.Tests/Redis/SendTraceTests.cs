using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

/// <summary>A send the client provably never wrote leaves nothing unknown; an ambiguous one stays a send.</summary>
public sealed class SendTraceTests
{
    [Theory]
    [InlineData(CommandStatus.WaitingToBeSent, "SocketFailure on 127.0.0.1:1", StoreOutcome.NotSent)]
    [InlineData(CommandStatus.Unknown, "No connection is active/available to service this operation: SET k", StoreOutcome.NotSent)]
    [InlineData(CommandStatus.Unknown, "SocketFailure on 127.0.0.1:1", StoreOutcome.Unknown)]
    [InlineData(CommandStatus.Sent, "SocketClosed on 127.0.0.1:1", StoreOutcome.Unknown)]
    public async Task A_connection_failure_counts_as_sent_unless_the_command_was_never_written(CommandStatus status, string message, StoreOutcome expected)
    {
        var trace = new SendTrace();
        trace.MarkSent();

        var act = async () => await trace.AcknowledgeAsync(Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, message, null, status)));

        await act.Should().ThrowAsync<RedisConnectionException>();
        trace.Outcome().Should().Be(expected);
    }

    [Fact]
    public async Task An_unsent_retry_after_an_ambiguous_attempt_keeps_the_outcome_unknown()
    {
        var trace = new SendTrace();
        trace.MarkSent();
        var first = async () => await trace.AcknowledgeAsync(Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketClosed, CommandFlags.None, "SocketClosed", null, CommandStatus.Sent)));
        await first.Should().ThrowAsync<RedisConnectionException>();
        trace.MarkSent();
        var retry = async () => await trace.AcknowledgeAsync(Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "No connection is active/available to service this operation", null, CommandStatus.WaitingToBeSent)));
        await retry.Should().ThrowAsync<RedisConnectionException>();

        trace.Outcome().Should().Be(StoreOutcome.Unknown, "the first attempt may still land");
    }
}
