using System.Buffers.Binary;
using System.Text;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

internal static class RedisConsumerClock
{
    public static async Task AdvanceAsync(IDatabase database, RedisKey stream, TimeSpan elapsed, string? group = null)
    {
        var full = Pairs(await database.ExecuteAsync("XINFO", "STREAM", stream, "FULL", "COUNT", 0));
        var consumers = ((RedisResult[])full["groups"]!)
            .Select(Pairs)
            .Where(item => group is null || (string?)item["name"] == group)
            .SelectMany(item => (RedisResult[])item["consumers"]!)
            .Select(Pairs)
            .ToArray();
        if (consumers.Length == 0)
        {
            return;
        }
        var payload = (byte[])(await database.ExecuteAsync("DUMP", stream))!;
        var ttl = await database.KeyTimeToLiveAsync(stream);
        Rewrite(payload, consumers, checked((long)elapsed.TotalMilliseconds));
        var milliseconds = ttl is { } remaining ? Math.Max(1, (long)Math.Ceiling(remaining.TotalMilliseconds)) : 0;
        await database.ExecuteAsync("RESTORE", stream, milliseconds, payload, "REPLACE");
    }

    private static Dictionary<string, RedisResult> Pairs(RedisResult value) =>
        ((RedisResult[])value!).Chunk(2).ToDictionary(pair => (string)pair[0]!, pair => pair[1], StringComparer.Ordinal);

    private static void Rewrite(byte[] payload, Dictionary<string, RedisResult>[] consumers, long milliseconds)
    {
        var checksum = payload.Length - sizeof(ulong);
        Crc(payload.AsSpan(0, checksum)).Should().Be(BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(checksum)));
        foreach (var consumer in consumers)
        {
            var name = Encoding.UTF8.GetBytes((string)consumer["name"]!);
            var seen = (long)consumer["seen-time"];
            var marker = new byte[name.Length + sizeof(long)];
            name.CopyTo(marker, 0);
            BinaryPrimitives.WriteInt64LittleEndian(marker.AsSpan(name.Length), seen);
            var found = payload.AsSpan(0, checksum).IndexOf(marker);
            found.Should().BeGreaterThanOrEqualTo(0);
            payload.AsSpan(found + marker.Length, checksum - found - marker.Length).IndexOf(marker).Should().Be(-1);
            // Stream snapshots store the consumer's seen and active timestamps immediately after its name.
            var stamp = found + name.Length;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(stamp), seen - milliseconds);
            if (consumer.TryGetValue("active-time", out var activeValue))
            {
                var active = (long)activeValue;
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(stamp + sizeof(long))).Should().Be(active);
                if (active >= 0)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(stamp + sizeof(long)), active - milliseconds);
                }
            }
        }
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(checksum), Crc(payload.AsSpan(0, checksum)));
    }

    private static ulong Crc(ReadOnlySpan<byte> bytes)
    {
        ulong crc = 0;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x95ac9329ac4bc9b5UL);
            }
        }
        return crc;
    }
}
