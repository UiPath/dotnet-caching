namespace UiPath.Caching.Serialization;

/// <summary>The JSON default: no header written, none looked for.</summary>
internal readonly struct PlainFormat : IValueFormat
{
    public ICacheValueSerializer? Writer => null;

    public bool Sniff => false;

    public bool Handles(Exception exception) => true;
}
