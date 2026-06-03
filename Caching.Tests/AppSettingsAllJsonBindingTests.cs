using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Broadcast.Redis;
using UiPath.Platform.Caching.Polly;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Tests;

public class AppSettingsAllJsonBindingTests
{
    private static readonly string AppSettingsAllPath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.all.json");

    private static IConfiguration BuildConfig()
    {
        var raw = File.ReadAllText(AppSettingsAllPath);
        var stripped = StripJsoncComments(raw);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(stripped));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    // Strip `// ...` line and trailing comments from JSONC, respecting string literals so we
    // don't eat `//` that's part of a URL/value. Block comments `/* ... */` are not used by
    // appsettings.all.json, so they aren't handled.
    private static string StripJsoncComments(string input)
    {
        var sb = new StringBuilder(input.Length);
        var inString = false;
        var escape = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (inString)
            {
                sb.Append(c);
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }
            if (c == '/' && i + 1 < input.Length && input[i + 1] == '/')
            {
                while (i < input.Length && input[i] != '\n')
                {
                    i++;
                }
                if (i < input.Length)
                {
                    sb.Append(input[i]);
                }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [Fact]
    public void CacheOptions_binds()
    {
        var cfg = BuildConfig().GetSection("Caching");
        var opts = cfg.Get<CacheOptions>();
        opts.Should().NotBeNull();
        opts!.AppShortName.Should().Be("app");
        opts.LocalLockPoolSize.Should().Be(100);
    }

    [Fact]
    public void RedisConnectionOptions_binds()
    {
        var cfg = BuildConfig().GetSection("Caching:Connections:Redis");
        var opts = cfg.Get<RedisConnectionOptions>();
        opts.Should().NotBeNull();
        opts!.ConnectionString.Should().NotBeEmpty();
        opts.AbortOnConnectFail.Should().BeFalse();
    }

    [Fact]
    public void RedisStreamsTopicOptions_binds()
    {
        var cfg = BuildConfig().GetSection("Caching:Broadcast:RedisStreams");
        var opts = cfg.Get<RedisStreamsTopicOptions>();
        opts.Should().NotBeNull();
        opts!.Enabled.Should().BeTrue();
        opts.MaxLength.Should().Be(32768);
    }

    [Fact]
    public void RedisPubSubTopicOptions_binds()
    {
        var cfg = BuildConfig().GetSection("Caching:Broadcast:RedisPubSub");
        var opts = cfg.Get<RedisPubSubTopicOptions>();
        opts.Should().NotBeNull();
    }

    [Fact]
    public void InMemoryRedisCacheOptions_binds()
    {
        var cfg = BuildConfig().GetSection("Caching:InMemoryRedis");
        var opts = cfg.Get<InMemoryRedisCacheOptions>();
        opts.Should().NotBeNull();
        opts!.LocalLockEnabled.Should().BeTrue();
        opts.DistributedLockEnabled.Should().BeNull();
    }

    [Fact]
    public void InMemoryCacheOptions_binds()
    {
        var cfg = BuildConfig().GetSection("Caching:InMemory");
        var opts = cfg.Get<InMemoryCacheOptions>();
        opts.Should().NotBeNull();
        opts!.DistributedLockEnabled.Should().BeNull();
    }

    [Fact]
    public void RedisCacheOptions_binds()
    {
        var cfg = BuildConfig().GetSection("Caching:Redis");
        var opts = cfg.Get<RedisCacheOptions>();
        opts.Should().NotBeNull();
        opts!.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ResiliencePoliciesOptions_binds()
    {
        var cfg = BuildConfig().GetSection("Caching:ResiliencePolicies");
        var opts = cfg.Get<ResiliencePoliciesOptions>();
        opts.Should().NotBeNull();
        opts!.ExceptionsAllowedBeforeBreaking.Should().Be(500);
    }

    [Fact]
    public void Policies_dictionary_binds_each_CachePolicy()
    {
        var cfg = BuildConfig().GetSection("Caching:Policies");
        var opts = cfg.Get<Dictionary<string, CachePolicy>>();
        opts.Should().NotBeNull().And.ContainKey("MyApp.Models.Order");
        opts!["MyApp.Models.Order"].RehydrateEnabled.Should().BeTrue();
        opts["MyApp.Models.Order"].Lock!.LocalLockEnabled.Should().BeTrue();
    }

    // Drift detection: the binder ignores unknown JSON keys and silently defaults missing
    // ones, so a parse-only test cannot tell when JSON and options classes drift apart.
    // This theory enumerates every binding-visible property on each options type and asserts
    // that the JSON section's child keys match it exactly (modulo well-known sub-section
    // names that don't map to a property on the parent type, like Caching:Connections).
    [Theory]
    [MemberData(nameof(DriftCases))]
    public void Json_keys_match_bindable_properties(string sectionPath, Type optionsType, string[] subSectionAllowlist)
    {
        var section = BuildConfig().GetSection(sectionPath);
        var jsonKeys = section.GetChildren()
            .Select(c => c.Key)
            .Except(subSectionAllowlist, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var classKeys = BindableProperties(optionsType)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingFromJson = classKeys.Except(jsonKeys).OrderBy(x => x).ToArray();
        var extraInJson = jsonKeys.Except(classKeys).OrderBy(x => x).ToArray();

        missingFromJson.Should().BeEmpty(
            "section '{0}' should carry a JSON key for every bindable property on {1} but is missing: {2}",
            sectionPath, optionsType.Name, string.Join(", ", missingFromJson));
        extraInJson.Should().BeEmpty(
            "section '{0}' should not carry JSON keys without a matching bindable property on {1}, found: {2}",
            sectionPath, optionsType.Name, string.Join(", ", extraInJson));
    }

    public static IEnumerable<object[]> DriftCases() => new[]
    {
        new object[]
        {
            "Caching",
            typeof(CacheOptions),
            new[] { "Connections", "Broadcast", "InMemoryRedis", "Redis", "InMemory", "ResiliencePolicies" },
        },
        new object[] { "Caching:Connections:Redis",      typeof(RedisConnectionOptions),    Array.Empty<string>() },
        new object[] { "Caching:Broadcast:RedisStreams", typeof(RedisStreamsTopicOptions),  new[] { "Topics" } },
        new object[] { "Caching:Broadcast:RedisPubSub",  typeof(RedisPubSubTopicOptions),   new[] { "Topics" } },
        new object[] { "Caching:InMemoryRedis",          typeof(InMemoryRedisCacheOptions), Array.Empty<string>() },
        new object[] { "Caching:Redis",                  typeof(RedisCacheOptions),         Array.Empty<string>() },
        new object[] { "Caching:InMemory",               typeof(InMemoryCacheOptions),      Array.Empty<string>() },
        new object[] { "Caching:ResiliencePolicies",     typeof(ResiliencePoliciesOptions), Array.Empty<string>() },
        new object[] { "Caching:Policies:MyApp.Models.Order",           typeof(CachePolicy),      Array.Empty<string>() },
        new object[] { "Caching:Policies:MyApp.Models.Order:Rehydrate", typeof(RehydrateOptions), Array.Empty<string>() },
        new object[] { "Caching:Policies:MyApp.Models.Order:Lock",      typeof(LockProfile),      Array.Empty<string>() },
    };

    private static IEnumerable<PropertyInfo> BindableProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Where(p => p.GetCustomAttributes<ObsoleteAttribute>().FirstOrDefault() is null)
            .Where(p => IsBindableType(p.PropertyType));

    // Bindable from JSON via Microsoft.Extensions.Configuration: scalars, strings, enums,
    // nullable variants, classes (records, options-nested types), and generic collections.
    // Excluded: delegates, System.Type (the standard ConfigurationBinder has no
    // string → Type converter; Type properties are set programmatically via the options
    // delegate), and non-collection interfaces (code-only seams supplied via DI / builder).
    private static bool IsBindableType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u.IsSubclassOf(typeof(Delegate))) return false;
        if (u == typeof(Type)) return false;
        if (u.IsGenericType)
        {
            var def = u.GetGenericTypeDefinition();
            if (def == typeof(IDictionary<,>) || def == typeof(Dictionary<,>) ||
                def == typeof(IReadOnlyDictionary<,>) ||
                def == typeof(IList<>) || def == typeof(List<>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(ICollection<>) ||
                def == typeof(IEnumerable<>))
            {
                return true;
            }
        }
        if (u.IsInterface) return false;
        return true;
    }
}
