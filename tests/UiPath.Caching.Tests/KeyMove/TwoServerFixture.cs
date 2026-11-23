namespace UiPath.Caching.Tests.KeyMove;

/// <summary>
/// Two independent servers for the tests of an instance move: the old one (Src) and the new one (Dst).
/// Set RUN_REDIS_INTEGRATION_TESTS=1, TWO_SERVER_SRC and TWO_SERVER_DST (for example <c>127.0.0.1:16492</c> and <c>127.0.0.1:16491</c>); without them the tests skip.
/// </summary>
/// <remarks>
/// The servers need no replica: a test that wants a replica sets MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA, and the kit then takes that master as the Src.
/// Every test works under a library prefix and group of its own and deletes its keys, so tests may share the servers.
/// </remarks>
public sealed class TwoServerFixture
{
    public static string? Src => Environment.GetEnvironmentVariable("TWO_SERVER_SRC");

    public static string? Dst => Environment.GetEnvironmentVariable("TWO_SERVER_DST");

    /// <summary>A master with a replica, from MOVE_SIDES_MASTER; null when not set.</summary>
    public static string? SrcWithReplica => Environment.GetEnvironmentVariable("MOVE_SIDES_MASTER");
    public bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("RUN_REDIS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal) && Src is not null && Dst is not null;
}
