using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Signer;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Issue #145: one network policy decides which storage nodes a gateway uses (QA TC-22..TC-33).</summary>
public sealed class NetworkPolicyTests
{
    private const ulong Chain = 31337;
    private const string Registry = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
    private static readonly long Past = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
    private static readonly long Future = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();

    private readonly EthECKey _key = EthECKey.GenerateKey();

    private static NetworkPolicy Policy(params ProtocolSchedule.Activation[] features) => Policy(new ProtocolSchedule.MinVersion(0, 0, 0), features);

    private static NetworkPolicy Policy(ProtocolSchedule.MinVersion min, params ProtocolSchedule.Activation[] features)
    {
        var policy = new NetworkPolicy(Microsoft.Extensions.Options.Options.Create(new GatewayOptions { ChainId = Chain, NodeRegistryAddress = Registry }),
            NullLogger<NetworkPolicy>.Instance);
        policy.Apply(new ProtocolSchedule(true, 0, min, features));
        return policy;
    }

    private string Node => _key.GetPublicAddress();

    /// <summary>A current node: TLS with a valid proof, plus cleartext during the overlap.</summary>
    private NodeServices Current(bool plaintext = true, int version = 1)
    {
        var fp = SHA256.HashData("cert"u8);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        var features = new List<string> { ProtocolFeatures.StorageGrpcTls, ProtocolFeatures.StorageRpcAuthV1 };
        if (plaintext)
        {
            features.Add(ProtocolFeatures.StorageGrpcPlaintext);
        }

        return new NodeServices(Node, new NodeServices.StorageService(plaintext ? "http://10.0.0.1:6180" : null,
            Convert.ToHexStringLower(fp), StorageTlsProof.Sign(_key, Chain, Registry, fp, notAfter), notAfter, "https://10.0.0.1:6280"), version, features);
    }

    /// <summary>A node that never turned TLS on (stands in for the previous release).</summary>
    private NodeServices Legacy(int version = 1) =>
        new(Node, new NodeServices.StorageService("http://10.0.0.1:6180"), version, [ProtocolFeatures.StorageGrpcPlaintext]);

    [Fact]
    public void BothOffered_PrefersPinnedTls()
    {
        var admitted = Policy().AdmitStorageNode(Node, Current());
        Assert.Equal("https://10.0.0.1:6280", admitted.Node!.Url);
        Assert.NotNull(admitted.Node.TlsFingerprint);
        Assert.Empty(admitted.Warnings);
    }

    [Fact]
    public void CleartextOnly_BeforeActivation_IsUsedWithAWarning()
    {
        var admitted = Policy(new ProtocolSchedule.Activation(ProtocolFeatures.StorageGrpcTls, Future)).AdmitStorageNode(Node, Legacy());
        Assert.Equal("http://10.0.0.1:6180", admitted.Node!.Url);
        Assert.Null(admitted.Node.TlsFingerprint);
        Assert.Contains(admitted.Warnings, w => w.Contains(ProtocolFeatures.StorageGrpcTls));
    }

    [Fact]
    public void CleartextOnly_AfterActivation_IsRefused()
    {
        var policy = Policy(new ProtocolSchedule.Activation(ProtocolFeatures.StorageGrpcTls, Past));
        var admitted = policy.AdmitStorageNode(Node, Legacy());
        Assert.Null(admitted.Node);
        Assert.Contains(ProtocolFeatures.StorageGrpcTls, admitted.RefusedBecause);
        Assert.True(policy.IsActive(ProtocolFeatures.StorageGrpcTls));
        Assert.NotNull(policy.AdmitStorageNode(Node, Current()).Node);
    }

    [Fact]
    public void CancelledActivation_RequiresNothing()
    {
        var policy = Policy(new ProtocolSchedule.Activation(ProtocolFeatures.StorageGrpcTls, 0));
        Assert.False(policy.IsActive(ProtocolFeatures.StorageGrpcTls));
        Assert.NotNull(policy.AdmitStorageNode(Node, Legacy()).Node);
    }

    [Fact]
    public void NewerStorageFeatureThisGatewayDoesNotKnow_IsStillEnforcedByName()
    {
        var admitted = Policy(new ProtocolSchedule.Activation("storage.multipart.v2", Past)).AdmitStorageNode(Node, Current());
        Assert.Null(admitted.Node);
        Assert.Contains("storage.multipart.v2", admitted.RefusedBecause);
    }

    [Fact]
    public void FeaturesOfOtherRoles_DoNotApplyToStorageNodes()
    {
        var admitted = Policy(new ProtocolSchedule.Activation("compute.pageserver.v2", Past)).AdmitStorageNode(Node, Current());
        Assert.NotNull(admitted.Node);
        Assert.Empty(admitted.Warnings);
    }

    [Fact]
    public void MinimumVersion_RefusesOlderNodes_AndWarnsAheadOfARise()
    {
        Assert.Null(Policy(new ProtocolSchedule.MinVersion(2, 0, 0)).AdmitStorageNode(Node, Current(version: 1)).Node);
        Assert.NotNull(Policy(new ProtocolSchedule.MinVersion(1, 0, 0)).AdmitStorageNode(Node, Current(version: 1)).Node);

        var pending = Policy(new ProtocolSchedule.MinVersion(1, 2, Future)).AdmitStorageNode(Node, Current(version: 1));
        Assert.NotNull(pending.Node);
        Assert.Contains(pending.Warnings, w => w.Contains("version 2"));

        // A pending rise whose time has passed is in effect even before the chain view says so.
        Assert.Null(Policy(new ProtocolSchedule.MinVersion(1, 2, Past)).AdmitStorageNode(Node, Current(version: 1)).Node);
    }

    [Fact]
    public void ActiveRequirements_AreNeverForgotten_WhenLaterReadsDropThem()
    {
        var policy = Policy(new ProtocolSchedule.MinVersion(2, 0, 0), new ProtocolSchedule.Activation(ProtocolFeatures.StorageGrpcTls, Past));

        policy.Apply(ProtocolSchedule.None); // e.g. a misbehaving or replaced Blockchain Gateway

        Assert.True(policy.IsActive(ProtocolFeatures.StorageGrpcTls));
        Assert.Null(policy.AdmitStorageNode(Node, Legacy(version: 5)).Node);
        Assert.Null(policy.AdmitStorageNode(Node, Current(version: 1)).Node);
    }

    [Fact]
    public void BadTlsProof_RefusesTheNode_InsteadOfFallingBackToCleartext()
    {
        var forged = Current() with { Node = EthECKey.GenerateKey().GetPublicAddress() };
        var admitted = Policy().AdmitStorageNode(forged.Node, forged);
        Assert.Null(admitted.Node);
        Assert.Contains("TLS", admitted.RefusedBecause);
    }

    [Fact]
    public void PreReleaseDocument_WithTlsInGrpc_IsReadAsTls()
    {
        var current = Current();
        // #142 pre-release hosts: TLS URL in "grpc", no grpcTls, no protocol fields.
        var old = new NodeServices(Node, current.Storage! with { Grpc = "https://10.0.0.1:6180", GrpcTls = null });
        var policy = Policy(new ProtocolSchedule.Activation(ProtocolFeatures.StorageGrpcTls, Past));
        Assert.Equal("https://10.0.0.1:6180", policy.AdmitStorageNode(Node, old).Node!.Url);
    }

    [Fact]
    public void NoStorageAddress_IsRefused() =>
        Assert.Null(Policy().AdmitStorageNode(Node, new NodeServices(Node, new NodeServices.StorageService(null), 1, [])).Node);

    [Fact]
    public void Apply_ReportsChangesOnlyWhenTheScheduleChanges()
    {
        var policy = Policy();
        var schedule = new ProtocolSchedule(true, 1, new ProtocolSchedule.MinVersion(0, 0, 0), [new ProtocolSchedule.Activation("storage.x", Future)]);
        Assert.True(policy.Apply(schedule));
        Assert.False(policy.Apply(schedule with { Now = 2, Features = [new ProtocolSchedule.Activation("storage.x", Future)] }));
        Assert.True(policy.Apply(schedule with { Features = [new ProtocolSchedule.Activation("storage.x", Future + 1)] }));
    }
}
