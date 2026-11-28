using System.Collections.Concurrent;
using System.Reflection;
using UiPath.Caching.Tests;
using Xunit.v3;

[assembly: KeepRunningTestsReachable]

namespace UiPath.Caching.Tests;

/// <summary>Holds each running test's token, so a test awaiting only Redis or a fake clock stays reachable through its token's registrations.</summary>
/// <remarks>Nothing else roots a test parked on such a wait: a full GC would collect it, its connections and the signal it waits for, and the test would hang.</remarks>
internal sealed class KeepRunningTestsReachableAttribute : BeforeAfterTestAttribute
{
    private static readonly ConcurrentDictionary<string, CancellationToken> Running = new();

    public override void Before(MethodInfo methodUnderTest, IXunitTest test) => Running[test.UniqueID] = TestContext.Current.CancellationToken;

    public override void After(MethodInfo methodUnderTest, IXunitTest test) => Running.TryRemove(test.UniqueID, out _);
}
