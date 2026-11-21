using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public partial class MoveScriptsIntegrationTests(RedisContainerFixture fixture)
{
    private Task<MoveTestEnvironment> NewEnvironmentAsync(bool useScripts, bool openControl = true) =>
        MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl);
}
