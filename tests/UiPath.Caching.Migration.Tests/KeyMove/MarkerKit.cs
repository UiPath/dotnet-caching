using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The supersession marker of a key as the tests set it and read it back: a field of the control record of N's slot.
internal static class MarkerKit
{
    public static RedisKey Control(this KeyMoveKit kit, string key) => kit.Names.ControlFor(kit.Group, kit.N(key));

    public static string Field(this KeyMoveKit kit, string key) => kit.Names.MarkerField(kit.L(key));

    public static async Task<string?> MarkerAsync(this KeyMoveKit kit, string key) => await kit.Db.HashGetAsync(kit.Control(key), kit.Field(key));

    // A marker of the identity in force, with L holding an older copy, as a writer that stopped after its first step leaves them.
    public static async Task LeaveSupersededAsync(this KeyMoveKit kit, string key, string older, string nonce = "n0")
    {
        await kit.Db.StringSetAsync(kit.L(key), older);
        await kit.Db.HashSetAsync(kit.Control(key), kit.Field(key), $"{kit.Id}|{nonce}");
    }
}
