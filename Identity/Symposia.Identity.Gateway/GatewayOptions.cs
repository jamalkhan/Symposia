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

    /// <summary>How long a write waits for its receipt before answering 202 pending with the tx hash (#222).</summary>
    public TimeSpan ReceiptTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the receipt is polled while waiting.</summary>
    public TimeSpan ReceiptPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Bound on each signing step and broadcast call, so one hung RPC can't hold the relayer's nonce gate.</summary>
    public TimeSpan RpcStepTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a write waits for its turn at the nonce gate before answering 503.</summary>
    public TimeSpan GateTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
