using System.Reflection;
using System.Runtime.CompilerServices;
using UiPath.Caching.Config;

namespace UiPath.Caching.PluginContract.Tests;

public class PublicSurfaceGuardTests
{
    [Fact]
    public void No_assembly_grants_this_project_access_to_its_internals()
    {
        var self = typeof(PublicSurfaceGuardTests).Assembly.GetName().Name;
        var libraries = new[] { typeof(CachingBuilder).Assembly, typeof(ICacheValueSerializer).Assembly };

        var granted = libraries.SelectMany(a => a.GetCustomAttributes<InternalsVisibleToAttribute>())
            .Select(a => new AssemblyName(a.AssemblyName).Name);

        granted.Should().NotContain(self, "a test that compiles here must use a seam a third-party plugin has too");
    }
}
