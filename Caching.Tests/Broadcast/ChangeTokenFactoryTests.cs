namespace UiPath.Platform.Caching.Tests.Broadcast;

public class ChangeTokenFactoryTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private ChangeTokenFactory? _sut = null;
    private ChangeTokenFactory Sut => _sut ??= _fixture.Create<ChangeTokenFactory>();

    [Fact]
    public void NotNullable_change_token()
    {
        Sut.Create(_fixture.Create<string>(), _fixture.Create<string>()).Should().NotBeNull();
    }
}
