namespace UiPath.Caching.Serialization;

/// <summary>The format id in front of a value. JSON stays headerless, and no JSON text starts with a byte this reads as a header.</summary>
internal static class ValueHeader
{
    private const byte ExtendedMarker = 0x1F;

    public static int Length(int formatId)
    {
        if (IsOneByteId(formatId))
        {
            return 1;
        }

        if (formatId is >= CacheValueFormats.FirstExtended and <= CacheValueFormats.LastExtended)
        {
            return 2;
        }

        throw new ArgumentOutOfRangeException(nameof(formatId), formatId, "Not an id that reads back as a header.");
    }

    public static int Write(Span<byte> destination, int formatId)
    {
        var length = Length(formatId);
        if (length == 1)
        {
            destination[0] = (byte)formatId;
            return 1;
        }

        destination[0] = ExtendedMarker;
        destination[1] = (byte)formatId;
        return 2;
    }

    /// <summary>False means headerless JSON; a lone extended marker is a header whose <paramref name="formatId"/> is -1.</summary>
    public static bool TryRead(ReadOnlySpan<byte> value, out int formatId, out int headerLength)
    {
        formatId = 0;
        headerLength = 0;
        if (value.IsEmpty || !IsHeaderByte(value[0]))
        {
            return false;
        }

        if (value[0] != ExtendedMarker)
        {
            formatId = value[0];
            headerLength = 1;
            return true;
        }

        if (value.Length < 2)
        {
            formatId = -1;
            headerLength = 1;
            return true;
        }

        formatId = CacheValueFormats.FirstExtended | value[1];
        headerLength = 2;
        return true;
    }

    /// <summary>
    /// Whether a body starts with a format id this library writes (<c>0x02</c>, <c>0x03</c>) or one a consumer may register, so a node that
    /// cannot read it lost what it carried. The other library ids are unused, so a foreign producer's first byte such as <c>0x08</c> is junk.
    /// </summary>
    public static bool IsKnownFormat(ReadOnlySpan<byte> value) =>
        TryRead(value, out var formatId, out _)
        && (formatId is -1 or CacheValueFormats.MessagePack or CacheValueFormats.MessagePackLz4
            || formatId is >= CacheValueFormats.FirstConsumer and <= CacheValueFormats.LastConsumer
            || formatId is >= CacheValueFormats.FirstExtended and <= CacheValueFormats.LastExtended);

    private static bool IsOneByteId(int id) => id is >= 0x01 and <= 0x1E && IsHeaderByte((byte)id);

    private static bool IsHeaderByte(byte b) => b is >= 0x01 and <= 0x1F and not 0x09 and not 0x0A and not 0x0D;
}
