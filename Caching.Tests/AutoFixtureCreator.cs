using AutoFixture.Kernel;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests;

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
                    PrimaryMaxExpiration = TimeSpan.FromMinutes(3),
                    PrimaryMaxExpirationDisconnected = TimeSpan.FromSeconds(30)
                };
            }
            if (type == typeof(InMemoryCacheOptions))
            {
                return new InMemoryCacheOptions
                {
                    PrimaryMaxExpiration = TimeSpan.FromMinutes(3),
                    PrimaryMaxExpirationDisconnected = TimeSpan.FromSeconds(30)
                };
            }
            if (typeof(IMultilayerCacheOptions).IsAssignableFrom(type))
            {
                return new InMemoryRedisCacheOptions
                {
                    PrimaryMaxExpiration = TimeSpan.FromMinutes(3),
                    PrimaryMaxExpirationDisconnected = TimeSpan.FromSeconds(30)
                };
            }
        }

        if (request is System.Reflection.PropertyInfo propertyInfo)
        {
            if (propertyInfo.Name == nameof(IMultilayerCacheOptions.PrimaryMaxExpiration))
            {
                return TimeSpan.FromMinutes(3);
            }
            if (propertyInfo.Name == nameof(IMultilayerCacheOptions.PrimaryMaxExpirationDisconnected))
            {
                return TimeSpan.FromSeconds(30);
            }
        }

        return new NoSpecimen();
    }
}
