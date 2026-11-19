namespace UiPath.Caching.Migration;

/// <summary>Reads the configuration of the multiplexer behind the library once it is connected, without ever waiting for a connection, and keeps it across disconnections and replacements.</summary>
internal sealed class LibrarySettleSnapshot(Func<TimeSpan> pipeline, Func<string?> tryLiveConfiguration)
{
    private TimeSpan? _command;

    public TimeSpan Current()
    {
        var command = _command;
        if (command is null && Read() is { } live)
        {
            command = LibrarySettleBound.CommandTimeout(live);
            _command = command;
        }

        return pipeline() + (command ?? LibrarySettleBound.Ceiling);
    }

    private string? Read()
    {
        try
        {
            return tryLiveConfiguration();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
