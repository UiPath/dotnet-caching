namespace UiPath.Platform.Caching.Broadcast;

internal interface IKeyedObserver<in T> : IObserver<T> where T : IEvent
{
    string Key { get; }
}
