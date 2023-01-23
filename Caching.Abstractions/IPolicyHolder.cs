namespace UiPath.Platform.Caching;

public interface IPolicyHolder
{
    IPolicyExecutor Read { get; }

    IPolicyExecutor Write { get; }
}
