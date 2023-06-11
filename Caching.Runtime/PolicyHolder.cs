using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching;

public class PolicyHolder : IPolicyHolder
{
    public PolicyHolder(IPolicyExecutor read, IPolicyExecutor? write = null)
    {
        Read = read;
        Write = write ?? read;
    }

    public IPolicyExecutor Read { get; }

    public IPolicyExecutor Write { get; }
}
