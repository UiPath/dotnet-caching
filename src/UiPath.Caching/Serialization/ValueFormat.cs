namespace UiPath.Caching.Serialization;

/// <summary>What one call writes with and whether its reads look for a format header; the default is JSON with no sniffing.</summary>
internal readonly record struct ValueFormat(ICacheValueSerializer? Writer, bool Sniff) : IValueFormat
{
    public bool IsDefault => Writer is null && !Sniff;

    public bool Handles(Exception exception) => exception is not OutOfMemoryException;
}
