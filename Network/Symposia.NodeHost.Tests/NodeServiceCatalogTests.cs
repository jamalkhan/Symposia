using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Symposia.NodeHost.ServiceCatalog;

namespace Symposia.NodeHost.Tests;

/// <summary>
/// The public Node Services earnings breakdown (issues #159, #160, #81). The catalog is pinned to the
/// contracts it describes, so a governance-visible number can't silently drift from what the chain pays.
/// </summary>
public sealed partial class NodeServiceCatalogTests
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

        throw new InvalidOperationException("Could not find the repository root.");
    }

    private static string Contract(string relative) =>
        File.ReadAllText(Path.Combine(Root, "Blockchain", "bootstrap-chain", relative));

    [Fact]
    public void ListsEveryOnChainServiceOnce_InEnumOrder()
    {
        var source = Contract("src/interfaces/INodeRegistry.sol");
        var body = Regex.Match(source, @"enum NodeType \{(?<b>[^}]*)\}").Groups["b"].Value;
        var names = Regex.Replace(body, @"//[^\n]*", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Equal(11, names.Count);
        Assert.Equal(names.Count, NodeServiceCatalog.All.Count);
        Assert.Equal(Enumerable.Range(0, names.Count), NodeServiceCatalog.All.Select(s => s.Id).Order());
        foreach (var svc in NodeServiceCatalog.All)
        {
            var onChain = names[svc.Id];
            Assert.Equal(onChain.ToLowerInvariant(), svc.Name.Replace(" ", "").Replace("Address", "").ToLowerInvariant());
        }
    }

    [Fact]
    public void EarningFlags_MatchTheProductModel()
    {
        var byKey = NodeServiceCatalog.All.ToDictionary(s => s.Key);
        Assert.True(byKey["storage"].Earns);
        Assert.True(byKey["verifier"].Earns);
        Assert.True(byKey["blob-gateway"].Earns);
        Assert.False(byKey["email-ip"].Earns);
        Assert.Equal(NodeServiceCatalog.NeverEarns, byKey["email-ip"].Status);
        Assert.Equal(NodeServiceCatalog.Strawman, byKey["blob-gateway"].Status);
        Assert.Equal(NodeServiceCatalog.Pinned, byKey["compute"].Status);
        Assert.All(new[] { "storage", "blob-gateway", "compute" }, k => Assert.NotNull(byKey[k].SettingsSection));
    }

    [Fact]
    public void FactorWeights_MatchScoringWeightsTables()
    {
        var source = Contract("src/ScoringWeights.sol");
        foreach (var svc in NodeServiceCatalog.All.Where(s => s.Factors.Count > 0))
        {
            Assert.Equal(10_000, svc.Factors.Sum(f => f.WeightBps));
            // Tables are pushed by ordinal or by a named constant (VERIFIER, BLOB_GATEWAY).
            var key = svc.Id switch { 5 => "VERIFIER", 8 => "BLOB_GATEWAY", var id => id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var push = Regex.Match(source, $@"_push\({key}, 0, _u16\(\[uint256\((?<w>[^\]]*)\]\)\)");
            Assert.True(push.Success, $"ScoringWeights has no table for {svc.Name} ({svc.Id})");
            var onChain = push.Groups["w"].Value.Replace(")", "").Split(',').Select(w => int.Parse(w.Trim().Replace("_", ""))).ToList();
            Assert.Equal(onChain, svc.Factors.Select(f => f.WeightBps).ToList());
        }
    }

    [Fact]
    public void PoolShares_MatchTheDeployScript()
    {
        var deploy = Contract("script/DeployRewards.s.sol");
        int Weight(int type) => int.Parse(Regex.Match(deploy, $@"rewardTypeWeight\({type}\), (?<w>[\d_]+)\)").Groups["w"].Value.Replace("_", ""));
        var byKey = NodeServiceCatalog.All.ToDictionary(s => s.Key);
        Assert.StartsWith($"{Weight(0) / 100}% of the capacity slice", byKey["storage"].PoolShare);
        Assert.StartsWith($"{Weight(8) / 100}% of the capacity slice", byKey["blob-gateway"].PoolShare);
        Assert.Contains("REWARD_VERIFIER_SHARE_BPS, 800", deploy);
        Assert.StartsWith("8%", byKey["verifier"].Funding);
    }

    [Fact]
    public void ServiceSettings_LiveOnTheNodeServicesTab()
    {
        var html = File.ReadAllText(Path.Combine(Root, "Network", "Symposia.NodeHost", "wwwroot", "index.html"));
        var services = Section(html, "view-services");
        var settings = Section(html, "view-settings");
        foreach (var field in new[] { "Storage.Enabled", "Compute.Enabled", "BlobGateway.Enabled", "Storage.Port", "BlobGateway.AccessKeyId" })
        {
            Assert.Contains($"name=\"{field}\"", services);
            Assert.DoesNotContain($"name=\"{field}\"", settings);
        }

        Assert.Contains("data-view=\"services\"", html);
        Assert.DoesNotMatch(RoleWord(), html);
    }

    [Fact]
    public void LedgerLabels_MatchTheSolidityEnums()
    {
        static int Count(string source, string name)
        {
            var body = Regex.Match(source, $@"enum {name} \{{(?<b>[^}}]*)\}}").Groups["b"].Value;
            return Regex.Replace(body, @"//[^\n]*", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        }

        Assert.Equal(Count(Contract("src/RewardDistributor.sol"), "SettlementReason"), Ledger.LedgerService.SettlementReasons.Length);
        Assert.Equal(Count(Contract("src/interfaces/INodeRegistry.sol"), "NodeStatus"), Ledger.LedgerService.Statuses.Length);
        Assert.Equal(4, Count(Contract("src/StakingNodeRegistry.sol"), "StakeChangeReason")); // Register, TopUp, Withdraw, Slash in LedgerService.Map
    }

    private static string Section(string html, string id)
    {
        var start = html.IndexOf($"<section id=\"{id}\"", StringComparison.Ordinal);
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    [GeneratedRegex(@"\brole\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoleWord();
}
