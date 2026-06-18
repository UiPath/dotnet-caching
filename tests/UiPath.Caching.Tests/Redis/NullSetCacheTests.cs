namespace UiPath.Caching.Tests.Redis;

public class NullSetCacheTests(ITestContextAccessor testContextAccessor)
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void Name_is_Null() => NullSetCache.Instance.Name.Should().Be("Null");

    [Fact]
    public async Task Add_single_returns_false()
    {
        var sut = new NullSetCache();
        var actual = await sut.AddAsync(_fixture.Create<string>(), _fixture.Create<TestDto>(), token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Add_many_returns_zero()
    {
        var sut = new NullSetCache();
        var actual = await sut.AddAsync(_fixture.Create<string>(), _fixture.CreateMany<TestDto>(), (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
        actual.Should().Be(0);
    }

    [Fact]
    public async Task Pop_single_returns_default()
    {
        var sut = new NullSetCache();
        var actual = await sut.PopAsync<TestDto>(_fixture.Create<string>(), token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Pop_count_returns_empty()
    {
        var sut = new NullSetCache();
        var actual = await sut.PopAsync<TestDto>(_fixture.Create<string>(), 5, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task Members_returns_empty()
    {
        var sut = new NullSetCache();
        var actual = await sut.MembersAsync<TestDto>(_fixture.Create<string>(), token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task ContainsItem_returns_false()
    {
        var sut = new NullSetCache();
        var actual = await sut.ContainsItemAsync(_fixture.Create<string>(), _fixture.Create<TestDto>(), testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Count_returns_zero()
    {
        var sut = new NullSetCache();
        var actual = await sut.CountAsync<TestDto>(_fixture.Create<string>(), testContextAccessor.Current.CancellationToken);
        actual.Should().Be(0);
    }

    [Fact]
    public async Task RemoveItem_returns_false()
    {
        var sut = new NullSetCache();
        var actual = await sut.RemoveItemAsync(_fixture.Create<string>(), _fixture.Create<TestDto>(), testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveItems_returns_zero()
    {
        var sut = new NullSetCache();
        var actual = await sut.RemoveItemsAsync(_fixture.Create<string>(), _fixture.CreateMany<TestDto>(), testContextAccessor.Current.CancellationToken);
        actual.Should().Be(0);
    }

    [Fact]
    public async Task Remove_returns_false()
    {
        var sut = new NullSetCache();
        var actual = await sut.RemoveAsync<TestDto>(_fixture.Create<string>(), testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Contains_returns_false()
    {
        var sut = new NullSetCache();
        var actual = await sut.ContainsAsync<TestDto>(_fixture.Create<string>(), testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Action act = () => new NullSetCache().Dispose();
        act.Should().NotThrow();
    }
}
