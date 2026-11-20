using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Migration;

/// <summary>The hosted services of the router's named stack, which the router starts and stops itself.</summary>
internal sealed record StackServices(IReadOnlyList<IHostedService> HostedServices);
