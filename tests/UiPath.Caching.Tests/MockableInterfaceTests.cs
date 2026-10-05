using System.Reflection;

namespace UiPath.Caching.Tests;

/// <summary>A proxy generator emits invalid IL for a method that takes a ref struct, so a public interface that consumers mock must not have one.</summary>
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
    public void No_public_interface_takes_a_ref_struct()
    {
        var offenders = LibraryAssemblies()
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.IsInterface && !SpanCapabilities.Contains(t))
            .SelectMany(t => t.GetMethods().Select(m => (Type: t, Method: m)))
            .Where(x => x.Method.GetParameters().Any(p => (p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType).IsByRefLike))
            .Select(x => $"{x.Type.FullName}.{x.Method.Name}")
            .Distinct()
            .Order()
            .ToArray();

        offenders.Should().BeEmpty();
    }

    private static IEnumerable<Assembly> LibraryAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "UiPath.Caching*.dll")
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom);
}
