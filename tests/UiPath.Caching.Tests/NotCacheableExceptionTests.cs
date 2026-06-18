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
    public void CanBeCached(Type type)
    {
        var act = () => NotCacheableException.ThrowIfNotCacheable(type);
        act.Should().NotThrow();

    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(TestStruct))]
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
