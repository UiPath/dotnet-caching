using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration;

/// <summary>Picks the write policy of one call by its group: MessagePack only while the group is switched and the gate is open, the envelope in every other case.</summary>
/// <remarks>
/// The setting is read on every call. An answer that throws, or that names no known format, writes the envelope: the group is never moved toward MessagePack by a setting that cannot be read.
/// The library still applies its own negotiation to a MessagePack write, so a node that joined without the pin turns the write back to the envelope there.
/// </remarks>
internal sealed partial class WriteFormatSelector(Func<string, string?> format, PinCoordinator pins, ILogger logger)
{
    private readonly HashSet<string> _failed = [];

    public CachePolicy PolicyFor(GroupPin pin)
    {
        string? answer;
        try
        {
            answer = format(pin.Group);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(pin.Group, ex);
            return WriteFormats.Envelope;
        }

        Recover(pin.Group);
        if (!WriteFormats.IsKnown(answer))
        {
            Report(pin.Group, null);
            pins.Want(pin, false);
            return WriteFormats.Envelope;
        }

        var wanted = WriteFormats.WantsMessagePack(answer);
        pins.Want(pin, wanted);
        return wanted && Ready(pin) ? WriteFormats.MessagePackPolicy : WriteFormats.Envelope;
    }

    private bool Ready(GroupPin pin) => pin.Applied && pin.Acked && pin.Switched && pin.Durable && pins.Hold() is null;

    private void Report(string group, Exception? exception)
    {
        lock (_failed)
        {
            if (!_failed.Add(group))
            {
                return;
            }
        }

        if (exception is null)
        {
            LogUnknown(group);
        }
        else
        {
            LogFailed(exception, group);
        }
    }

    private void Recover(string group)
    {
        lock (_failed)
        {
            _failed.Remove(group);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The write format setting for group {Group} threw; the group writes the envelope.")]
    private partial void LogFailed(Exception exception, string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The write format setting for group {Group} names no known format (messagepack, json or null); the group writes the envelope.")]
    private partial void LogUnknown(string group);
}
