namespace UiPath.Caching.Migration;

/// <summary>The write formats a group can ask for and the per-call policy that carries each to the library.</summary>
/// <remarks>The envelope policy names <c>json</c>, which for a header-free envelope is the proxy's own bytes; the MessagePack policy names the serializer the application registered under that name.</remarks>
internal static class WriteFormats
{
    public const string MessagePack = "messagepack";
    public const string Json = "json";

    public static CachePolicy Envelope { get; } = new() { Serializer = new SerializerPolicy { Write = Json } };

    public static CachePolicy MessagePackPolicy { get; } = new() { Serializer = new SerializerPolicy { Write = MessagePack } };

    /// <summary>True for the one answer that wants MessagePack; a null answer is JSON.</summary>
    public static bool WantsMessagePack(string? answer) => string.Equals(answer, MessagePack, StringComparison.OrdinalIgnoreCase);

    /// <summary>False for an answer that is neither a known format nor null.</summary>
    public static bool IsKnown(string? answer) => answer is null || WantsMessagePack(answer) || string.Equals(answer, Json, StringComparison.OrdinalIgnoreCase);
}
