namespace UiPath.Caching.Redis;

/// <summary>Runs one command per Redis Cluster slot group of a multi-key call.</summary>
internal static class SlotFanOut
{
    // Commands, one per slot group; the same count the batch writes keep in flight, at one command per key.
    internal const int MaxInFlight = InMemoryRedisCacheOptions.DefaultBatchChunkSize * InMemoryRedisCacheOptions.DefaultBatchConcurrentChunks;

    /// <summary>Every group in flight together, up to <paramref name="maxInFlight"/>; of several, one that throws answers <paramref name="failed"/>.</summary>
    public static async Task<TResult[]> RunAsync<TState, TResult>(
        int[][] groups,
        TState state,
        Func<TState, int[], ValueTask<TResult>> run,
        TResult failed,
        Action<Exception> logFailure,
        int maxInFlight = MaxInFlight)
    {
        if (groups.Length == 1)
        {
            return [await run(state, groups[0]).ConfigureAwait(false)];
        }

        using var gate = groups.Length > maxInFlight ? new SemaphoreSlim(maxInFlight) : null;
        var runs = new Task<TResult>[groups.Length];
        for (var i = 0; i < groups.Length; i++)
        {
            runs[i] = RunGroupAsync(run, state, groups[i], failed, logFailure, gate);
        }

        return await Task.WhenAll(runs).ConfigureAwait(false);

        // A group that throws before its first await must not return the caller, which frees the write payloads, while the others still write.
        static async Task<TResult> RunGroupAsync(Func<TState, int[], ValueTask<TResult>> run, TState state, int[] group, TResult failed, Action<Exception> logFailure, SemaphoreSlim? gate)
        {
            if (gate is not null)
            {
                await gate.WaitAsync().ConfigureAwait(false);
            }

            try
            {
                return await run(state, group).ConfigureAwait(false);
            }
            // A cancelled call fails whole, as it does on one slot; the caller still waits for the other groups.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logFailure(ex);
                return failed;
            }
            finally
            {
                gate?.Release();
            }
        }
    }
}
