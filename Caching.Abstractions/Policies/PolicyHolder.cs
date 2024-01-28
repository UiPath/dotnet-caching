namespace UiPath.Platform.Caching.Policies;

public sealed class PolicyHolder : IPolicyHolder
{
    public static readonly PolicyHolder NoOp = new(new NoOpExecutor());

    public PolicyHolder(IPolicyExecutor read, IPolicyExecutor? write = null)
    {
        Read = read;
        Write = write ?? read;
    }

    public IPolicyExecutor Read { get; }

    public IPolicyExecutor Write { get; }
}
