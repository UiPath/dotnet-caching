namespace UiPath.Platform.Caching.Policies;

public interface IPolicyHolder
{
    IPolicyExecutor Read { get; }

    IPolicyExecutor Write { get; }
}
