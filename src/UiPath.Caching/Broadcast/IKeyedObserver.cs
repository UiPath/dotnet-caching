namespace UiPath.Caching.Broadcast;

internal interface IKeyedObserver<in T> : IObserver<T> where T : IEvent
{
    string Key { get; }
}
