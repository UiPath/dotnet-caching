using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class PathTests : RouterTestBase
{
    [Fact]
    public async Task The_predicate_is_read_once_per_call_and_the_whole_call_stays_on_that_path()
    {
        var reads = 0;
        var node = NewNode(useLibrary: _ => reads++ == 0);

        var value = await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);

        value.Should().Be(new Item("made"));
        reads.Should().Be(1);
        node.LibraryBackend.Reads.Should().Be(1);
        node.LibraryBackend.Writes.Should().Be(1, "the write follows the path the read took");
        node.SourceBackend.Reads.Should().Be(0);
        node.SourceBackend.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_predicate_that_throws_sends_the_call_to_the_source()
    {
        using var meters = new MeterCollector(CacheName);
        var node = NewNode(useLibrary: _ => throw new InvalidOperationException("flag store down"));

        await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);

        node.SourceBackend.Reads.Should().Be(1);
        node.SourceBackend.Writes.Should().Be(1);
        node.LibraryBackend.Reads.Should().Be(0);
        meters.Count("miss", "path_reason", "predicate_error").Should().Be(1);
        meters.Count("miss", "path", "source").Should().Be(1);
    }

    [Fact]
    public async Task The_predicate_gets_the_group_of_the_cache()
    {
        var seen = new List<string>();
        var node = NewNode(useLibrary: group =>
        {
            seen.Add(group);
            return true;
        });

        await node.Cache.TryGetAsync("k", Ct);

        seen.Should().Equal("g");
    }

    [Fact]
    public async Task A_flip_changes_which_client_reads_on_a_miss_and_not_the_local_copy()
    {
        var library = true;
        var node = NewNode(useLibrary: _ => library);
        Store["k"] = new Item("stored");
        (await node.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("stored"));
        library = false;

        var again = await node.Cache.GetOrAddAsync("k", _ => throw new InvalidOperationException("no factory run for a stored entry"), default, Ct);

        again.Should().Be(new Item("stored"));
        node.SourceBackend.Reads.Should().Be(0, "the local copy served the call");
        node.LibraryBackend.Reads.Should().Be(1);
    }
}
