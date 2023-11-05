## Caching docs

Multilayer caching library offering multiple InMemory & Redis configurations with support for cache synchronization between layers using [Redis Streams](https://redis.io/docs/data-types/streams/) (at least one sync event guaranteed) or [Redis PubSub](https://redis.io/docs/interact/pubsub/)

Main capabilities:

* 2 types of caching for simple objects [ICache](/Caching.Abstractions/ICache.cs) and for dictionaries [IHashCache](/Caching.Abstractions/IHashCache.cs), useful when you have a list of objects that needs to be treated as one (expires at the same time) but you need to retrive only a subset of them.
* Different key expiration policies for each cache layer. Ex: Keep the object in memory for max 20s and in redis for max 10m
* Extending a key lifetime for all cache layers without rehydrate the payload.
* back-pressure and retry policies using [Polly](https://github.com/App-vNext/Polly) library
* build-in telemetry
* Redis profiling (can be enabled via feature flags for specific Account/Tenant)
* Redis hanging connections handeling and azure planned maitenance detection
* Synchronization events using Redis Streams or PubSub. See [ITopic](/Caching.Abstractions/Broadcast/ITopic.cs) implementations.
* Multiple extensions points in order to fit applications use cases

## Documentation

* [Basic Usage](basics.md) \- getting started and basic usage
* [Running sample app](sample-app.md) \- running the sample app
* [Advanced usage](advanced-usage.md) - extending and advanced usage