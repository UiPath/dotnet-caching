namespace UiPath.Caching.Broadcast;

/// <summary>An observer of every event and loss on the topic, told before the keyed observers of the event's key.</summary>
internal interface IFirstObserver<in T> : IObserver<T> where T : IEvent
{
}
