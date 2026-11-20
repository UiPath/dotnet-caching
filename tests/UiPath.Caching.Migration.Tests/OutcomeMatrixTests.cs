using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class OutcomeMatrixTests : RouterTestBase
{
    private const string Hit = "hit";
    private const string Miss = "miss";
    private const string Throws = "throws";

    public static TheoryData<bool, ReadOutcome, ReadOutcome, string> Matrix()
    {
        var data = new TheoryData<bool, ReadOutcome, ReadOutcome, string>();
        foreach (var library in new[] { true, false })
        {
            foreach (var lib in Enum.GetValues<ReadOutcome>())
            {
                foreach (var src in Enum.GetValues<ReadOutcome>())
                {
                    data.Add(library, lib, src, Expected(library, lib, src));
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Every_combination_of_path_library_outcome_and_source_outcome_ends_as_the_table_says(bool library, ReadOutcome lib, ReadOutcome src, string expected)
    {
        var node = NewNode(useLibrary: _ => library, localCap: TimeSpan.Zero);
        Store["k"] = new Item("stored");
        node.LibraryBackend.ReadOverride = lib;
        node.SourceBackend.ReadOverride = src;
        var factoryRuns = 0;
        Task<Item?> Factory(CancellationToken token)
        {
            factoryRuns++;
            return Task.FromResult<Item?>(new Item("made"));
        }

        var call = async () => await node.Cache.GetOrAddAsync("k", Factory, default, Ct);

        if (expected == Throws)
        {
            await call.Should().ThrowAsync<CacheUnavailableException>();
            factoryRuns.Should().Be(0, "no answer from either client never runs the factory");
            return;
        }

        var value = await call();
        value.Should().Be(expected == Hit ? new Item("stored") : new Item("made"));
        factoryRuns.Should().Be(expected == Hit ? 0 : 1);
    }

    [Fact]
    public async Task A_library_read_that_times_out_is_unavailable_and_never_runs_the_factory()
    {
        var store = new FakeMigrationCache { Read = (_, _) => throw new TimeoutException("redis timeout") };
        var node = NewNode(library: new LibraryPathClient<Item>(store, new FakeConnection(), key => key, TimeSpan.FromSeconds(1)));
        node.SourceBackend.ReadOverride = ReadOutcome.Unavailable;
        var factoryRuns = 0;
        Task<Item?> Factory(CancellationToken token)
        {
            factoryRuns++;
            return Task.FromResult<Item?>(new Item("made"));
        }

        var call = async () => await node.Cache.GetOrAddAsync("k", Factory, default, Ct);

        await call.Should().ThrowAsync<CacheUnavailableException>();
        factoryRuns.Should().Be(0);
    }

    private static string Expected(bool library, ReadOutcome lib, ReadOutcome src)
    {
        var first = library ? lib : src;
        var second = library ? src : lib;
        if (first == ReadOutcome.Found)
        {
            return Hit;
        }

        if (first == ReadOutcome.Absent)
        {
            return Miss;
        }

        if (first == ReadOutcome.Unavailable)
        {
            if (!library)
            {
                return Miss;
            }

            return second switch
            {
                ReadOutcome.Found => Hit,
                ReadOutcome.Absent => Miss,
                _ => Throws,
            };
        }

        // The first client found the entry unreadable: the other client may read it.
        return second == ReadOutcome.Found ? Hit : Miss;
    }
}
