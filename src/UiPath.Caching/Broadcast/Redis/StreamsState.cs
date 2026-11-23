namespace UiPath.Caching.Broadcast.Redis;

internal enum StreamsState
{
    /// <summary>Not probed yet, or the probe could not tell: a timeout, a loading server, no primary.</summary>
    Unknown,

    Supported,

    /// <summary>The server refused the Streams commands, or is known to run them badly. Sticky for the life of the process.</summary>
    Unsupported,
}
