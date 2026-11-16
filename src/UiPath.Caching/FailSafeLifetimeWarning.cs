using UiPath.Caching.Config;

namespace UiPath.Caching;

/// <summary>Warns once per policy that enables fail-safe on a cache with no size limit, where nothing caps how many values are kept.</summary>
internal static partial class FailSafeLifetimeWarning
{
    internal const string DefaultPolicyName = "(default)";

    /// <summary>Logs one warning for the default policy and each named policy that enables fail-safe, unless <paramref name="sizeLimit"/> caps the kept values.</summary>
    public static void Log(ILogger logger, string provider, ICachePolicyFactory policies, long? sizeLimit)
    {
        // As the kept-value store reads it: zero or less is no limit.
        if (sizeLimit > 0)
        {
            return;
        }

        Check(logger, provider, DefaultPolicyName, policies.Default);
        foreach (var name in policies.Keys)
        {
            Check(logger, provider, name, policies.Resolve(name));
        }
    }

    private static void Check(ILogger logger, string provider, string name, CachePolicy? policy)
    {
        if (policy?.FailSafeEnabled == true)
        {
            LogUnlimited(logger, name, provider, policy.FailSafeMaxStaleAge ?? StaleValues.DefaultMaxStaleAge);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Policy {Policy} enables fail-safe while {Provider} SizeLimit is unset: every key written under it keeps its value in memory until FailSafeMaxStaleAge ({MaxStaleAge}) after its local copy expires, whatever LocalMaxExpiration or LocalExpiration is, and nothing caps how many are kept. Set SizeLimit, which then bounds the local entries and the kept values together, or a shorter FailSafeMaxStaleAge.")]
    private static partial void LogUnlimited(ILogger logger, string policy, string provider, TimeSpan maxStaleAge);
}
