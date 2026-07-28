using Symposia.Database.ComputeNode.Safekeeping;

namespace Symposia.Database.ComputeNode.Tests.Safekeeping;

/// <summary>
/// Traces to Gherkin "Commit requires full 3-node quorum acknowledgment", "Degraded peer replaced
/// to preserve latency budget", "Safekeeper reassignment on peer failure", and QA TC-14 (no
/// qualifying peer available must fail closed, never silently under-size the quorum).
/// </summary>
public sealed class SafekeeperCoordinationServiceTests
{
    private static SafekeeperCandidate Candidate(string nodeId, double rttMs = 3.0, string region = "us-east", bool nvme = true, bool penalized = false) =>
        new(nodeId, region, rttMs, nvme, penalized);

    [Fact]
    public void AssignInitialPeers_TwoQualifyingCandidates_AssignsBothAsHealthy()
    {
        var service = new SafekeeperCoordinationService();
        var candidates = new[] { Candidate("peer-1"), Candidate("peer-2") };

        var result = service.AssignInitialPeers("db-1", "primary-1", "us-east", candidates);

        Assert.Equal(AssignSafekeepersOutcome.Assigned, result.Outcome);
        Assert.Equal(["peer-1", "peer-2"], result.Assignment!.PeerNodeIds);
        Assert.Equal(QuorumHealth.Healthy, result.Assignment.Status);
    }

    [Fact]
    public void AssignInitialPeers_FewerThanTwoQualifyingCandidates_FailsClosed()
    {
        var service = new SafekeeperCoordinationService();
        var candidates = new[] { Candidate("peer-1", rttMs: 8.0), Candidate("peer-2") };

        var result = service.AssignInitialPeers("db-1", "primary-1", "us-east", candidates);

        Assert.Equal(AssignSafekeepersOutcome.InsufficientQualifyingPeers, result.Outcome);
        Assert.Null(service.GetAssignment("db-1"));
    }

    [Fact]
    public void ReassignPeer_DegradedPeerReplacedWithQualifyingCandidate_PreservesTheOtherPeer()
    {
        var service = new SafekeeperCoordinationService();
        service.AssignInitialPeers("db-1", "primary-1", "us-east", [Candidate("peer-1"), Candidate("peer-2")]);

        var result = service.ReassignPeer("db-1", "peer-1", [Candidate("peer-1", rttMs: 7.0), Candidate("peer-2"), Candidate("peer-3")]);

        Assert.Equal(AssignSafekeepersOutcome.Assigned, result.Outcome);
        Assert.Equal(["peer-2", "peer-3"], result.Assignment!.PeerNodeIds);
        Assert.Equal(QuorumHealth.Healthy, result.Assignment.Status);
    }

    [Fact]
    public void ReassignPeer_NoQualifyingReplacementAvailable_MarksAwaitingQualifyingPeer()
    {
        var service = new SafekeeperCoordinationService();
        service.AssignInitialPeers("db-1", "primary-1", "us-east", [Candidate("peer-1"), Candidate("peer-2")]);

        var result = service.ReassignPeer("db-1", "peer-1", [Candidate("peer-1"), Candidate("peer-2")]);

        Assert.Equal(AssignSafekeepersOutcome.InsufficientQualifyingPeers, result.Outcome);
        Assert.Equal(QuorumHealth.AwaitingQualifyingPeer, result.Assignment!.Status);
        Assert.Equal(QuorumHealth.AwaitingQualifyingPeer, service.GetAssignment("db-1")!.Status);
    }

    [Fact]
    public void ReassignPeer_NeverTouchesThePrimary()
    {
        var service = new SafekeeperCoordinationService();
        service.AssignInitialPeers("db-1", "primary-1", "us-east", [Candidate("peer-1"), Candidate("peer-2")]);

        var result = service.ReassignPeer("db-1", "peer-1", [Candidate("peer-1"), Candidate("peer-2"), Candidate("peer-3")]);

        Assert.Equal("primary-1", result.Assignment!.PrimaryNodeId);
    }

    [Fact]
    public void ReportLagBytes_ReflectsMostRecentReport()
    {
        var service = new SafekeeperCoordinationService();

        service.ReportLagBytes("db-1", 4096);

        Assert.Equal(4096, service.GetLagBytes("db-1"));
    }
}
