using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration;

/// <summary>Picks the write and read policy of one call by its group.</summary>
/// <remarks>
/// A write is MessagePack only while the group is switched and the gate is open, the envelope in every other case.
/// The setting is read on every write. An answer that throws, or that names no known format, writes the envelope and clears the group's wanted bit: the group is never moved toward MessagePack by a setting that cannot be read, and holds the gate no longer.
/// The library still applies its own negotiation to a MessagePack write, so a node that joined without the pin turns the write back to the envelope there.
/// </remarks>
internal sealed partial class WriteFormatSelector(Func<string, string?> format, PinCoordinator pins, ILogger logger)
{
    private readonly HashSet<string> _failed = [];

    /// <summary>The policy of one read: none while the group is ready for MessagePack, else the envelope, so a stack-wide rewrite on read never converts a group that is not.</summary>
    /// <remarks>
    /// Ready is the group's own state: it asks for MessagePack and is applied, acknowledged, switched and durable. The gate is not asked, since the library holds a rewrite on its own gate, which covers every group that asks.
    /// The envelope policy names no rewrite target, so a default <c>MigrateOnRead</c> does nothing for it.
    /// </remarks>
    public CachePolicy? ReadPolicy(GroupPin pin) => pin.Wanted && pin.Applied && pin.Acked && pin.Switched && pin.Durable ? null : WriteFormats.Envelope;

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
            pins.Want(pin, false);
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
        try
        {
            if (Ready(pin))
            {
                return new(WriteFormats.MessagePackPolicy, pin);
            }
        }
        catch
        {
            pin.ExitWriting();
            throw;
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
