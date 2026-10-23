using Microsoft.Extensions.Options;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public sealed class ProfiledCommandProcessor(
    ICachingTelemetryProvider telemetryProvider,
    IOptions<RedisConnectionOptions> options) : IProfiledCommandProcessor
{
    private readonly HashSet<string> _commandDenyList =
        new(options.Value.ProfilerCommandDenyList ?? [], StringComparer.OrdinalIgnoreCase);

    // On a cluster each command reports the node that served it; the target stays the server the application configured.
    private readonly string? _connectionTarget = ConnectionTarget(options.Value.ConnectionString);

    public static string FlagsField { get; set; } = "Flags";

    public static string CreationToEnqueuedField { get; set; } = "CreationToEnqueued";

    public static string EnqueuedToSendingField { get; set; } = "EnqueuedToSending";

    public static string SentToResponseField { get; set; } = "SentToResponse";

    public static string ResponseToCompletionField { get; set; } = "ResponseToCompletion";

    public static string TelemetryTypeField { get; set; } = "TelemetryType";

    public static string RetransmissionOfField { get; set; } = "RetransmissionOf";

    public static string RetransmissionReasonField { get; set; } = "RetransmissionReason";

    public static string ProfileSessionIdField { get; set; } = "ProfileSessionId";

    public static string NodeField { get; set; } = "RedisNode";

    public static string UnknownCommand { get; set; } = "UNKNOWN";

    public static string UnknownTarget { get; set; } = "Unk";

    public static string UnknownRetransmissionReason { get; set; } = "N/A";

    public static string RedisTelemetryType { get; set; } = "Redis";

    public void Process(IProfiledCommand command, string? sessionId)
    {
        var baseCommand = string.IsNullOrEmpty(command.Command) ? UnknownCommand : command.Command;

        if (_commandDenyList.Count > 0 && _commandDenyList.Contains(baseCommand))
        {
            return;
        }

        var statement = command.GetStatement();
        var commandName = command.GetCommandName();
        var node = command.GetTarget();
        var properties = new List<KeyValuePair<string, string>>(10)
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

        if (node is not null)
        {
            properties.Add(new(NodeField, node));
        }

        telemetryProvider.TrackDependency(
            type: RedisTelemetryType,
            name: commandName,
            target: _connectionTarget ?? node ?? UnknownTarget,
            data: statement,
            startTime: command.CommandCreated,
            duration: command.ElapsedTime,
            resultCode: string.Empty,
            success: true,
            properties: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(properties));
    }

    // Null without a connection string, as with a ConnectionFactory, and the node stays the target.
    private static string? ConnectionTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var configuration = ConfigurationOptions.Parse(connectionString, ignoreUnknown: true);
            configuration.SetDefaultPorts();
            var endPoints = configuration.EndPoints.Select(ProfiledCommandExtensions.Format).OfType<string>().ToArray();
            return endPoints.Length == 0 ? null : string.Join(',', endPoints);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
