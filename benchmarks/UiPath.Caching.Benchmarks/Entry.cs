using Microsoft.Extensions.Hosting;
using UiPath.Caching;

namespace UiPath.Caching.Benchmarks;

public sealed record Entry<T>(IHost Host, ICache<T> Cache, Task Start);

