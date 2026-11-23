using System.Text;

namespace UiPath.Caching.KeyMove;

/// <summary>The cluster slot of a key, computed as the server does, so names can be chosen without a connection.</summary>
internal static class HashSlot
{
    public const int Count = 16384;

    private static readonly ushort[] Table = BuildTable();

    public static int Of(string key) => Of(Encoding.UTF8.GetBytes(key));

    /// <summary>The slot of the physical key: <paramref name="prefix"/> as the connection prepends it, then the logical key.</summary>
    public static int Of(string prefix, string key) => Of(Encoding.UTF8.GetBytes(prefix + key));

    public static int Of(ReadOnlySpan<byte> key)
    {
        var open = key.IndexOf((byte)'{');
        if (open >= 0)
        {
            var close = key[(open + 1)..].IndexOf((byte)'}');
            if (close > 0)
            {
                key = key.Slice(open + 1, close);
            }
        }

        return Crc16(key) & (Count - 1);
    }

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var b in bytes)
        {
            crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
