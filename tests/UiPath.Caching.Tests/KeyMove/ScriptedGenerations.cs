using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The real generation reader with a failover simulated per side: <see cref="Change"/> moves the generation for good, <see cref="Unknown"/> makes reads fail, and <see cref="Rewrite"/> scripts a read by count.</summary>
internal sealed class ScriptedGenerations(IGenerationSource real) : IGenerationSource
{
    private readonly int[] _shift = new int[2];
    private readonly bool[] _unknown = new bool[2];
    private readonly int[] _reads = new int[2];

    /// <summary>Answers the Nth read of a side (1-based) with the real generation, or null for unknown, replacing it by the return value.</summary>
    public Func<RecordSide, int, ServerGeneration?, ServerGeneration?>? Rewrite { get; set; }

    /// <summary>How many reads of the side were made.</summary>
    public int Reads(RecordSide side) => Volatile.Read(ref _reads[(int)side]);

    /// <summary>A failover of the side: every later read shows another generation.</summary>
    public void Change(RecordSide side) => Interlocked.Increment(ref _shift[(int)side]);

    /// <summary>Reads of the side fail (or work again) from now on.</summary>
    public void Unknown(RecordSide side, bool unknown) => _unknown[(int)side] = unknown;

    /// <summary>The proof of a write on the side as the simulated failovers show it: the run id carries the same shift as <see cref="ReadAsync"/>.</summary>
    public ConnectionProof Shifted(RecordSide side, ConnectionProof proof)
    {
        var shift = Volatile.Read(ref _shift[(int)side]);
        return shift == 0 ? proof : proof with { RunId = $"{proof.RunId}#{shift}" };
    }

    public async Task<ServerGeneration?> ReadAsync(RecordSide side, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _reads[(int)side]);
        var actual = await real.ReadAsync(side, cancellationToken);
        if (Rewrite is { } rewrite)
        {
            return rewrite(side, count, actual);
        }

        if (_unknown[(int)side] || actual is not { } generation)
        {
            return null;
        }

        var shift = Volatile.Read(ref _shift[(int)side]);
        return shift == 0 ? generation : generation with { RunId = $"{generation.RunId}#{shift}" };
    }
}
