using System.Reflection;

namespace UiPath.Caching.Tests.Broadcast;

public class OptionsCloneTests
{
    [Fact]
    public void RedisStreamsTopicOptions_Clone_copies_every_settable_property()
    {
        var source = new RedisStreamsTopicOptions();
        MutateAllSettableProperties(source);

        var clone = source.Clone();

        AssertAllSettablePropertiesEqual(source, clone);
        clone.Should().NotBeSameAs(source);
    }

    [Fact]
    public void RedisPubSubTopicOptions_Clone_copies_every_settable_property()
    {
        var source = new RedisPubSubTopicOptions();
        MutateAllSettableProperties(source);

        var clone = source.Clone();

        AssertAllSettablePropertiesEqual(source, clone);
        clone.Should().NotBeSameAs(source);
    }

    private static void MutateAllSettableProperties(object target)
    {
        // Public properties only: the options classes are public POCOs whose contract
        // is the public surface. Internal properties (if any are ever added) would
        // need a separate guard.
        foreach (var prop in target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite))
        {
            prop.SetValue(target, MakeNonDefault(prop.PropertyType, prop.GetValue(target)));
        }
    }

    private static void AssertAllSettablePropertiesEqual(object expected, object actual)
    {
        foreach (var prop in expected.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite))
        {
            var e = prop.GetValue(expected);
            var a = prop.GetValue(actual);
            a.Should().Be(e, $"property {prop.Name} should be copied by Clone()");
        }
    }

    private static object? MakeNonDefault(Type type, object? current)
    {
        if (type == typeof(bool)) return !(bool)(current ?? false);
        if (type == typeof(bool?)) return !(((bool?)current) ?? false);
        if (type == typeof(int)) return ((int?)current ?? 0) + 17;
        if (type == typeof(long)) return ((long?)current ?? 0L) + 31L;
        if (type == typeof(long?)) return (((long?)current) ?? 0L) + 31L;
        if (type == typeof(string)) return Guid.NewGuid().ToString("N");
        if (type == typeof(TimeSpan)) return ((TimeSpan?)current ?? TimeSpan.Zero) + TimeSpan.FromSeconds(7);
        if (type == typeof(TimeSpan?)) return (((TimeSpan?)current) ?? TimeSpan.Zero) + TimeSpan.FromSeconds(7);
        if (type == typeof(System.Threading.Channels.BoundedChannelFullMode))
        {
            var cur = (System.Threading.Channels.BoundedChannelFullMode)(current ?? System.Threading.Channels.BoundedChannelFullMode.Wait);
            return cur == System.Threading.Channels.BoundedChannelFullMode.Wait
                ? System.Threading.Channels.BoundedChannelFullMode.DropOldest
                : System.Threading.Channels.BoundedChannelFullMode.Wait;
        }
        if (type == typeof(IRedisStreamKeyStrategy)) return Substitute.For<IRedisStreamKeyStrategy>();
        if (type == typeof(IRedisChannelStrategy)) return Substitute.For<IRedisChannelStrategy>();
        throw new NotSupportedException($"Add a non-default factory for {type.FullName}");
    }
}
