namespace UiPath.Platform.Caching.Tests.Broadcast;

public class ChangeTokenFactoryTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ChangeTokenFactory? _sut = null;
    private ChangeTokenFactory Sut => _sut ??= _fixture.Create<ChangeTokenFactory>();

    [Fact]
    public void NotNullable_change_token()
    {
        Sut.Create(_fixture.Create<string>(), _fixture.Create<ITopic<ICacheEvent>>(), _fixture.Create<string>(), _fixture.Create<Type>()).Should().NotBeNull();
    }

    [Fact]
    public void NotNullable_change_InMemory()
    {
        Sut.Create(_fixture.Create<string>(), _fixture.Create<ITopic<ICacheEvent>>(), "InMemory", _fixture.Create<Type>()).Should().NotBeNull();
    }
}
