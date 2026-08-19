using System.Buffers.Binary;

namespace UiPath.Caching.Distributed;

/// <summary>IDistributedCache payload + expiration metadata. Layout: "UPDC" magic, version, flags (bit0 sliding, bit1 absolute), optional LE sliding ticks, optional LE absolute UtcTicks, payload.</summary>
internal sealed class DistributedCacheEnvelope(byte[] data, long? slidingTicks, DateTimeOffset? absoluteExpiration)
{
    private const byte FormatVersion = 1;
    private const int HeaderLength = 6;

    private static ReadOnlySpan<byte> Magic => "UPDC"u8;

    public byte[] Data { get; } = data;

    public long? SlidingTicks { get; } = slidingTicks;

    public DateTimeOffset? AbsoluteExpiration { get; } = absoluteExpiration;

    public byte[] Encode()
    {
        var length = HeaderLength
            + (SlidingTicks.HasValue ? sizeof(long) : 0)
            + (AbsoluteExpiration.HasValue ? sizeof(long) : 0)
            + Data.Length;
        var buffer = new byte[length];
        Magic.CopyTo(buffer);
        buffer[4] = FormatVersion;
        buffer[5] = (byte)((SlidingTicks.HasValue ? 1 : 0) | (AbsoluteExpiration.HasValue ? 2 : 0));
        var offset = HeaderLength;
        if (SlidingTicks is { } sliding)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), sliding);
            offset += sizeof(long);
        }

        if (AbsoluteExpiration is { } absolute)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), absolute.UtcTicks);
            offset += sizeof(long);
        }

        Data.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    public static DistributedCacheEnvelope? TryDecode(byte[]? value)
    {
        if (value is null || value.Length < HeaderLength)
        {
            return null;
        }

        var span = value.AsSpan();
        if (!span[..4].SequenceEqual(Magic) || span[4] != FormatVersion)
        {
            return null;
        }

        var flags = span[5];
        var offset = HeaderLength;
        long? sliding = null;
        DateTimeOffset? absolute = null;
        if ((flags & 1) != 0)
        {
            if (span.Length < offset + sizeof(long))
            {
                return null;
            }

            sliding = BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);
            if (sliding <= 0)
            {
                return null;
            }

            offset += sizeof(long);
        }

        if ((flags & 2) != 0)
        {
            if (span.Length < offset + sizeof(long))
            {
                return null;
            }

            var ticks = BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);
            if ((ulong)ticks > (ulong)DateTime.MaxValue.Ticks)
            {
                return null;
            }

            absolute = new DateTimeOffset(ticks, TimeSpan.Zero);
            offset += sizeof(long);
        }

        return new DistributedCacheEnvelope(span[offset..].ToArray(), sliding, absolute);
    }
}
