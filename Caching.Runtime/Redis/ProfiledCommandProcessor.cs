using StackExchange.Redis.Profiling;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class ProfiledCommandProcessor(ICachingTelemetryProvider telemetryProvider) : IProfiledCommandProcessor
{
    public static string FlagsField { get; set; } = "Flags";

    public static string CreationToEnqueuedField { get; set; } = "CreationToEnqueued";

    public static string EnqueuedToSendingField { get; set; } = "EnqueuedToSending";

    public static string SentToResponseField { get; set; } = "SentToResponse";

    public static string ResponseToCompletionField { get; set; } = "ResponseToCompletion";

    public static string TelemetryTypeField { get; set; } = "TelemetryType";

    public static string RetransmissionOfField { get; set; } = "RetransmissionOf";

    public static string RetransmissionReasonField { get; set; } = "RetransmissionReason";

    public static string ProfileSessionIdField { get; set; } = "ProfileSessionId";

    public static string UnknownTarget { get; set; } = "Unk";

    public static string UnknownRetransmissionReason { get; set; } = "N/A";

    public static string RedisTelemetryType { get; set; } = "Redis";

    public void Process(IProfiledCommand command, string? sessionId)
    {
        var statement = command.GetStatement();
        var commandName = command.GetCommandName();
        var target = command.GetTarget();
        var properties = new List<KeyValuePair<string, string>>(9)
        {
            new(FlagsField, command.Flags.ToString("F")),
            new(CreationToEnqueuedField, command.CreationToEnqueued.ToString()),
            new(EnqueuedToSendingField, command.EnqueuedToSending.ToString()),
            new(SentToResponseField, command.SentToResponse.ToString()),
            new(ResponseToCompletionField, command.ResponseToCompletion.ToString()),
            new(TelemetryTypeField, RedisTelemetryType),
        };

        if (command.RetransmissionOf != null)
        {
            properties.Add(new(RetransmissionOfField, command.RetransmissionOf.GetCommandName()));
            properties.Add(new(RetransmissionReasonField, command.RetransmissionReason?.ToString() ?? UnknownRetransmissionReason));
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            properties.Add(new(ProfileSessionIdField, sessionId));
        }

        telemetryProvider.TrackDependency(
            type: RedisTelemetryType,
            name: commandName,
            target: target ?? UnknownTarget,
            data: statement,
            startTime: command.CommandCreated,
            duration: command.ElapsedTime,
            resultCode: string.Empty,
            success: true,
            properties: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(properties));
    }
}
