using System.Text;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// One test's key move kit with a hash client over the same plane, scripts and keys, and the codecs of the tests.
internal sealed class KeyMoveHashKit : IAsyncDisposable
{
    private KeyMoveHashKit(KeyMoveKit kit, KeyMoveHashClient<Item> client, HashPlainStub plain)
    {
        Kit = kit;
        Client = client;
        Plain = plain;
    }

    public KeyMoveKit Kit { get; }

    public KeyMoveHashClient<Item> Client { get; }

    public HashPlainStub Plain { get; }

    public IDatabase Db => Kit.Db;

    public static async Task<KeyMoveHashKit> CreateAsync(RedisFixture fixture, bool useScripts = true, bool noOrigin = false, bool cluster = false, KeyMoveOptions? options = null, bool nativeNulls = false, bool inPlaceNulls = false, int maxFields = 4096, IKeyMoveHashCodec<Item>? inPlace = null, IKeyMoveHashCodec<Item>? native = null, string? nativePrefix = null, TimeProvider? clock = null, Func<string, bool>? mappable = null, IHashPathClient<Item>? plain = null, Func<IDatabase, IDatabase>? interceptDb = null)
    {
        var settings = options ?? new KeyMoveOptions();
        var kit = await KeyMoveKit.CreateAsync(fixture, useScripts, noOrigin, cluster, clock, settings, nativePrefix: nativePrefix, interceptDb: interceptDb);
        var stub = new HashPlainStub();
        var parts = new KeyMoveHashClientParts<Item>(kit.Group, kit.Cid, noOrigin, kit.L, kit.N, inPlace ?? new ItemHashCodec(inPlaceNulls, false, maxFields, settings.MaxValueBytes), native ?? new ItemHashCodec(nativeNulls, true, maxFields, settings.MaxValueBytes), maxFields, plain ?? stub, kit.Plane, kit.Clock, () => TimeSpan.FromSeconds(30))
        {
            Options = settings,
            Schedule = kit.Schedule,
            RunRetriesInBackground = false,
            Mappable = mappable,
        };
        return new KeyMoveHashKit(kit, new KeyMoveHashClient<Item>(parts), stub);
    }

    public static Dictionary<string, Item?> Items(params (string Field, string? Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => f.Value is null ? null : new Item(f.Value));

    public string L(string key) => Kit.L(key);

    public string N(string key) => Kit.N(key);

    // A hash as the codecs store it: each value is the bytes of its text, and an empty text is a null.
    public async Task SetAsync(string redisKey, IDictionary<string, string> fields, TimeSpan? life = null)
    {
        await Db.HashSetAsync(redisKey, [.. fields.Select(f => new HashEntry(f.Key, Encoding.UTF8.GetBytes(f.Value)))]);
        if (life is { } span)
        {
            await Db.KeyExpireAsync(redisKey, span);
        }
    }

    // The user fields of a stored hash, as text.
    public async Task<Dictionary<string, string>> FieldsAsync(string redisKey) =>
        (await Db.HashGetAllAsync(redisKey)).Where(e => !(e.Name.ToString()).StartsWith('_')).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

    public async Task<Dictionary<string, string>> AllFieldsAsync(string redisKey) =>
        (await Db.HashGetAllAsync(redisKey)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Kit.DisposeAsync();
    }
}
