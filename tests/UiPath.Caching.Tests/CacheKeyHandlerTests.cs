using System.Globalization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

[Collection(AllocationCollection.Name)]
public class CacheKeyHandlerTests
{
    private static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateTimeOffset At = new(2026, 10, 30, 12, 30, 45, TimeSpan.FromHours(2));

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void Handler_key_equals_the_interpolated_string_key(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var dto = At;
            var dt = new DateTime(2026, 10, 30, 12, 30, 45, DateTimeKind.Utc);
            var amount = 1234567.891;
            var price = 1234.5m;
            var id = Id;
            var count = -42;
            var number = 9_000_000_000L;
            string? none = null;
            var long1 = new string('y', 300);

            Check(Handle($"a:{dto}"), new CacheKey($"a:{dto}"), $"a:{dto}");
            Check(Handle($"a:{dto:o}:{dto:d}"), new CacheKey($"a:{dto:o}:{dto:d}"), $"a:{dto:o}:{dto:d}");
            Check(Handle($"a:{dt}"), new CacheKey($"a:{dt}"), $"a:{dt}");
            Check(Handle($"a:{amount}:{amount:F2}:{amount:N1}"), new CacheKey($"a:{amount}:{amount:F2}:{amount:N1}"), $"a:{amount}:{amount:F2}:{amount:N1}");
            Check(Handle($"a:{price}:{price:C}"), new CacheKey($"a:{price}:{price:C}"), $"a:{price}:{price:C}");
            Check(Handle($"a:{id}:{id:N}"), new CacheKey($"a:{id}:{id:N}"), $"a:{id}:{id:N}");
            Check(Handle($"a:{count}:{number}"), new CacheKey($"a:{count}:{number}"), $"a:{count}:{number}");
            Check(Handle($"a:{none}:b"), new CacheKey($"a:{none}:b"), $"a:{none}:b");
            Check(Handle($"{long1}:{long1}:{amount}"), new CacheKey($"{long1}:{long1}:{amount}"), $"{long1}:{long1}:{amount}");
            Check(Handle($"a:{id,8}|{count,-8}|{amount,20:F2}|{none,4}|{long1.AsSpan(0, 2),5}"), new CacheKey($"a:{id,8}|{count,-8}|{amount,20:F2}|{none,4}|{long1.AsSpan(0, 2),5}"), $"a:{id,8}|{count,-8}|{amount,20:F2}|{none,4}|{long1.AsSpan(0, 2),5}");
            Check(Handle($"a:{count,8}|{count,-8}|{amount,10:F2}"), new CacheKey($"a:{count,8}|{count,-8}|{amount,10:F2}"), $"a:{count,8}|{count,-8}|{amount,10:F2}");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task An_empty_interpolation_matches_the_empty_string_key()
    {
        var empty = string.Empty;
        var plain = Substitute.For<ICache>();
        var recorder = new SpanRecordingCache();
        ICache spanCache = recorder;

        await plain.GetAsync<string>($"", (CachePolicy?)null, TestContext.Current.CancellationToken);
        await plain.GetAsync<string>($"{empty}", (CachePolicy?)null, TestContext.Current.CancellationToken);
        await spanCache.GetAsync<string>($"", (CachePolicy?)null, TestContext.Current.CancellationToken);
        await spanCache.GetAsync<string>($"{empty}", (CachePolicy?)null, TestContext.Current.CancellationToken);

        plain.ReceivedCalls().Select(c => c.GetOriginalArguments()[0]).Should().OnlyContain(k => (CacheKey)k! == new CacheKey("") && ((CacheKey)k!).Name == string.Empty);
        recorder.Calls.Should().Equal([("GetAsync(policy)", ""), ("GetAsync(policy)", "")]);
    }

    [Fact]
    public void Handler_allocates_nothing_for_a_short_key()
    {
#if DEBUG
        Assert.Skip("allocation counts are only stable in Release");
#endif
        var recorder = new SpanRecordingCache { RecordKeys = false };
        ICache cache = recorder;
        var machine = "0123456789abcdef0123456789abcdef0123";
        long allocated = -1;
        for (var round = 0; round < 60 && allocated != 0; round++)
        {
            for (var i = 0; i < 300; i++)
            {
                Build(cache, Id, machine);
            }

            SpinWait.SpinUntil(static () => false, 100);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                Build(cache, Id, machine);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }

        allocated.Should().Be(0);
        recorder.LastLength.Should().Be(73);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hole_with_no_natural_type_formats_as_string_interpolation_does(bool flag)
    {
        var text = Handle($"a:{(flag ? 1 : "x")}:{(flag ? 1 : "x"),5}:{(flag ? 2.5 : "x"):F1}");

        text.Should().Be($"a:{(flag ? 1 : "x")}:{(flag ? 1 : "x"),5}:{(flag ? 2.5 : "x"):F1}");
    }

    private static void Build(ICache cache, Guid tenant, string machine) =>
        Completed(cache.GetOrAddAsync($"{tenant}:{machine}", 1, static (_, _) => Task.FromResult<string?>("v"), (CachePolicy?)null));

    private static string Handle(CacheKeyHandler key)
    {
        var recorder = new SpanRecordingCache();
        ICache cache = recorder;
        Completed(cache.GetOrAddAsync(ref key, 1, static (_, _) => Task.FromResult<string?>("v"), (CachePolicy?)null));
        return recorder.SpanKeys.Single();
    }

    // The recording cache completes synchronously; reading the result inline keeps the allocation count on this thread.
    private static T Completed<T>(ValueTask<T> task) =>
        task.IsCompleted ? task.Result : throw new InvalidOperationException("The cache did not complete synchronously.");

    private static void Check(string handled, CacheKey expectedKey, string expectedText)
    {
        handled.Should().Be(expectedText);
        new CacheKey(handled).Should().Be(expectedKey);
    }
}
