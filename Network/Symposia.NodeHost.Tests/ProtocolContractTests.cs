using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Symposia.Network.Node;

namespace Symposia.NodeHost.Tests;

/// <summary>
/// Protocol compatibility checks that need the .NET types (issue #146; QA TC-13, TC-14, TC-22..TC-24).
/// They live here because the Node Host references every component that ships in a node. The
/// diff-based checks (schemas, fixtures, registry history) are in tools/protocol-compat.
/// Rules and fixes: docs/engineering/protocol-compatibility.md#the-checks
/// </summary>
public sealed class ProtocolContractTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "protocol", "features.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (protocol/features.json).");
    }

    private static JsonNode Load(string relative) => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, relative)))!;

    // ── Feature constants ↔ registry ──────────────────────────────────────────

    [Fact]
    public void FeatureConstants_AndRegistry_AgreeAndNoRetiredFeatureIsReferenced()
    {
        var constants = typeof(ProtocolFeatures).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name);
        var registry = ProtocolFeatures.Registry.Features.ToDictionary(f => f.Name);

        foreach (var (value, name) in constants)
        {
            Assert.True(registry.ContainsKey(value), $"ProtocolFeatures.{name} = \"{value}\" is not in protocol/features.json.");
            Assert.True(registry[value].Stage != FeatureStage.Retired,
                $"ProtocolFeatures.{name} refers to retired feature \"{value}\"; remove the constant and the code using it.");
        }

        foreach (var feature in registry.Values.Where(f => f.Stage != FeatureStage.Retired))
        {
            Assert.True(constants.ContainsKey(feature.Name), $"protocol/features.json has \"{feature.Name}\" but ProtocolFeatures has no constant for it.");
        }
    }

    [Fact]
    public void EmbeddedRegistry_IsTheRepositoryFile()
    {
        var file = Load("protocol/features.json");
        Assert.Equal(file["release"]!.GetValue<string>(), ProtocolFeatures.Registry.Release);
        Assert.Equal(file["protocolVersion"]!.GetValue<int>(), ProtocolFeatures.ProtocolVersion);
    }

    // ── Records ↔ schemas ─────────────────────────────────────────────────────

    public static TheoryData<string, object> Documents => new()
    {
        {
            "node-services",
            new NodeServices("0xA", new NodeServices.StorageService("http://h:6180", "ff", "0x00", 1, "https://h:6280"), 1, ["storage.grpc.tls"])
        },
        {
            "protocol-schedule",
            new ProtocolSchedule(true, 1, new ProtocolSchedule.MinVersion(1, 2, 3), [new ProtocolSchedule.Activation("storage.grpc.tls", 4)])
        },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Record_AndSchema_HaveTheSameProperties(string schemaName, object populated)
    {
        var schema = Load($"protocol/schemas/{schemaName}.schema.json");
        var json = JsonSerializer.SerializeToNode(populated, JsonSerializerOptions.Web)!;

        var inRecord = Paths(json, "$").ToHashSet();
        var inSchema = SchemaPaths(schema, "$").ToHashSet();

        Assert.True(inRecord.IsSubsetOf(inSchema),
            $"Serialized properties missing from {schemaName}.schema.json: {string.Join(", ", inRecord.Except(inSchema))}. Add them to the schema (optional).");
        Assert.True(inSchema.IsSubsetOf(inRecord),
            $"{schemaName}.schema.json has properties the record doesn't serialize: {string.Join(", ", inSchema.Except(inRecord))}. Never remove a property; keep populating it.");
    }

    private static IEnumerable<string> Paths(JsonNode? node, string path) => node switch
    {
        JsonObject o => o.SelectMany(p => Paths(p.Value, $"{path}.{p.Key}").Prepend($"{path}.{p.Key}")),
        JsonArray a => a.Take(1).SelectMany(item => Paths(item, path + "[]")),
        _ => [],
    };

    private static IEnumerable<string> SchemaPaths(JsonNode schema, string path)
    {
        if (schema["properties"] is JsonObject props)
        {
            foreach (var (name, child) in props)
            {
                yield return $"{path}.{name}";
                foreach (var nested in SchemaPaths(child!, $"{path}.{name}"))
                {
                    yield return nested;
                }
            }
        }

        if (schema["items"] is JsonObject items)
        {
            foreach (var nested in SchemaPaths(items, path + "[]"))
            {
                yield return nested;
            }
        }
    }

    // ── Golden fixtures still parse ───────────────────────────────────────────

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "protocol", "fixtures"), "*.json", SearchOption.AllDirectories).Order())
        {
            data.Add(Path.GetRelativePath(Root, file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_DeserializesWithThisBuild(string fixture)
    {
        var text = File.ReadAllText(Path.Combine(Root, fixture));
        var kind = Path.GetFileName(Path.GetDirectoryName(fixture));
        switch (kind)
        {
            case "node-services":
                var services = JsonSerializer.Deserialize<NodeServices>(text, JsonSerializerOptions.Web)!;
                Assert.False(string.IsNullOrEmpty(services.Node));
                if (services.Storage is not null)
                {
                    Assert.True(services.Storage.TlsUrl is not null || services.Storage.PlaintextUrl is not null, $"{fixture}: no usable storage address");
                }

                break;
            case "protocol-schedule":
                Assert.NotNull(JsonSerializer.Deserialize<ProtocolSchedule>(text, JsonSerializerOptions.Web)!.Features);
                break;
            default:
                Assert.Fail($"{fixture}: no deserialization test for fixture folder '{kind}'. Add one here.");
                break;
        }
    }

    // ── No local peer-policy switches ─────────────────────────────────────────

    [Fact]
    public void OptionsTypes_HaveNoUnreviewedPolicySwitches()
    {
        var allowlist = Load("protocol/options-allowlist.json")["allowed"]!.AsArray()
            .ToDictionary(e => e!["property"]!.GetValue<string>(), e => e!["reason"]!.GetValue<string>());
        Assert.All(allowlist, e => Assert.False(string.IsNullOrWhiteSpace(e.Value), $"{e.Key}: allowlist entry needs a reason"));

        var found = NodeAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsPublic && t.Name.EndsWith("Options", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(bool) && PolicySwitchPrefixes.Any(prefix => p.Name.StartsWith(prefix, StringComparison.Ordinal)))
                .Select(p => $"{t.FullName}.{p.Name}"))
            .ToHashSet();

        var unreviewed = found.Except(allowlist.Keys).ToList();
        Assert.True(unreviewed.Count == 0,
            $"Peer-acceptance switches belong in NetworkPolicy behind an on-chain activation, not in options: {string.Join(", ", unreviewed)}. " +
            "If the switch is node-local, add it to protocol/options-allowlist.json with a reason.");

        var stale = allowlist.Keys.Except(found).ToList();
        Assert.True(stale.Count == 0, $"protocol/options-allowlist.json lists properties that no longer exist: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// Names that usually mean "decide which peers are acceptable". A naming rule, not a semantic one:
    /// reviewers still catch switches named otherwise (#146 review).
    /// </summary>
    private static readonly string[] PolicySwitchPrefixes = ["Require", "Allow", "Accept", "Skip", "Disable", "Insecure", "Trust"];

    private static IEnumerable<Assembly> NodeAssemblies() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Symposia.*.dll")
            .Where(f => !Path.GetFileName(f).Contains(".Tests", StringComparison.Ordinal))
            .Select(f => Assembly.LoadFrom(f));
}
