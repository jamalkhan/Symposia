namespace Symposia.Identity.Gateway;

public sealed class GatewayOptions
{
    /// <summary>Domain bound into every SIWE challenge, per EIP-4361 §"Domain binding".</summary>
    public string SiweDomain { get; set; } = "symposia.network";

    /// <summary>How long an issued SIWE challenge remains valid before TC-2.5 rejects it.</summary>
    public TimeSpan ChallengeTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>JSON-RPC endpoint for the protocol L3 (chain-architecture.md).</summary>
    public string ChainRpcUrl { get; set; } = "http://localhost:8545";

    public string ConsentRegistryAddress { get; set; } = "";

    public string CapabilityRegistryAddress { get; set; } = "";

    /// <summary>Relayer account that pays gas to submit signed consent/revocation/capability
    /// transactions on the individual's behalf (the individual's authority comes from their
    /// own signature, not from this key).</summary>
    public string RelayerPrivateKey { get; set; } = "";
}
