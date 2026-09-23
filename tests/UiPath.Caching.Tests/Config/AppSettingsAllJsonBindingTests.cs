using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using UiPath.Caching.Azure;
using UiPath.Caching.Polly;

namespace UiPath.Caching.Tests.Config;

/// <summary>Checks that <c>appsettings.all.json</c> lists every binding-visible option.</summary>
public class AppSettingsAllJsonBindingTests
{
    private const string OrderPolicy = "Caching:Policies:MyApp.Models.Order";

    private static readonly string AppSettingsAllPath = Path.Combine(AppContext.BaseDirectory, "appsettings.all.json");

    private static readonly HashSet<Type> BindableGenerics =
    [
        typeof(IDictionary<,>), typeof(Dictionary<,>), typeof(IReadOnlyDictionary<,>),
        typeof(IList<>), typeof(List<>), typeof(IReadOnlyList<>), typeof(ICollection<>), typeof(IEnumerable<>),
    ];

    // SecondaryRedis is deliberately abbreviated, so it is not listed.
    public static TheoryData<string, Type[], string[]> DriftCases() => new()
    {
        { "Caching", [typeof(CacheOptions)], ["Connections", "AzureEntra", "Broadcast", "InMemoryRedis", "Redis", "InMemory", "ResiliencePolicies", "Queue"] },
        { "Caching:Connections:Redis", [typeof(RedisConnectionOptions)], [] },
        { "Caching:AzureEntra", [typeof(AzureEntraOptions)], [] },
        { "Caching:Broadcast:RedisStreams", [typeof(RedisStreamsTopicOptions)], ["Topics"] },
        { "Caching:Broadcast:RedisPubSub", [typeof(RedisPubSubTopicOptions)], ["Topics"] },
        { "Caching:InMemoryRedis", [typeof(InMemoryRedisCacheOptions)], [] },
        { "Caching:Redis", [typeof(RedisCacheOptions)], [] },
        { "Caching:InMemory", [typeof(InMemoryCacheOptions)], [] },
        { "Caching:ResiliencePolicies", [typeof(ResiliencePoliciesOptions)], [] },
        { "Caching:Queue:Redis", [typeof(RedisSetCacheOptions)], [] },
        { "Caching:Queue:InMemory", [typeof(InMemoryQueueCacheOptions)], [] },
        { "Caching:Queue:InMemoryRedis", [typeof(InMemoryRedisQueueCacheOptions)], [] },
        { OrderPolicy, [typeof(CachePolicy)], [] },
        { OrderPolicy + ":Rehydrate", [typeof(RehydrateOptions)], [] },
        { OrderPolicy + ":Lock", [typeof(LockProfile)], [] },
    };

    // A container only groups sections, so it must hold exactly the ones the file is expected to carry.
    public static TheoryData<string, string[]> ContainerCases() => new()
    {
        { "Caching:Connections", ["Redis", "SecondaryRedis"] },
        { "Caching:Broadcast", ["RedisStreams", "RedisPubSub"] },
        { "Caching:Queue", ["Redis", "InMemory", "InMemoryRedis"] },
        { "Caching:Policies", ["MyApp.Models.Order"] },
    };

    // A topic entry overrides only what it names, so its keys are checked one way: none may be unknown.
    public static TheoryData<string, Type> TopicCases() => new()
    {
        { "Caching:Broadcast:RedisStreams:Topics", typeof(RedisStreamsTopicOptions) },
        { "Caching:Broadcast:RedisPubSub:Topics", typeof(RedisPubSubTopicOptions) },
    };

    [Fact]
    public void The_file_is_valid_jsonc_and_binds_the_core_options()
    {
        var opts = BuildConfig().GetSection("Caching").Get<CacheOptions>();

        opts.Should().NotBeNull();
        opts!.AppShortName.Should().Be("app");
        opts.LocalLockPoolSize.Should().Be(100);
    }

    // The binder ignores unknown keys and defaults missing ones, so only comparing key sets catches drift.
    [Theory]
    [MemberData(nameof(DriftCases))]
    public void Json_keys_match_bindable_properties(string sectionPath, Type[] optionsTypes, string[] subSectionAllowlist)
    {
        var section = BuildConfig().GetSection(sectionPath);
        section.Exists().Should().BeTrue("'{0}' is listed as a drift case", sectionPath);

        var names = string.Join(" / ", optionsTypes.Select(o => o.Name));
        var jsonKeys = section.GetChildren()
            .Select(c => c.Key)
            .Except(subSectionAllowlist, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Obsolete keys are allowed but not required: some, like ShardKeyEnabled, are still read at runtime.
        var required = Keys(optionsTypes, includeObsolete: false);
        var permitted = Keys(optionsTypes, includeObsolete: true);

        var extra = jsonKeys.Except(permitted, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var absent = required.Except(jsonKeys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

        extra.Should().BeEmpty("'{0}' carries keys with no bindable property on {1}: {2}", sectionPath, names, string.Join(", ", extra));
        absent.Should().BeEmpty("'{0}' is missing keys for bindable properties on {1}: {2}", sectionPath, names, string.Join(", ", absent));
    }

    [Theory]
    [MemberData(nameof(ContainerCases))]
    public void Container_sections_hold_exactly_the_expected_children(string containerPath, string[] expected)
    {
        var children = BuildConfig().GetSection(containerPath).GetChildren().Select(c => c.Key).ToArray();

        var extra = children.Except(expected, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var absent = expected.Except(children, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

        extra.Should().BeEmpty("'{0}' holds sections no case checks: {1}", containerPath, string.Join(", ", extra));
        absent.Should().BeEmpty("'{0}' is missing sections: {1}", containerPath, string.Join(", ", absent));
    }

    [Theory]
    [MemberData(nameof(TopicCases))]
    public void Topic_entries_carry_a_name_and_only_bindable_keys(string topicsPath, Type optionsType)
    {
        var entries = BuildConfig().GetSection(topicsPath).GetChildren().ToArray();
        entries.Should().NotBeEmpty("'{0}' is listed as a topic case", topicsPath);

        var permitted = Keys([optionsType], includeObsolete: true);
        permitted.Add("Name");
        foreach (var entry in entries)
        {
            entry["Name"].Should().NotBeNullOrWhiteSpace("an entry without a Name is skipped at '{0}'", entry.Path);
            var extra = entry.GetChildren().Select(c => c.Key).Where(k => !permitted.Contains(k)).OrderBy(x => x).ToArray();
            extra.Should().BeEmpty("'{0}' carries keys with no bindable property on {1}: {2}", entry.Path, optionsType.Name, string.Join(", ", extra));
        }
    }

    private static HashSet<string> Keys(Type[] optionsTypes, bool includeObsolete) =>
        optionsTypes
            .SelectMany(o => BindableProperties(o, includeObsolete))
            .Select(prop => prop.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IConfiguration BuildConfig()
    {
        var stripped = StripJsoncComments(File.ReadAllText(AppSettingsAllPath));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(stripped));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    // Line comments only; a `//` inside a string survives.
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

    private static IEnumerable<PropertyInfo> BindableProperties(Type type, bool includeObsolete) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Where(p => includeObsolete || p.GetCustomAttribute<ObsoleteAttribute>() is null)
            .Where(p => IsBindableType(p.PropertyType));

    // Delegates, Type, abstract classes and non-collection interfaces have no JSON form; they are set in code or through DI.
    private static bool IsBindableType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsSubclassOf(typeof(Delegate)) || underlying == typeof(Type) || (underlying.IsAbstract && !underlying.IsInterface))
        {
            return false;
        }

        if (underlying.IsGenericType && BindableGenerics.Contains(underlying.GetGenericTypeDefinition()))
        {
            return true;
        }

        return !underlying.IsInterface;
    }
}
