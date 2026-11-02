namespace UiPath.Caching.Broadcast;

/// <summary>Tells whether a payload came from one source.</summary>
public interface IEventSourceMatcher
{
    /// <summary>
    /// True only when the decoded event would name the source as <c>SameSource</c> compares it; false when it names
    /// another, or when that cannot be told without a decode.
    /// </summary>
    bool IsFromSource(ReadOnlyMemory<byte> payload);
}
