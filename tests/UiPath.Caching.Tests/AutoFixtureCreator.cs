using AutoFixture.Kernel;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests;

public static class AutoFixtureCreator
{
    public static IFixture NSubstitute() =>
        Create(new AutoNSubstituteCustomization { ConfigureMembers = true });

    private static IFixture Create(ICustomization customization)
    {
        var fixture = new Fixture()
            .Customize(customization);
        fixture.Customizations.Add(new CollectionPropertyOmitter());
        fixture.Customizations.Add(new MultilayerCacheOptionsCustomization());
        fixture.Customizations.Add(new TelemetryProviderCustomization());
        fixture.Customizations.Add(new CachePolicyFactoryCustomization());
        fixture.Customizations.Add(new CacheOptionsCustomization());

        fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList()
            .ForEach(b => fixture.Behaviors.Remove(b));
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());
        fixture.Behaviors.Add(new GenerationDepthBehavior(10));

        return fixture;
    }
}

public class TelemetryProviderCustomization : ISpecimenBuilder
{
    public object Create(object request, ISpecimenContext context)
    {
        if (request is Type type && type == typeof(ICachingTelemetryProvider))
        {
            return NullTelemetryProvider.Instance;
        }
        return new NoSpecimen();
    }
}

public class CachePolicyFactoryCustomization : ISpecimenBuilder
{
    public object Create(object request, ISpecimenContext context)
    {
        if (request is Type type && type == typeof(ICachePolicyFactory))
        {
            var cacheOptions = (CacheOptions)context.Resolve(typeof(CacheOptions));
            return new DefaultCachePolicyFactory(cacheOptions.Policies, cacheOptions.DefaultCachePolicy);
        }
        return new NoSpecimen();
    }
}

public class CacheOptionsCustomization : ISpecimenBuilder
{
    // Pin to a fresh default so AutoFixture doesn't fill DefaultCachePolicy with random data.
    public object Create(object request, ISpecimenContext context)
    {
        if (request is Type type && type == typeof(CacheOptions))
        {
            return new CacheOptions { AppShortName = "test" };
        }
        return new NoSpecimen();
    }
}

public class MultilayerCacheOptionsCustomization : ISpecimenBuilder
{
    public object Create(object request, ISpecimenContext context)
    {
        if (request is Type type)
        {
            if (type == typeof(InMemoryRedisCacheOptions))
            {
                return new InMemoryRedisCacheOptions
                {
                    LocalMaxExpiration = TimeSpan.FromMinutes(3),
                    LocalMaxExpirationDisconnected = TimeSpan.FromSeconds(30)
                };
            }
            if (type == typeof(InMemoryCacheOptions))
            {
                return new InMemoryCacheOptions
                {
                    LocalMaxExpiration = TimeSpan.FromMinutes(3),
                    LocalMaxExpirationDisconnected = TimeSpan.FromSeconds(30)
                };
            }
            if (typeof(IMultilayerCacheOptions).IsAssignableFrom(type))
            {
                return new InMemoryRedisCacheOptions
                {
                    LocalMaxExpiration = TimeSpan.FromMinutes(3),
                    LocalMaxExpirationDisconnected = TimeSpan.FromSeconds(30)
                };
            }
        }

        if (request is System.Reflection.PropertyInfo propertyInfo)
        {
            // [Obsolete]-marked aliases on IMultilayerCacheOptions (e.g. PrimaryMaxExpiration →
            // LocalMaxExpiration) forward assignments to the new property. If AutoFixture auto-fills
            // them with random values they would overwrite the sensible defaults set on the new
            // property below. Skip ONLY the IMultilayerCacheOptions hierarchy's obsolete forwarders
            // so we don't accidentally suppress unrelated [Obsolete] properties on other types.
            if (propertyInfo.DeclaringType is { } declaringType
                && typeof(IMultilayerCacheOptions).IsAssignableFrom(declaringType)
                && System.Reflection.CustomAttributeExtensions.GetCustomAttribute<ObsoleteAttribute>(propertyInfo) is not null)
            {
                return new OmitSpecimen();
            }
            if (propertyInfo.Name == nameof(IMultilayerCacheOptions.LocalMaxExpiration))
            {
                return TimeSpan.FromMinutes(3);
            }
            if (propertyInfo.Name == nameof(IMultilayerCacheOptions.LocalMaxExpirationDisconnected))
            {
                return TimeSpan.FromSeconds(30);
            }
            // Lock-related TimeSpan? fields: AutoFixture fills with sub-millisecond random values
            // which fail LockSettingsValidator.ValidateCross (must be >= DistributedLockPollInterval).
            // Leave them null so the validator treats them as unset.
            if (propertyInfo.Name is nameof(IMultilayerCacheOptions.DistributedLockTimeout)
                or nameof(IMultilayerCacheOptions.DistributedLockExpiry)
                or nameof(IMultilayerCacheOptions.LocalLockTimeout))
            {
                return new OmitSpecimen();
            }
        }

        return new NoSpecimen();
    }
}
