namespace UiPath.Caching.PhaseB;

/// <summary>The shortest base-36 hash tag of each slot, first in length-then-alphabet order so every node picks the same; the table is built once.</summary>
internal static class SlotTags
{
    public const int MaxLength = 4;

    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    private static readonly Lazy<string?[]> Table = new(BuildAll);

    public static string For(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, HashSlot.Count);
        return Table.Value[slot] ?? throw new InvalidOperationException($"No tag of at most {MaxLength} characters reaches slot {slot}.");
    }

    /// <summary>Every slot's tag, in one pass over the candidates; null for a slot no candidate reaches.</summary>
    internal static string?[] BuildAll()
    {
        var all = new string?[HashSlot.Count];
        var found = 0;
        Span<byte> candidate = stackalloc byte[MaxLength];
        for (var length = 1; length <= MaxLength && found < HashSlot.Count; length++)
        {
            var total = Pow36(length);
            for (var n = 0; n < total && found < HashSlot.Count; n++)
            {
                Spell(n, candidate[..length]);
                var slot = HashSlot.Crc16(candidate[..length]) & (HashSlot.Count - 1);
                if (all[slot] is null)
                {
                    all[slot] = System.Text.Encoding.ASCII.GetString(candidate[..length]);
                    found++;
                }
            }
        }

        return all;
    }

    // Most significant character first, so the order is the alphabetical order of the tags of one length.
    private static void Spell(int n, Span<byte> into)
    {
        for (var i = into.Length - 1; i >= 0; i--)
        {
            into[i] = (byte)Alphabet[n % Alphabet.Length];
            n /= Alphabet.Length;
        }
    }

    private static int Pow36(int length)
    {
        var total = 1;
        for (var i = 0; i < length; i++)
        {
            total *= Alphabet.Length;
        }

        return total;
    }
}
