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

    /// <summary>The policy alone, for a caller that writes nothing with it.</summary>
    public CachePolicy PolicyFor(GroupPin pin)
    {
        using var lease = Select(pin);
        return lease.Policy!;
    }

    /// <summary>The policy of one write; a MessagePack lease keeps the group in the gate's coverage until it is disposed after the write.</summary>
    public WriteLease Select(GroupPin pin)
    {
        string? answer;
        try
        {
            answer = format(pin.Group);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(pin.Group, ex);
            return new(WriteFormats.Envelope, null);
        }

        if (!WriteFormats.IsKnown(answer))
        {
            Report(pin.Group, null);
            pins.Want(pin, false);
            return new(WriteFormats.Envelope, null);
        }

        // Only an answer that reads and validates ends a reported failure, so a setting that stays wrong is logged once.
        Recover(pin.Group);
        var wanted = WriteFormats.WantsMessagePack(answer);
        pins.Want(pin, wanted);
        if (!wanted)
        {
            return new(WriteFormats.Envelope, null);
        }

        // Counted as writing before the gate is asked, so a concurrent answer that clears the wanted bit cannot drop the group from the library's own check.
        pin.EnterWriting();
        if (Ready(pin))
        {
            return new(WriteFormats.MessagePackPolicy, pin);
        }

        pin.ExitWriting();
        return new(WriteFormats.Envelope, null);
    }

    private bool Ready(GroupPin pin) => pin.Applied && pin.Acked && pin.Switched && pin.Durable && pins.HoldFor(pin) is null;

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
