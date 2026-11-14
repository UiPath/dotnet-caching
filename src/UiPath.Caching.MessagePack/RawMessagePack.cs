namespace UiPath.Caching.MessagePack;

internal readonly struct RawMessagePack(ReadOnlyMemory<byte> bytes)
{
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;
}
