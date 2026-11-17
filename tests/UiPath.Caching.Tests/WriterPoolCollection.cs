namespace UiPath.Caching.Tests;

/// <summary>Tests that swap <see cref="PooledJsonWriter.Pool"/> run alone: two swaps at once would record each other's arrays and restore the wrong pool.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WriterPoolCollection
{
    public const string Name = "PooledJsonWriter pool";

    private WriterPoolCollection()
    {
        // Never constructed: the type only carries the collection definition.
    }
}
