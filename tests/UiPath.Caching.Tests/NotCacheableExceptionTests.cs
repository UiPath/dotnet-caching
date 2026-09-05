namespace UiPath.Caching.Tests;
public class NotCacheableExceptionTests
{
    [Theory]
    [InlineData(typeof(int?))]
    [InlineData(typeof(bool?))]
    [InlineData(typeof(string))]
    [InlineData(typeof(object))]
    [InlineData(typeof(TestStruct?))]
    [InlineData(typeof(TestClass))]
    [InlineData(typeof(ReadOnlyMemory<byte>))]   // default is empty memory, which the tiers already read as absent
    public void CanBeCached(Type type)
    {
        var act = () => NotCacheableException.ThrowIfNotCacheable(type);
        act.Should().NotThrow();

    }

    /// <summary>The allowance is for one type. Its mutable and multi-segment cousins stay out: a cache must not hand back a window it can be written through.</summary>
    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(TestStruct))]
    [InlineData(typeof(Memory<byte>))]
    [InlineData(typeof(ReadOnlyMemory<char>))]
    [InlineData(typeof(System.Buffers.ReadOnlySequence<byte>))]
    public void CanNotBeCached(Type type)
    {
        var act = () => NotCacheableException.ThrowIfNotCacheable(type);
        act.Should().Throw<NotCacheableException>();
    }

    public struct TestStruct
    {
    }

    public class TestClass
    {
    }
}
