# Basemail v1 Smart Contracts

This file defines the v1 onchain control plane on Base.

## Contract Set

Basemail v1 uses the following contracts:

1. `BasemailToken`
2. `NodeRegistry`
3. `MailboxRegistry`
4. `RewardsLedger`
5. `CommitmentRegistry`
6. `PricingPolicy`
7. `SlashingManager`

These contracts should be deployed to Base Sepolia first, then adapted for Base mainnet later.

## 1. BasemailToken

Purpose:

- native utility token, temporarily referred to as `$Basemail`
- staking
- rewards
- mailbox service payments

Example interface:

```solidity
interface IBasemailToken {
    function mint(address to, uint256 amount) external;
    function burn(address from, uint256 amount) external;
    function transfer(address to, uint256 amount) external returns (bool);
    function transferFrom(address from, address to, uint256 amount) external returns (bool);
    function balanceOf(address owner) external view returns (uint256);
}
```

Notes:

- v1 may use controlled minting for incentives
- mainnet issuance policy should be finalized later

## 2. NodeRegistry

Purpose:

- register service nodes
- track stake
- declare capabilities
- manage node lifecycle

Suggested structs:

```solidity
struct NodeCapabilities {
    bool smtpIngress;
    bool mailStorage;
    bool mailIndex;
    bool webGateway;
    uint32 advertisedStorageGb;
    uint32 advertisedBandwidthGbPerDay;
}

struct NodeRecord {
    address operator;
    bytes32 nodeId;
    string metadataUri;
    uint256 stake;
    uint64 registeredAt;
    uint64 lastEpochReported;
    bool active;
    bool jailed;
}
```

Suggested interface:

```solidity
interface INodeRegistry {
    function registerNode(bytes32 nodeId, string calldata metadataUri, NodeCapabilities calldata caps) external;
    function increaseStake(bytes32 nodeId, uint256 amount) external;
    function decreaseStake(bytes32 nodeId, uint256 amount) external;
    function updateCapabilities(bytes32 nodeId, NodeCapabilities calldata caps) external;
    function setNodeStatus(bytes32 nodeId, bool active) external;
    function jailNode(bytes32 nodeId) external;
    function unjailNode(bytes32 nodeId) external;
    function getNode(bytes32 nodeId) external view returns (NodeRecord memory);
}
```

Rules:

- one operator may control multiple nodes
- each node must have a unique `nodeId`
- only staked nodes may participate in reward-bearing roles

## 3. MailboxRegistry

Purpose:

- global mailbox identity
- wallet ownership
- address bindings
- privacy tier and service plan state
- routing commitment anchor

Suggested enums and structs:

```solidity
enum PrivacyTier { Standard, Private }

struct MailboxRecord {
    bytes32 mailboxId;
    address owner;
    PrivacyTier privacyTier;
    uint64 createdAt;
    uint64 lastUpdatedAt;
    bool active;
    bytes32 routingRoot;
    bytes32 policyRoot;
}
```

Suggested interface:

```solidity
interface IMailboxRegistry {
    function createMailbox(bytes32 mailboxId, address owner, PrivacyTier tier) external;
    function bindAddress(bytes32 mailboxId, string calldata emailAddress) external;
    function unbindAddress(string calldata emailAddress) external;
    function transferMailbox(bytes32 mailboxId, address newOwner) external;
    function updatePrivacyTier(bytes32 mailboxId, PrivacyTier tier) external;
    function updateRoutingRoot(bytes32 mailboxId, bytes32 routingRoot) external;
    function getMailbox(bytes32 mailboxId) external view returns (MailboxRecord memory);
    function resolveAddress(string calldata emailAddress) external view returns (bytes32 mailboxId);
}
```

Rules:

- `MailboxId` is the stable global identity
- multiple addresses may map to one mailbox
- one mailbox may exist across multiple domains

## 4. RewardsLedger

Purpose:

- account for reward accrual by epoch
- expose claimable balances
- keep rewards separate from realtime protocol traffic

Suggested structs:

```solidity
struct EpochReward {
    uint64 epochId;
    uint256 totalPool;
    bytes32 scoreRoot;
    bool finalized;
}
```

Suggested interface:

```solidity
interface IRewardsLedger {
    function publishEpoch(uint64 epochId, uint256 totalPool, bytes32 scoreRoot) external;
    function assignReward(bytes32 nodeId, uint64 epochId, uint256 amount) external;
    function claim(bytes32 nodeId) external;
    function claimable(bytes32 nodeId) external view returns (uint256);
}
```

v1 note:

- score computation is offchain
- onchain contract only anchors results and pays claims

## 5. CommitmentRegistry

Purpose:

- anchor mailbox routing roots
- anchor replica commitments
- anchor uptime score roots

Suggested interface:

```solidity
interface ICommitmentRegistry {
    function commitMailboxRouting(bytes32 mailboxId, bytes32 routingRoot, uint64 epochId) external;
    function commitReplicaSet(bytes32 mailboxId, bytes32 messageId, bytes32 replicaRoot, uint64 epochId) external;
    function commitUptimeScores(uint64 epochId, bytes32 scoreRoot) external;
}
```

## 6. PricingPolicy

Purpose:

- generic pricing across service classes
- prepare for future services beyond mail

Suggested service classes:

- `mail_storage`
- `mail_bandwidth`
- `mail_privacy`
- `mail_index`
- future arbitrary classes

Suggested interface:

```solidity
struct ServicePrice {
    bytes32 serviceClass;
    uint256 basePrice;
    uint256 unitPrice;
    bool enabled;
}

interface IPricingPolicy {
    function setPrice(bytes32 serviceClass, uint256 basePrice, uint256 unitPrice, bool enabled) external;
    function getPrice(bytes32 serviceClass) external view returns (ServicePrice memory);
}
```

## 7. SlashingManager

Purpose:

- slash stake
- jail nodes
- enforce penalties

Suggested interface:

```solidity
interface ISlashingManager {
    function slash(bytes32 nodeId, uint256 amount, string calldata reason) external;
    function jail(bytes32 nodeId, string calldata reason) external;
}
```

Slash triggers:

- failed storage challenge
- repeated downtime
- false capability claims
- invalid signed protocol behavior
- replica under-delivery

## Privacy And Telemetry Policy

The chain should store only policy-relevant state, not behavioral telemetry.

Onchain, v1 should store:

- mailbox privacy tier
- policy version root
- consent-related commitments if needed later

Offchain systems may process telemetry only for `Standard` mailboxes. `Private` mailboxes should be excluded except for minimal operational metrics.

## What Must Stay Offchain

Never put these directly on Base in v1:

- raw message bodies
- attachments
- mailbox indexes
- clickstream records
- impression logs
- hover-time logs
- search indexes

Base is the control plane, not the data plane.
