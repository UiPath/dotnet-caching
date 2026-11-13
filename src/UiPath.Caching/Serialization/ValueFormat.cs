namespace UiPath.Caching.Serialization;

/// <summary>What one call writes with and which formats its reads accept, or null when they look for no format header; the default is JSON with no sniffing.</summary>
internal readonly record struct ValueFormat(ICacheValueSerializer? Writer, ReadableFormats? Readable) : IValueFormat
{
    public bool Sniff => Readable is not null;

    public bool IsDefault => Writer is null && Readable is null;

    public bool Handles(Exception exception) => exception is not OutOfMemoryException;
}
