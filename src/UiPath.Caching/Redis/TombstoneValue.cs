using System.Buffers;
using System.Buffers.Text;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

/// <summary>What a removal leaves behind when <see cref="RedisCacheOptions.RemoveTombstone"/> is set: the reserved header byte, the grace in milliseconds as digits, a colon, and a nonce of hex digits that is new with every write, so two removals never leave equal bytes.</summary>
internal static class TombstoneValue
{
    public const byte Header = FormatIdRules.TombstoneId;

    /// <summary>The longest a tombstone can be: the header, the digits of the largest grace, the separator and the nonce.</summary>
    public const int MaxLength = 1 + 19 + 1 + NonceLength;

    private const byte Separator = (byte)':';
    private const int NonceLength = 16;

    public static byte[] Create(long graceMilliseconds) => Create(graceMilliseconds, Random.Shared.NextInt64());

    public static byte[] Create(long graceMilliseconds, long nonce)
    {
        var digits = DigitCount(graceMilliseconds);
        var bytes = new byte[1 + digits + 1 + NonceLength];
        bytes[0] = Header;
        Utf8Formatter.TryFormat(graceMilliseconds, bytes.AsSpan(1), out _);
        bytes[1 + digits] = Separator;
        Utf8Formatter.TryFormat((ulong)nonce, bytes.AsSpan(2 + digits), out _, new StandardFormat('x', NonceLength));
        return bytes;
    }

    public static bool Is(RedisValue value) => TryRead(value, out _);

    public static bool TryRead(RedisValue value, out long graceMilliseconds)
    {
        graceMilliseconds = 0;
        if (value.IsNull || value.Length() < 2)
        {
            return false;
        }

        var bytes = ((ReadOnlyMemory<byte>)value).Span;
        if (bytes[0] != Header)
        {
            return false;
        }

        var separator = bytes.IndexOf(Separator);
        return separator > 1
            && Utf8Parser.TryParse(bytes[1..separator], out graceMilliseconds, out var consumed)
            && consumed == separator - 1
            && graceMilliseconds > 0
            && IsNonce(bytes[(separator + 1)..]);
    }

    /// <summary>The first two bytes of a tombstone: the header and a digit.</summary>
    public static bool IsHead(ReadOnlySpan<byte> head) => head.Length >= 2 && head[0] == Header && head[1] is >= (byte)'0' and <= (byte)'9';

    /// <summary>Whether a fill whose generator ran for <paramref name="generatorElapsedMilliseconds"/> (null for none) may write over a tombstone: its age is the grace less what is left.</summary>
    public static TombstoneVerdict Judge(long graceMilliseconds, TimeSpan? left, long? generatorElapsedMilliseconds)
    {
        if (generatorElapsedMilliseconds is not { } elapsed)
        {
            return TombstoneVerdict.Replace;
        }

        // No deadline, or one past the grace: something changed it, so its age is not known and it counts as young.
        if (left is not { } remaining)
        {
            return TombstoneVerdict.Unknown;
        }

        var age = graceMilliseconds - (long)Math.Ceiling(remaining.TotalMilliseconds);
        if (age < 0)
        {
            return TombstoneVerdict.Unknown;
        }

        // A tie is not older than the generator.
        return elapsed >= age ? TombstoneVerdict.Young : TombstoneVerdict.Replace;
    }

    private static bool IsNonce(ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceLength)
        {
            return false;
        }

        foreach (var c in nonce)
        {
            if (c is not ((>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static int DigitCount(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
}
