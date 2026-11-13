using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Benchmarks;

/// <summary>The MessagePack serializer with compression off against its default threshold, at three payload sizes of compressible rows.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MessagePackCompressionBenchmark
{
    private readonly ArrayBufferWriter<byte> _target = new();
    private ServiceProvider _plainServices = default!;
    private ServiceProvider _compressedServices = default!;
    private ICacheValueSerializer _plain = default!;
    private ICacheValueSerializer _compressed = default!;
    private Payload _value = default!;
    private byte[] _plainBytes = default!;
    private byte[] _compressedBytes = default!;
    private int _compressedId;

    [Params(512, 1024, 16 * 1024, 128 * 1024)]
    public int Bytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _plainServices = Build(o => o.CompressionThresholdBytes = int.MaxValue);
        _compressedServices = Build(_ => { });
        _plain = ValueSerializerRegistration.BuildRegistry(_plainServices).Entries.Single().Serializer;
        _compressed = ValueSerializerRegistration.BuildRegistry(_compressedServices).Entries.Single().Serializer;
        _value = Payload.OfSize(Bytes);
        _plainBytes = Write(_plain, out _);
        _compressedBytes = Write(_compressed, out _compressedId);
        Console.WriteLine($"size {Bytes}: plain {_plainBytes.Length}, compressed {_compressedBytes.Length} (id 0x{_compressedId:X2})");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plainServices.Dispose();
        _compressedServices.Dispose();
    }

    [BenchmarkCategory("Write"), Benchmark(Baseline = true)]
    public int Write_Plain()
    {
        _target.ResetWrittenCount();
        return _plain.Serialize(_value, _target);
    }

    [BenchmarkCategory("Write"), Benchmark]
    public int Write_Compressed()
    {
        _target.ResetWrittenCount();
        return _compressed.Serialize(_value, _target);
    }

    [BenchmarkCategory("Read"), Benchmark(Baseline = true)]
    public Payload? Read_Plain() => _plain.Deserialize<Payload>(CacheValueFormats.MessagePack, new ReadOnlySequence<byte>(_plainBytes));

    [BenchmarkCategory("Read"), Benchmark]
    public Payload? Read_Compressed() => _compressed.Deserialize<Payload>(_compressedId, new ReadOnlySequence<byte>(_compressedBytes));

    private static ServiceProvider Build(Action<MessagePackValueSerializerOptions> configure) =>
        new ServiceCollection()
            .AddCaching(b => b.AddMessagePackSerializer(configure: configure), o => o.AppShortName = "bench")
            .BuildServiceProvider();

    private byte[] Write(ICacheValueSerializer serializer, out int id)
    {
        var target = new ArrayBufferWriter<byte>();
        id = serializer.Serialize(_value, target);
        return target.WrittenMemory.ToArray();
    }

    public sealed class Payload
    {
        public List<Row> Rows { get; set; } = [];

        public static Payload OfSize(int bytes)
        {
            var payload = new Payload();
            var random = new Random(42);
            string[] statuses = ["active", "pending", "suspended", "closed"];
            for (var size = 0; size < bytes; size += 60)
            {
                payload.Rows.Add(new Row
                {
                    Id = payload.Rows.Count,
                    Name = $"customer-{random.Next(100_000)}",
                    Status = statuses[random.Next(statuses.Length)],
                    Amount = Math.Round(random.NextDouble() * 1000, 2),
                    Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(random.Next(500_000)),
                });
            }

            return payload;
        }
    }

    public sealed class Row
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        public string? Status { get; set; }

        public double Amount { get; set; }

        public DateTime Created { get; set; }
    }
}
