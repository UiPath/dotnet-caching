global using System.Diagnostics.CodeAnalysis;
global using Microsoft.Extensions.Caching.Memory;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.DependencyInjection.Extensions;
global using Microsoft.Extensions.Internal;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Logging.Abstractions;
global using Microsoft.Extensions.Options;
global using StackExchange.Redis;
global using UiPath.Caching.Broadcast;
global using UiPath.Caching.Redis;
global using static UiPath.Caching.CacheValueHelpers;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UiPath.Caching.Tests")]
// Castle DynamicProxy (used by NSubstitute via AutoFixture in Caching.Tests) needs access to the
// internal concrete cache classes to generate dynamic proxies for closed generics like
// ILogger<RedisCache>. Without this, mocking those proxies fails at test fixture time.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]
