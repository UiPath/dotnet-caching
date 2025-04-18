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

        fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList()
            .ForEach(b => fixture.Behaviors.Remove(b));
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());
        fixture.Behaviors.Add(new GenerationDepthBehavior(10));

        return fixture;
    }
}
