using Symposia.Database.ComputeNode.Safekeeping;

namespace Symposia.Database.ComputeNode.Tests.Safekeeping;

/// <summary>
/// Traces to the QA test plan's peer eligibility section (TC-08–TC-14): RTT, region, and NVMe
/// constraints are all required jointly, and the 5ms RTT boundary is inclusive.
/// </summary>
public sealed class SafekeeperEligibilityTests
{
    private static SafekeeperCandidate Candidate(
        string nodeId = "node-a",
        string region = "us-east",
        double rttMs = 3.0,
        bool nvme = true,
        bool penalized = false) =>
        new(nodeId, region, rttMs, nvme, penalized);

    [Fact]
    public void IsEligible_FullyQualifyingCandidate_IsEligible()
    {
        Assert.True(SafekeeperEligibility.IsEligible(Candidate(), "us-east"));
    }

    [Fact]
    public void IsEligible_ExactlyFiveMsRtt_IsEligible()
    {
        Assert.True(SafekeeperEligibility.IsEligible(Candidate(rttMs: 5.0), "us-east"));
    }

    [Fact]
    public void IsEligible_JustOverFiveMsRtt_IsRejected()
    {
        Assert.False(SafekeeperEligibility.IsEligible(Candidate(rttMs: 5.01), "us-east"));
    }

    [Fact]
    public void IsEligible_EightMsRtt_IsRejected()
    {
        Assert.False(SafekeeperEligibility.IsEligible(Candidate(rttMs: 8.0), "us-east"));
    }

    [Fact]
    public void IsEligible_CrossRegionCandidate_IsRejectedRegardlessOfRtt()
    {
        Assert.False(SafekeeperEligibility.IsEligible(Candidate(region: "us-west", rttMs: 1.0), "us-east"));
    }

    [Fact]
    public void IsEligible_NonNvmeCandidate_IsRejected()
    {
        Assert.False(SafekeeperEligibility.IsEligible(Candidate(nvme: false), "us-east"));
    }

    [Fact]
    public void IsEligible_PenalizedCandidate_IsRejected()
    {
        Assert.False(SafekeeperEligibility.IsEligible(Candidate(penalized: true), "us-east"));
    }

    [Fact]
    public void SelectQualifying_OrdersByRttAscendingAndExcludesGivenNodes()
    {
        var candidates = new[]
        {
            Candidate("node-b", rttMs: 4.0),
            Candidate("node-c", rttMs: 1.0),
            Candidate("node-d", rttMs: 2.0),
            Candidate("primary", rttMs: 0.1),
        };

        var selected = SafekeeperEligibility.SelectQualifying(candidates, "us-east", count: 2, new HashSet<string> { "primary" });

        Assert.Equal(["node-c", "node-d"], selected.Select(c => c.NodeId));
    }

    [Fact]
    public void SelectQualifying_FewerThanRequestedQualify_ReturnsShortList()
    {
        var candidates = new[] { Candidate("node-b", rttMs: 8.0), Candidate("node-c", rttMs: 1.0) };

        var selected = SafekeeperEligibility.SelectQualifying(candidates, "us-east", count: 2);

        Assert.Single(selected);
        Assert.Equal("node-c", selected[0].NodeId);
    }
}
