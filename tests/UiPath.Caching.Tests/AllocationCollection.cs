namespace UiPath.Caching.Tests;

/// <summary>Tests that measure allocated bytes run alone: other tests allocating or collecting at the same time would skew the count and slow the rest of the suite with GC pressure.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationCollection
{
    public const string Name = "Allocation measuring";
}
