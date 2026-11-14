using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>With fail-safe off, a miss that generates and stores allocates what it did before fail-safe existed.</summary>
[Collection(AllocationCollection.Name)]
public class GeneratedValueStoreAllocationTests
{
    // Measured by this test on the commit fail-safe branched from (4301cc87), on net8.0 and net10.0 alike, in this same harness. A field or an
    // awaiter added to a frame the ordinary run shares shows as 8 bytes or more; a frame added shows as a state machine. A change to the harness
    // moves both numbers: measure them again on a build without the change under test.
    private const long PlainBytes = 4064;
    private const long GeneratedBytes = 4192;

    private static readonly Task<string?> Plain = Task.FromResult<string?>("v");
    private static readonly Task<GeneratedValue<string>> Generated = Task.FromResult(new GeneratedValue<string>("v"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_miss_that_waits_for_its_inner_write_allocates_no_more_than_before_fail_safe()
    {
        using var peers = new CachePeers();
        var node = peers.Add("a");
        var policy = new CachePolicy();
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Inner.AfterWrite = written.Task;
        var pending = new List<Task>();
        var plain = long.MaxValue;
        var generated = long.MaxValue;

        // Each call misses, generates and stops at its inner write: what it allocated by then includes one state machine per frame that waits.
        for (var round = 0; round < 200; round++)
        {
            CacheKey plainKey = $"p:{round:D3}";
            CacheKey generatedKey = $"g:{round:D3}";
            var before = GC.GetAllocatedBytesForCurrentThread();
            var first = node.Cache.GetOrAddAsync(plainKey, static _ => Plain, policy, Ct);
            var between = GC.GetAllocatedBytesForCurrentThread();
            var second = node.Cache.GetOrAddWithExpirationAsync(generatedKey, static _ => Generated, policy, Ct);
            var after = GC.GetAllocatedBytesForCurrentThread();
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
            pending.Add(first.AsTask());
            pending.Add(second.AsTask());
            plain = Math.Min(plain, between - before);
            generated = Math.Min(generated, after - between);
        }

        written.SetResult();
        await Task.WhenAll(pending);

        plain.Should().BeLessThanOrEqualTo(PlainBytes, "a plain generator's run");
        generated.Should().BeLessThanOrEqualTo(GeneratedBytes, "the run of a generator that names its expiration");
    }
}
