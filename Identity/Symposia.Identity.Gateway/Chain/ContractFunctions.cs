using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Identity.Gateway.Chain;

[Event("CapabilityIssued")]
public sealed class CapabilityIssuedEventDto : IEventDTO
{
    [Parameter("uint256", "tokenId", 1, true)]
    public System.Numerics.BigInteger TokenId { get; set; }

    [Parameter("address", "wallet", 2, true)]
    public string Wallet { get; set; } = "";

    [Parameter("bytes32", "tenantId", 3, true)]
    public byte[] TenantId { get; set; } = [];

    [Parameter("uint8", "permission", 4, false)]
    public byte Permission { get; set; }

    [Parameter("uint64", "issuedAt", 5, false)]
    public ulong IssuedAt { get; set; }

    [Parameter("uint64", "consentGrantedAt", 6, false)]
    public ulong ConsentGrantedAt { get; set; }
}

// Function/output DTOs mirroring Blockchain/bootstrap-chain/src/ConsentRegistry.sol
// and CapabilityRegistry.sol exactly. The chain is authoritative; these types only
// describe the wire format for relaying calls to it.

[Function("grantConsent")]
public sealed class GrantConsentFunction : FunctionMessage
{
    [Parameter("address", "wallet", 1)]
    public string Wallet { get; set; } = "";

    [Parameter("bytes32", "tenantId", 2)]
    public byte[] TenantId { get; set; } = [];

    [Parameter("uint8[]", "permissions", 3)]
    public List<byte> Permissions { get; set; } = [];

    [Parameter("bytes32", "grantSourceHash", 4)]
    public byte[] GrantSourceHash { get; set; } = [];

    [Parameter("bytes32", "grantWordingHash", 5)]
    public byte[] GrantWordingHash { get; set; } = [];

    [Parameter("uint256", "nonce", 6)]
    public new System.Numerics.BigInteger Nonce { get; set; }

    [Parameter("uint256", "deadline", 7)]
    public System.Numerics.BigInteger Deadline { get; set; }

    [Parameter("bytes", "signature", 8)]
    public byte[] Signature { get; set; } = [];
}

[Function("revokeConsent")]
public sealed class RevokeConsentFunction : FunctionMessage
{
    [Parameter("address", "wallet", 1)]
    public string Wallet { get; set; } = "";

    [Parameter("bytes32", "tenantId", 2)]
    public byte[] TenantId { get; set; } = [];

    [Parameter("uint8[]", "permissions", 3)]
    public List<byte> Permissions { get; set; } = [];

    [Parameter("uint256", "nonce", 4)]
    public new System.Numerics.BigInteger Nonce { get; set; }

    [Parameter("uint256", "deadline", 5)]
    public System.Numerics.BigInteger Deadline { get; set; }

    [Parameter("bytes", "signature", 6)]
    public byte[] Signature { get; set; } = [];
}

[Function("hasActiveConsent", "bool")]
public sealed class HasActiveConsentFunction : FunctionMessage
{
    [Parameter("address", "wallet", 1)]
    public string Wallet { get; set; } = "";

    [Parameter("bytes32", "tenantId", 2)]
    public byte[] TenantId { get; set; } = [];

    [Parameter("uint8", "permission", 3)]
    public byte Permission { get; set; }
}

[Function("consentState", typeof(ConsentStateOutput))]
public sealed class ConsentStateFunction : FunctionMessage
{
    [Parameter("address", "wallet", 1)]
    public string Wallet { get; set; } = "";

    [Parameter("bytes32", "tenantId", 2)]
    public byte[] TenantId { get; set; } = [];

    [Parameter("uint8", "permission", 3)]
    public byte Permission { get; set; }
}

[FunctionOutput]
public sealed class ConsentStateOutput : IFunctionOutputDTO
{
    [Parameter("bool", "granted", 1)]
    public bool Granted { get; set; }

    [Parameter("uint64", "grantedAt", 2)]
    public ulong GrantedAt { get; set; }

    [Parameter("bytes32", "grantSourceHash", 3)]
    public byte[] GrantSourceHash { get; set; } = [];

    [Parameter("bytes32", "grantWordingHash", 4)]
    public byte[] GrantWordingHash { get; set; } = [];
}

[Function("nonces", "uint256")]
public sealed class NoncesFunction : FunctionMessage
{
    [Parameter("address", "wallet", 1)]
    public string Wallet { get; set; } = "";
}

[Function("issueCapability", "uint256")]
public sealed class IssueCapabilityFunction : FunctionMessage
{
    [Parameter("address", "wallet", 1)]
    public string Wallet { get; set; } = "";

    [Parameter("bytes32", "tenantId", 2)]
    public byte[] TenantId { get; set; } = [];

    [Parameter("uint8", "permission", 3)]
    public byte Permission { get; set; }
}

[Function("getCapability", typeof(CapabilityTokenOutput))]
public sealed class GetCapabilityFunction : FunctionMessage
{
    [Parameter("uint256", "tokenId", 1)]
    public System.Numerics.BigInteger TokenId { get; set; }
}

[FunctionOutput]
public sealed class CapabilityTokenOutput : IFunctionOutputDTO
{
    [Parameter("tuple", "token", 1)]
    public CapabilityTokenTuple Token { get; set; } = new();
}

[FunctionOutput]
public sealed class CapabilityTokenTuple
{
    [Parameter("address", "wallet", 1)]
    public string Wallet { get; set; } = "";

    [Parameter("bytes32", "tenantId", 2)]
    public byte[] TenantId { get; set; } = [];

    [Parameter("uint8", "permission", 3)]
    public byte Permission { get; set; }

    [Parameter("uint64", "issuedAt", 4)]
    public ulong IssuedAt { get; set; }

    [Parameter("uint64", "consentGrantedAt", 5)]
    public ulong ConsentGrantedAt { get; set; }
}
