namespace UiPath.Caching.MessagePack;

public static class MessagePackFormats
{
    public const int Value = 0x02;
    public const int ValueLz4 = 0x03;
    public const int Event = 0x02;

    public static IReadOnlyList<int> ValueIds { get; } = [Value, ValueLz4];
}
