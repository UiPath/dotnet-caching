using Polly;

namespace UiPath.Platform.Caching.Redis;

public class PolicyHolder : IPolicyHolder
{
    public PolicyHolder(IAsyncPolicy[] policies) =>
        AsyncPolicy = Build(policies);

    public IAsyncPolicy AsyncPolicy { get; }

    private static IAsyncPolicy Build(IAsyncPolicy[] policies)
    {
        if (policies.Length == 0)
        {
            return Policy.NoOpAsync();
        }

        if (policies.Length == 1)
        {
            return policies[0];
        }

        return Policy.WrapAsync(policies);
    }
}
