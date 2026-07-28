// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title ConfigKeys
/// @notice Shared `IProtocolConfig` key namespace for the core protocol
/// contracts (issue #52). Centralizing the key constants here means all four
/// contracts (and their tests, and the eventual #54 governance module) agree
/// on the exact `bytes32` key for a given parameter without duplicating
/// `keccak256(...)` literals across files.
///
/// Keys that vary per node type or per violation type are computed via the
/// `*For` helper functions rather than enumerated individually, keeping the
/// key space open to new node types / violation categories without a
/// contract change (only a config-write to populate the new key).
library ConfigKeys {
    // --- Shared / cross-contract ---
    bytes32 internal constant TOKEN_ADDRESS = keccak256("token.address");
    bytes32 internal constant GOVERNANCE_TIMELOCK = keccak256("governance.timelock");
    bytes32 internal constant EPOCH_LENGTH = keccak256("epoch.length");
    bytes32 internal constant EPOCH_GENESIS_TIMESTAMP = keccak256("epoch.genesisTimestamp");
    bytes32 internal constant REGISTRY_ADDRESS = keccak256("registry.address");

    // --- Registry (staking) ---
    bytes32 internal constant REGISTRY_UNSTAKE_COOLDOWN = keccak256("registry.unstake.cooldown");
    bytes32 internal constant REGISTRY_SLASHING_CONTROLLER = keccak256("registry.slashingController");
    bytes32 internal constant REGISTRY_VERIFIER_ROLE = keccak256("registry.verifierRole");

    function registryStakeBase(uint8 nodeType) internal pure returns (bytes32) {
        return keccak256(abi.encode("registry.stake.base", nodeType));
    }

    function registryStakePerUnit(uint8 nodeType) internal pure returns (bytes32) {
        return keccak256(abi.encode("registry.stake.perUnit", nodeType));
    }

    // --- BlobDeals ---
    bytes32 internal constant DEALS_PRICE_PER_BYTE_EPOCH = keccak256("deals.price.byteEpoch");
    bytes32 internal constant DEALS_PRICE_EGRESS_PER_GB = keccak256("deals.price.egressGb");
    bytes32 internal constant DEALS_REPLICATION_ROLE = keccak256("deals.replicationRole");
    bytes32 internal constant DEALS_BILLING_ROLE = keccak256("deals.billingRole");
    bytes32 internal constant DEALS_GRACE_DURATION = keccak256("deals.nonpayment.grace");
    bytes32 internal constant DEALS_SOFT_SUSPEND_DURATION = keccak256("deals.nonpayment.softSuspend");
    bytes32 internal constant DEALS_SOFT_DELETE_DURATION = keccak256("deals.nonpayment.softDelete");
    bytes32 internal constant DEALS_HARD_DELETE_DURATION = keccak256("deals.nonpayment.hardDelete");

    // --- RewardDistributor ---
    bytes32 internal constant REWARD_EMISSION_PER_EPOCH = keccak256("reward.emissionPerEpoch");
    bytes32 internal constant REWARD_CAPACITY_SHARE_BPS = keccak256("reward.split.capacityBps");
    bytes32 internal constant REWARD_VERIFIER_SHARE_BPS = keccak256("reward.split.verifierBps");
    bytes32 internal constant REWARD_MULT_MIN = keccak256("reward.mult.min");
    bytes32 internal constant REWARD_MULT_MAX = keccak256("reward.mult.max");
    bytes32 internal constant REWARD_MULT_EPSILON = keccak256("reward.mult.epsilon");
    bytes32 internal constant REWARD_HEARTBEAT_THRESHOLD_BPS = keccak256("reward.heartbeat.thresholdBps");
    bytes32 internal constant REWARD_RESERVE_SWEEP_EPOCHS = keccak256("reward.reserve.sweepEpochs");
    bytes32 internal constant REWARD_FINALITY_WINDOW = keccak256("reward.finalityWindow");
    bytes32 internal constant REWARD_METRIC_SIGNER = keccak256("reward.metric.signer");

    function rewardTypeWeight(uint8 nodeType) internal pure returns (bytes32) {
        return keccak256(abi.encode("reward.typeWeight", nodeType));
    }

    function rewardStagePenaltyBps(uint8 stage) internal pure returns (bytes32) {
        return keccak256(abi.encode("reward.stagePenaltyBps", stage));
    }

    /// @notice Per-node-type, per-factor weight (bps, sums to 10_000 across a
    /// type's active factor table) used by the #53 per-factor scoring path.
    /// `factorIndex` for Storage (tokenomics-mvp.md §6.3): 0=retrieval speed,
    /// 1=uptime, 2=latency/TTFB, 3=I/O throughput, 4=network bandwidth,
    /// 5=available storage, 6=used storage.
    function rewardFactorWeightBps(uint8 nodeType, uint8 factorIndex) internal pure returns (bytes32) {
        return keccak256(abi.encode("reward.factorWeightBps", nodeType, factorIndex));
    }

    /// @notice Reliability-bonus parameters (tokenomics-mvp.md §6.4): a
    /// trailing-30-epoch reliability ratio >= this bps threshold multiplies
    /// gross payout by `REWARD_RELIABILITY_BONUS_BPS` (e.g. 10_500 = 1.05x).
    bytes32 internal constant REWARD_RELIABILITY_BONUS_THRESHOLD_BPS = keccak256("reward.reliabilityBonusThresholdBps");
    bytes32 internal constant REWARD_RELIABILITY_BONUS_BPS = keccak256("reward.reliabilityBonusBps");

    // --- SlashingController ---
    bytes32 internal constant SLASHING_STAGE3_PCT_PER_EPOCH_BPS = keccak256("slashing.stage3.pctPerEpochBps");
    bytes32 internal constant SLASHING_STAGE3_CAP_BPS = keccak256("slashing.stage3.capBps");
    bytes32 internal constant SLASHING_STAGE4_IMMEDIATE_BPS = keccak256("slashing.stage4.immediateBps");
    bytes32 internal constant SLASHING_STAGE4_ONGOING_BPS = keccak256("slashing.stage4.ongoingBps");
    bytes32 internal constant SLASHING_BAN_DURATION = keccak256("slashing.ban.duration");
    bytes32 internal constant SLASHING_RECOVERY_CLEAN_EPOCHS = keccak256("slashing.recovery.cleanEpochs");
    bytes32 internal constant SLASHING_DISPOSITION = keccak256("slashing.disposition"); // 0 = burn, 1 = redistribute
    bytes32 internal constant SLASHING_REDISTRIBUTION_TARGET = keccak256("slashing.redistributionTarget");
    bytes32 internal constant SLASHING_FAULT_SIGNER = keccak256("slashing.fault.signer");

    function slashingViolationPctBps(uint8 violationType) internal pure returns (bytes32) {
        return keccak256(abi.encode("slashing.violationPctBps", violationType));
    }

    // --- FoundationRegistry (issue #57) ---
    bytes32 internal constant FOUNDATION_RESERVE_ADDRESS = keccak256("foundation.reserveAddress");
    bytes32 internal constant ECOSYSTEM_RESERVE_ADDRESS = keccak256("foundation.ecosystemReserveAddress");
    bytes32 internal constant FOUNDATION_OPS_ROLE = keccak256("foundation.opsRole");
    bytes32 internal constant FOUNDATION_REGISTRY_ADDRESS = keccak256("foundation.registryAddress");
    bytes32 internal constant FOUNDATION_REWARD_HOOK = keccak256("foundation.rewardHook");
    bytes32 internal constant REGION_DISTRIBUTION_CAP_BPS = keccak256("foundation.regionDistributionCapBps");

    /// @notice Per-node (or per-class, if a caller keys by a shared class
    /// address) operational cost baseline consumed by the #57 reward-
    /// surplus-routing hook. Owned by finance/ops; this issue only requires
    /// the parameter exist and be read consistently — the exact
    /// methodology is explicitly out of scope.
    function operationalCostBaseline(address node) internal pure returns (bytes32) {
        return keccak256(abi.encode("foundation.operationalCostBaseline", node));
    }

    // --- ComputeTierRegistry (issue #89) ---
    bytes32 internal constant COMPUTE_TIER_MAX_EPOCHS_BETWEEN_VERIFICATIONS =
        keccak256("computeTier.maxEpochsBetweenVerifications");
    bytes32 internal constant COMPUTE_TIER_HARDWARE_TOLERANCE_BPS = keccak256("computeTier.hardwareToleranceBps");

    /// @notice Per-tier (ComputeTier ordinal: 1=Tier3, 2=Tier2, 3=Tier1) minimum thresholds
    /// backing `ComputeTierRegistry.classifyTier`. Governance-tunable per
    /// compute-nodes.md's "illustrative current thresholds" note — changing
    /// tier bars is a governance action, not a contract change.
    function computeTierMinCores(uint8 tier) internal pure returns (bytes32) {
        return keccak256(abi.encode("computeTier.minCores", tier));
    }

    function computeTierMinMips(uint8 tier) internal pure returns (bytes32) {
        return keccak256(abi.encode("computeTier.minMips", tier));
    }

    function computeTierMinRamGB(uint8 tier) internal pure returns (bytes32) {
        return keccak256(abi.encode("computeTier.minRamGB", tier));
    }

    function computeTierMinRamBandwidthGBs(uint8 tier) internal pure returns (bytes32) {
        return keccak256(abi.encode("computeTier.minRamBandwidthGBs", tier));
    }

    function computeTierMinIops(uint8 tier) internal pure returns (bytes32) {
        return keccak256(abi.encode("computeTier.minIops", tier));
    }

    function computeTierMaxRttMs(uint8 tier) internal pure returns (bytes32) {
        return keccak256(abi.encode("computeTier.maxRttMs", tier));
    }

    // --- ComputeNodeManifest (issue #90) ---
    bytes32 internal constant COMPUTE_TIER_REGISTRY_ADDRESS = keccak256("computeNodeManifest.tierRegistryAddress");
}
