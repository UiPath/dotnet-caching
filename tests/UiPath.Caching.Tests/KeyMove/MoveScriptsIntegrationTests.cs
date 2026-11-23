using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public partial class MoveScriptsIntegrationTests(RedisContainerFixture fixture)
{
    private Task<MoveTestEnvironment> NewEnvironmentAsync(bool useScripts, bool openControl = true) =>
        MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl);
}
