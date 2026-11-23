namespace UiPath.Caching.Redis;

/// <summary>An extra condition on writing a non-JSON value format, on top of what the node list shows; register one in the cache stack's services.</summary>
public interface IFormatWriteGate
{
    /// <summary>Null when writes may go ahead, otherwise why not; read on every write, so cheap and never throwing.</summary>
    string? Hold();
}
