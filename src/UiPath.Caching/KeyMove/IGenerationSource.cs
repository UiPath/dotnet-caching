namespace UiPath.Caching.KeyMove;

// An unreadable generation is unknown even if the connection counter did not change.
internal interface IGenerationSource
{
    Task<ServerGeneration?> ReadAsync(RecordSide side, CancellationToken cancellationToken);

    async Task<GenerationPair?> ReadPairAsync(CancellationToken cancellationToken)
    {
        if (await ReadAsync(RecordSide.Src, cancellationToken).ConfigureAwait(false) is not { } src)
        {
            return null;
        }

        return await ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false) is { } dst ? new GenerationPair(src, dst) : null;
    }
}
