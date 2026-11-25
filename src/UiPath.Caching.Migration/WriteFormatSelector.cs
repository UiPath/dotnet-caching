using Microsoft.Extensions.Logging;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Picks the write and read policy of one call by its group.</summary>
/// <remarks>
/// A write is MessagePack only while the group is switched and the gate is open, the envelope in every other case (the library's own JSON when the router has none).
/// The setting is read on every write. An answer that throws, or that names no known format, writes the envelope and clears the group's wanted bit: the group is never moved toward MessagePack by a setting that cannot be read, and holds the gate no longer.
/// The library still applies its own negotiation to a MessagePack write, so a node that joined without the pin turns the write back to the envelope there.
/// A group whose keys are moving keeps the format it has: a switch that has not happened waits until the move is over, so promoters, the mover and a reverse never meet L in two formats.
/// </remarks>
internal sealed partial class WriteFormatSelector(Func<string, string?> format, PinCoordinator pins, ILogger logger)
{
    private const string Held = "format_switch_held";

    private readonly HashSet<string> _failed = [];
    private readonly HashSet<string> _held = [];

    /// <summary>True while a group must keep its format for a key move; null where the node takes no part in key move. The build sets it once, before the first write.</summary>
    public Func<string, bool>? MoveHold { get; set; }

    /// <summary>Told when a group's switch is held for its move, with the group and the pending format.</summary>
    public Action<KeyMoveAlert>? Alert { get; set; }

    /// <summary>True while the node acts under a state in which the group's keys move (S1 to S4, either direction).</summary>
    public static bool Moving(GroupRules rules) => rules.Acting is { State: "S1" or "S2" or "S3" or "S4" };

    /// <summary>The hold of the node's own key move groups: until the node has read a group's state it may be in the middle of a move, as a node that restarted during one is, so it holds then too.</summary>
    public static Func<string, bool> HoldFrom(GroupStateMonitor monitor, IReadOnlySet<string> groups) =>
        group => groups.Contains(group) && (!monitor.HasRead(group) || Moving(monitor.RulesFor(group)));

    /// <summary>The policy of one read: none while the group is ready for MessagePack, else the envelope, so a stack-wide rewrite on read never converts a group that is not.</summary>
    /// <remarks>
    /// Ready is the group's own state: it asks for MessagePack and is applied, acknowledged, switched and durable. The gate is not asked, since the library holds a rewrite on its own gate, which covers every group that asks.
    /// The envelope policy names no rewrite target, so a default <c>MigrateOnRead</c> does nothing for it.
    /// </remarks>
    public static CachePolicy? ReadPolicy(GroupPin pin) => pin.Wanted && pin.Applied && pin.Acked && pin.Switched && pin.Durable ? null : WriteFormats.Envelope;

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
        if (wanted && HoldsForMove(pin, answer!))
        {
            pins.Want(pin, false);
            return new(WriteFormats.Envelope, null);
        }

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

    // A group that has not switched yet waits while its keys move; one that switched before keeps its format, since the move started under it.
    private bool HoldsForMove(GroupPin pin, string pending)
    {
        if (pin.Switched || MoveHold is not { } holds || !holds(pin.Group))
        {
            Release(pin.Group);
            return false;
        }

        RouterTelemetry.CountFormatSwitchHeld(pin.Group, pending);
        bool first;
        lock (_held)
        {
            first = _held.Add(pin.Group);
        }

        if (first)
        {
            LogHeld(pin.Group, pending);
            Alert?.Invoke(new KeyMoveAlert(pin.Group, Held, pending));
        }

        return true;
    }

    private void Release(string group)
    {
        bool released;
        lock (_held)
        {
            released = _held.Remove(group);
        }

        if (released)
        {
            LogReleased(group);
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Group {Group} asks for the {Pending} format while its keys move; it keeps its current format until the move is over.")]
    private partial void LogHeld(string group, string pending);

    [LoggerMessage(Level = LogLevel.Information, Message = "The move of group {Group} is over; its write format switch goes ahead.")]
    private partial void LogReleased(string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The write format setting for group {Group} threw; the group writes the envelope.")]
    private partial void LogFailed(Exception exception, string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The write format setting for group {Group} names no known format (messagepack, json or null); the group writes the envelope.")]
    private partial void LogUnknown(string group);
}
