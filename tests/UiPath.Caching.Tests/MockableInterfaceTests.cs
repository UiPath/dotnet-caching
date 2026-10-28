using System.Reflection;

namespace UiPath.Caching.Tests;

/// <summary>A proxy generator emits invalid IL for a method that takes a ref struct, so a public interface that consumers mock must neither take nor return one in a member a proxy overrides; a sealed member has a body no proxy touches.</summary>
public class MockableInterfaceTests
{
    // Span by design and never mocked: the extensions and the key composition only look for them on a real implementation.
    private static readonly HashSet<Type> SpanCapabilities =
    [
        typeof(ISpanKeyCache),
        typeof(ISpanKeyCache<>),
        typeof(ISpanKeyHashCache),
        typeof(ISpanKeyHashCache<>),
        typeof(ISpanCacheKeyStrategy),
    ];

    [Fact]
    public void No_public_interface_takes_or_returns_a_ref_struct()
    {
        var offenders = LibraryAssemblies()
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.IsInterface && !SpanCapabilities.Contains(t))
            .SelectMany(t => t.GetMethods().Where(m => m.IsVirtual).Select(m => (Type: t, Method: m)))
            .Where(x => IsRefStruct(x.Method.ReturnType) || x.Method.GetParameters().Any(p => IsRefStruct(p.ParameterType)))
            .Select(x => $"{x.Type.FullName}.{x.Method.Name}")
            .Distinct()
            .Order()
            .ToArray();

        offenders.Should().BeEmpty();
    }

    // On Castle.Core 5.2.1 an NSubstitute substitute throws on any call to an overridable default member (castleproject/Core#684).
    [Theory]
    [InlineData(typeof(ICacheFactory))]
    [InlineData(typeof(ICache))]
    [InlineData(typeof(ICache<>))]
    [InlineData(typeof(IHashCache))]
    [InlineData(typeof(IHashCache<>))]
    [InlineData(typeof(IPolicyDefaults))]
    [InlineData(typeof(ICachePolicyFactory))]
    [InlineData(typeof(ITopicFactory))]
    public void A_commonly_mocked_interface_has_no_overridable_default_member(Type type)
    {
        var defaults = type.GetInterfaces().Prepend(type)
            .SelectMany(t => t.GetMethods())
            .Where(m => m.IsVirtual && !m.IsAbstract)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToArray();

        defaults.Should().BeEmpty();
    }

    private static bool IsRefStruct(Type type) => (type.IsByRef ? type.GetElementType()! : type).IsByRefLike;

    private static IEnumerable<Assembly> LibraryAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "UiPath.Caching*.dll")
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom);
}
