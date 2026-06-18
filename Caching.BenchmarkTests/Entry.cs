using Microsoft.Extensions.Hosting;
using UiPath.Platform.Caching;

namespace Caching.BenchmarkTests;

public sealed record Entry<T>(IHost Host, ICache<T> Cache, Task Start);

