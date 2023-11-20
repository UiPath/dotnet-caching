## Caching

Multilayer caching library offering multiple InMemory & Redis configurations with support for cache synchronization between layers using [Redis Streams](https://redis.io/docs/data-types/streams/) (at least one sync event guaranteed) or [Redis PubSub](https://redis.io/docs/interact/pubsub/)

Main capabilities:
* 2 types of caching for simple objects [ICache](/Caching.Abstractions/ICache.cs) and for dictionaries [IHashCache](/Caching.Abstractions/IHashCache.cs), useful when you have a list of objects that needs to be treated as one (expires at the same time) but you need to retrive only a subset of them.
* Different key expiration policies for each cache layer. Ex: Keep the object in memory for max 20s and in redis for max 10m
* Extending a key lifetime for all cache layers without rehydrate the payload.
* back-pressure and retry policies using [Polly](https://github.com/App-vNext/Polly) library
* build-in telemetry
* In case of a Redis cluster, the writes are always done in master and the reads in slaves
* Redis profiling (can be enabled via feature flags for specific Account/Tenant)
* Redis hanging connections handeling and azure planned maitenance detection
* Audit large redis key values
* Support redis cluster [data sharding](https://redis.io/docs/management/scaling/)
* Cache events process back-pressure using [Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
* Synchronization events using Redis Streams or PubSub. See [ITopic](/Caching.Abstractions/Broadcast/ITopic.cs) implementations.
* Multiple extensions points in order to fit applications use cases


The general library guideline are defined here:
https://github.com/UiPath/ServiceCommon/blob/master/README.md

Docs can be found here:
https://github.com/UiPath/ServiceCommon/blob/master/Caching/docs/index.md

A complete change log for the library:
https://github.com/UiPath/ServiceCommon/blob/master/Caching/CHANGELOG.md

Official feed for supported versions:
https://uipath.visualstudio.com/Service%20Common/\_artifacts/feed/nuget-packages

Beta feed for all pre-release versions:
https://uipath.visualstudio.com/Service%20Common/\_artifacts/feed/ServiceCommon
