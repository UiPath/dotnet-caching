namespace UiPath.Caching.Negotiation;

internal sealed record EventFormat(string Name, int Id, Func<IServiceProvider, IEventFormatterProxy<ICacheEvent>> Factory, bool Write);
