using Microsoft.Extensions.Configuration;

const string RedisConnectionStringKey = "Caching__Connections__Redis__ConnectionString";
const string RedisConnectionStringExtraParamsKey = "Caching__Connections__Redis__ConnectionStringExtraParams";
const string ShardKeyEnabledKey = "Caching__ShardKeyEnabled";
const string ShardedPubSubKey = "Caching__Broadcast__RedisStreams__NotifyShardedPubSub";
const string UseOpenTelemetryKey = "SampleAspNetCore__UseOpenTelemetry";

var builder = DistributedApplication.CreateBuilder(args);

var useShardedRedis = builder.Configuration.GetValue("SampleAspNetCore:UseShardedRedis", false);
var useShardedPubSub = builder.Configuration.GetValue("SampleAspNetCore:UseShardedPubSub", false);
var useOpenTelemetry = builder.Configuration.GetValue("SampleAspNetCore:UseOpenTelemetry", true);
var useRedisInsight = builder.Configuration.GetValue("SampleAspNetCore:UseRedisInsight", true);
var useSingleMachine = builder.Configuration.GetValue("SampleAspNetCore:UseSingleMachine", false);
var singleRedisExtraParams = builder.Configuration["SampleAspNetCore:SingleRedisConnectionStringExtraParams"]
    ?? "connectRetry=2,keepAlive=30,syncTimeout=2500,connectTimeout=2500";
var shardedRedisExtraParams = builder.Configuration["SampleAspNetCore:ShardedRedisConnectionStringExtraParams"]
    ?? "allowAdmin=true,abortConnect=false,connectRetry=2,keepAlive=30,name=test,syncTimeout=2500,connectTimeout=2500";

if (useShardedRedis)
{
    var redis = AddShardedRedis(builder, useRedisInsight);

    foreach (var sampleMachine in AddSampleMachines(builder, redis.ConnectionString, shardedRedisExtraParams, shardKeyEnabled: true, useShardedPubSub: useShardedPubSub, useOpenTelemetry: useOpenTelemetry, useSingleMachine: useSingleMachine))
    {
        sampleMachine
            .WaitFor(redis.Master1)
            .WaitFor(redis.Slave1Master1)
            .WaitFor(redis.Slave2Master1)
            .WaitFor(redis.Master2)
            .WaitFor(redis.Slave1Master2)
            .WaitFor(redis.Slave2Master2)
            .WaitForCompletion(redis.ClusterInit);
    }
}
else
{
    var cache = builder.AddRedis("cache", port: 6379)
        .WithDataVolume()
        .WithPersistence(TimeSpan.FromSeconds(20), keysChangedThreshold: 1);

    if (useRedisInsight)
    {
        cache.WithRedisInsight(insight => insight.WithHostPort(8001));
    }

    foreach (var sampleMachine in AddSampleMachines(builder, cache.Resource.ConnectionStringExpression, singleRedisExtraParams, shardKeyEnabled: false, useShardedPubSub: false, useOpenTelemetry: useOpenTelemetry, useSingleMachine: useSingleMachine))
    {
        sampleMachine.WaitFor(cache);
    }
}

builder.Build().Run();

static IEnumerable<IResourceBuilder<ProjectResource>> AddSampleMachines(
    IDistributedApplicationBuilder builder,
    ReferenceExpression redisConnectionString,
    string redisConnectionStringExtraParams,
    bool shardKeyEnabled,
    bool useShardedPubSub,
    bool useOpenTelemetry,
    bool useSingleMachine)
{
    yield return AddSampleMachine(builder, "sample-aspnetcore-machine1", "Machine1", redisConnectionString, redisConnectionStringExtraParams, shardKeyEnabled, useShardedPubSub, useOpenTelemetry);

    if (useSingleMachine)
    {
        yield break;
    }

    yield return AddSampleMachine(builder, "sample-aspnetcore-machine2", "Machine2", redisConnectionString, redisConnectionStringExtraParams, shardKeyEnabled, useShardedPubSub, useOpenTelemetry);
}

static IResourceBuilder<ProjectResource> AddSampleMachine(
    IDistributedApplicationBuilder builder,
    string resourceName,
    string launchProfileName,
    ReferenceExpression redisConnectionString,
    string redisConnectionStringExtraParams,
    bool shardKeyEnabled,
    bool useShardedPubSub,
    bool useOpenTelemetry)
{
    return builder.AddProject<Projects.Sample_AspNetCore>(resourceName, launchProfileName)
        .WithEnvironment(RedisConnectionStringKey, redisConnectionString)
        .WithEnvironment(RedisConnectionStringExtraParamsKey, redisConnectionStringExtraParams)
        .WithEnvironment(ShardKeyEnabledKey, shardKeyEnabled ? "true" : "false")
        .WithEnvironment(ShardedPubSubKey, useShardedPubSub ? "true" : "false")
        .WithEnvironment(UseOpenTelemetryKey, useOpenTelemetry ? "true" : "false")
        .WithHttpHealthCheck("/healthz");
}

static ShardedRedisResources AddShardedRedis(IDistributedApplicationBuilder builder, bool useRedisInsight)
{
    var master1 = AddRedisStackNode(builder, "redis-master1", 6379, "--port 6379 --protected-mode no", redisInsightPort: useRedisInsight ? 8001 : null);
    var slave1Master1 = AddRedisStackNode(builder, "redis-slave1-master1", 6380, "--port 6380 --slaveof redis-master1 6379")
        .WaitFor(master1);
    var slave2Master1 = AddRedisStackNode(builder, "redis-slave2-master1", 6381, "--slaveof redis-master1 6379 --port 6381")
        .WaitFor(master1);

    var master2 = AddRedisStackNode(builder, "redis-master2", 6382, "--port 6382 --protected-mode no", redisInsightPort: useRedisInsight ? 8002 : null);
    var slave1Master2 = AddRedisStackNode(builder, "redis-slave1-master2", 6383, "--port 6383 --slaveof redis-master2 6382")
        .WaitFor(master2);
    var slave2Master2 = AddRedisStackNode(builder, "redis-slave2-master2", 6384, "--port 6384 --slaveof redis-master2 6382")
        .WaitFor(master2);

    // sleep 10 hedges the race between Aspire's WaitFor (container "Running") and the Redis process
    // inside actually listening — without per-container health checks, "Running" doesn't imply ready.
    var clusterInit = builder.AddContainer("redis-trib", "redis/redis-stack", "latest")
        .WithEntrypoint("bash")
        .WithArgs("-c", "sleep 10 && redis-trib.rb create --replicas 1 redis-master1:6379 redis-master2:6382")
        .WaitFor(master1)
        .WaitFor(slave1Master1)
        .WaitFor(slave2Master1)
        .WaitFor(master2)
        .WaitFor(slave1Master2)
        .WaitFor(slave2Master2);

    var connectionString = ReferenceExpression.Create(
        $"{Endpoint(master1)},{Endpoint(slave1Master1)},{Endpoint(slave2Master1)},{Endpoint(master2)},{Endpoint(slave1Master2)},{Endpoint(slave2Master2)}");

    return new(
        connectionString,
        master1,
        slave1Master1,
        slave2Master1,
        master2,
        slave1Master2,
        slave2Master2,
        clusterInit);
}

static IResourceBuilder<ContainerResource> AddRedisStackNode(
    IDistributedApplicationBuilder builder,
    string name,
    int port,
    string redisArgs,
    int? redisInsightPort = null)
{
    var image = redisInsightPort.HasValue ? "redis/redis-stack" : "redis/redis-stack-server";
    var resource = builder.AddContainer(name, image, "latest")
        .WithEnvironment("REDIS_ARGS", redisArgs)
        .WithEndpoint(targetPort: port, port: port, scheme: "tcp", name: "tcp")
        .WithVolume($"{name}-data", "/data");

    return redisInsightPort.HasValue
        ? resource.WithHttpEndpoint(targetPort: 8001, port: redisInsightPort, name: "redisinsight")
        : resource;
}

static EndpointReferenceExpression Endpoint(IResourceBuilder<ContainerResource> resource) =>
    resource.GetEndpoint("tcp").Property(EndpointProperty.HostAndPort);

internal sealed record ShardedRedisResources(
    ReferenceExpression ConnectionString,
    IResourceBuilder<ContainerResource> Master1,
    IResourceBuilder<ContainerResource> Slave1Master1,
    IResourceBuilder<ContainerResource> Slave2Master1,
    IResourceBuilder<ContainerResource> Master2,
    IResourceBuilder<ContainerResource> Slave1Master2,
    IResourceBuilder<ContainerResource> Slave2Master2,
    IResourceBuilder<ContainerResource> ClusterInit);
