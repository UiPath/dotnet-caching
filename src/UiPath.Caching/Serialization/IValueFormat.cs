namespace UiPath.Caching.Serialization;

/// <summary>What a Redis path asks of a call's format; implemented by structs, so a path compiled for <see cref="PlainFormat"/> carries none of the header steps.</summary>
internal interface IValueFormat
{
    ICacheValueSerializer? Writer { get; }

    bool Sniff { get; }

    /// <summary>The formats a read accepts; null when it looks for no header.</summary>
    ReadableFormats? Readable { get; }

    /// <summary>Whether a read that failed with <paramref name="exception"/> logs it and reads as a miss.</summary>
    bool Handles(Exception exception);
}
