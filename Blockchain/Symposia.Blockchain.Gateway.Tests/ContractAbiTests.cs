using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Symposia.Blockchain.Contracts;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// Every shared binding in Symposia.Blockchain.Contracts (#167) matches a function or event in the
/// forge ABI of its contract: name, parameter types and order, and (for events) which are indexed.
/// An ABI change that isn't mirrored here fails CI instead of failing at runtime.
/// </summary>
public sealed class ContractAbiTests
{
    private static readonly string[] Contracts =
        ["RewardDistributor", "StakingNodeRegistry", "TelemetryCheckpointRegistry", "EpochController", "MockProtocolConfig", "SYMToken"];

    private static readonly Lazy<List<JsonElement>> Abi = new(LoadAbi);

    public static IEnumerable<object[]> Bindings() =>
        typeof(SealEpochFunction).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<FunctionAttribute>() is not null || t.GetCustomAttribute<EventAttribute>() is not null)
            .Select(t => new object[] { t.Name });

    [Theory]
    [MemberData(nameof(Bindings))]
    public void BindingMatchesTheForgeAbi(string typeName)
    {
        var type = typeof(SealEpochFunction).Assembly.GetTypes().Single(t => t.Name == typeName);
        var function = type.GetCustomAttribute<FunctionAttribute>();
        var (kind, name) = function is not null ? ("function", function.Name) : ("event", type.GetCustomAttribute<EventAttribute>()!.Name);
        var parameters = type.GetProperties()
            .Select(p => p.GetCustomAttribute<ParameterAttribute>())
            .Where(p => p is not null)
            .OrderBy(p => p!.Order)
            .Select(p => (Type: Normalize(p!.Type), p.Parameter.Indexed))
            .ToList();

        var candidates = Abi.Value.Where(e => e.GetProperty("type").GetString() == kind && e.TryGetProperty("name", out var n) && n.GetString() == name).ToList();
        Assert.True(candidates.Count > 0, $"{typeName}: no {kind} '{name}' in {string.Join(", ", Contracts)}");
        Assert.Contains(candidates, e =>
        {
            var inputs = e.GetProperty("inputs").EnumerateArray()
                .Select(i => (Type: Normalize(i.GetProperty("type").GetString()!), Indexed: i.TryGetProperty("indexed", out var x) && x.GetBoolean()))
                .ToList();
            return kind == "event" ? inputs.SequenceEqual(parameters) : inputs.Select(i => i.Type).SequenceEqual(parameters.Select(p => p.Type));
        });
    }

    [Fact]
    public void NoBindingsForSharedContractsOutsideTheAssembly()
    {
        var root = BootstrapChain()[..BootstrapChain().LastIndexOf(Path.Combine("Blockchain", "bootstrap-chain"), StringComparison.Ordinal)];
        var shared = Bindings().Select(b => (string)b[0]).ToHashSet();
        var strays = new[] { "Blockchain/Symposia.Blockchain.Gateway", "Network/Symposia.Network.Node", "Network/Symposia.NodeHost" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            // NodeRegistry / EpochRootRegistry / ProtocolActivations bindings: other contracts, outside #167's scope.
            .Where(f => Path.GetFileName(f) != "ContractDefinitions.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"\[(Function|Event)\(""(?<n>\w+)""").Select(m => (File: f, Name: m.Groups["n"].Value)))
            .Where(m => Abi.Value.Any(e => e.TryGetProperty("name", out var n) && n.GetString() == m.Name))
            .ToList();
        Assert.True(strays.Count == 0, "Bindings outside Symposia.Blockchain.Contracts: " + string.Join(", ", strays.Select(s => $"{s.Name} in {Path.GetFileName(s.File)}")));
    }

    // Struct parameters are declared as tuple/tuple[] in both places; uint aliases normalize.
    private static string Normalize(string type) => type.Replace(" ", "") switch
    {
        "uint" => "uint256",
        "int" => "int256",
        var t => t,
    };

    private static List<JsonElement> LoadAbi()
    {
        var dir = BootstrapChain();
        RewardsChainFixture.ForgeGate.Wait();
        try
        {
            BuildIfMissing(dir);
        }
        finally
        {
            RewardsChainFixture.ForgeGate.Release();
        }

        return Contracts.SelectMany(c => JsonDocument.Parse(File.ReadAllText(ArtifactPath(dir, c))).RootElement.GetProperty("abi").EnumerateArray().Select(e => e.Clone())).ToList();
    }

    private static void BuildIfMissing(string dir)
    {
        if (!Contracts.All(c => File.Exists(ArtifactPath(dir, c))))
        {
            var forge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "bin", "forge");
            using var build = Process.Start(new ProcessStartInfo(forge, "build") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
            // Drain both pipes: forge's lint output fills the buffer otherwise and it blocks forever (#174).
            var stdout = build.StandardOutput.ReadToEndAsync();
            var stderr = build.StandardError.ReadToEndAsync();
            if (!build.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                build.Kill(entireProcessTree: true);
                throw new TimeoutException("forge build did not finish within 5 minutes.");
            }

            if (build.ExitCode != 0)
            {
                throw new InvalidOperationException($"forge build failed:\n{stdout.Result}\n{stderr.Result}");
            }
        }

    }

    private static string ArtifactPath(string dir, string contract) => Path.Combine(dir, "out", $"{contract}.sol", $"{contract}.json");

    private static string BootstrapChain()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Blockchain", "bootstrap-chain");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Blockchain/bootstrap-chain not found.");
    }
}
