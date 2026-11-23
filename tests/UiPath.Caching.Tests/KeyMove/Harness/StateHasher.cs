namespace UiPath.Caching.Tests.KeyMove.Harness;

// Two 64-bit FNV-style accumulators: the 128-bit result identifies a world state in the explorer's visited set.
internal sealed class StateHasher
{
    private ulong _a = 14695981039346656037UL;
    private ulong _b = 0x9E3779B97F4A7C15UL;

    public void Add(string? text)
    {
        if (text is null)
        {
            Mix(0xFFFF);
            return;
        }

        foreach (var c in text)
        {
            Mix(c);
        }

        Mix(0x1F);
    }

    public void Add(long value)
    {
        Mix((ulong)value & 0xFFFFFFFF);
        Mix((ulong)value >> 32);
        Mix(0x1E);
    }

    public (ulong, ulong) Result() => (_a, _b);

    private void Mix(ulong x)
    {
        _a = (_a ^ x) * 1099511628211UL;
        _b = ((_b << 7) | (_b >> 57)) ^ (x * 0x100000001B3UL + 0x7F4A7C15UL);
    }
}
