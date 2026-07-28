// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {MockProtocolConfig} from "../../src/config/MockProtocolConfig.sol";
import {ConfigKeys} from "../../src/config/ConfigKeys.sol";
import {StakingNodeRegistry} from "../../src/StakingNodeRegistry.sol";
import {BlobDeals} from "../../src/BlobDeals.sol";
import {RewardDistributor} from "../../src/RewardDistributor.sol";
import {SlashingController} from "../../src/SlashingController.sol";
import {MockERC20} from "../mocks/MockERC20.sol";

/// @notice Shared setup for the issue #52 core protocol contract test
/// suite: deploys a MockERC20 stake/reward token, a MockProtocolConfig
/// preloaded with MVP-ish values from `tokenomics-mvp.md` /
/// `node-runner-incentives-and-penalties.md`, and all four core contracts
/// behind ERC1967 (UUPS) proxies wired to that config.
abstract contract CoreProtocolTestBase is Test {
    MockERC20 internal token;
    MockProtocolConfig internal cfg;

    StakingNodeRegistry internal registry;
    BlobDeals internal deals;
    RewardDistributor internal rewards;
    SlashingController internal slashing;

    address internal timelockAddr = makeAddr("timelock");
    address internal configOwner = makeAddr("configOwner");
    address internal deployer = makeAddr("deployer");
    address internal verifierRole = makeAddr("verifierRole");
    address internal replicationRole = makeAddr("replicationRole");
    address internal billingRole = makeAddr("billingRole");
    address internal redistributionTarget = makeAddr("redistributionTarget");

    uint256 internal metricSignerPk = 0xA11CE;
    address internal metricSigner;
    uint256 internal faultSignerPk = 0xFA017;
    address internal faultSigner;

    uint256 internal constant EPOCH_LENGTH = 1 days;
    uint256 internal genesisTimestamp;

    function _deployCoreProtocol() internal {
        metricSigner = vm.addr(metricSignerPk);
        faultSigner = vm.addr(faultSignerPk);

        token = new MockERC20();
        cfg = new MockProtocolConfig(configOwner);

        genesisTimestamp = block.timestamp;

        registry = StakingNodeRegistry(
            _deployProxy(address(new StakingNodeRegistry()), abi.encodeCall(StakingNodeRegistry.initialize, (cfg)))
        );
        deals = BlobDeals(_deployProxy(address(new BlobDeals()), abi.encodeCall(BlobDeals.initialize, (cfg))));
        rewards = RewardDistributor(
            _deployProxy(address(new RewardDistributor()), abi.encodeCall(RewardDistributor.initialize, (cfg)))
        );
        slashing = SlashingController(
            _deployProxy(address(new SlashingController()), abi.encodeCall(SlashingController.initialize, (cfg)))
        );

        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.GOVERNANCE_TIMELOCK, timelockAddr);
        cfg.setAddress(ConfigKeys.TOKEN_ADDRESS, address(token));
        cfg.setAddress(ConfigKeys.REGISTRY_ADDRESS, address(registry));
        cfg.setAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER, address(slashing));
        cfg.setAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE, verifierRole);
        cfg.setAddress(ConfigKeys.DEALS_REPLICATION_ROLE, replicationRole);
        cfg.setAddress(ConfigKeys.DEALS_BILLING_ROLE, billingRole);
        cfg.setUint(ConfigKeys.EPOCH_LENGTH, EPOCH_LENGTH);
        cfg.setUint(ConfigKeys.EPOCH_GENESIS_TIMESTAMP, genesisTimestamp);
        cfg.setUint(ConfigKeys.REGISTRY_UNSTAKE_COOLDOWN, 21 days);

        // Node type 0 = Storage. base 100e18 + 1e18/TB(unit).
        cfg.setUint(ConfigKeys.registryStakeBase(0), 100e18);
        cfg.setUint(ConfigKeys.registryStakePerUnit(0), 1e18);

        // Deals pricing.
        cfg.setUint(ConfigKeys.DEALS_PRICE_PER_BYTE_EPOCH, 1);
        cfg.setUint(ConfigKeys.DEALS_PRICE_EGRESS_PER_GB, 1e15);
        cfg.setUint(ConfigKeys.DEALS_GRACE_DURATION, 7 days);
        cfg.setUint(ConfigKeys.DEALS_SOFT_SUSPEND_DURATION, 7 days);
        cfg.setUint(ConfigKeys.DEALS_SOFT_DELETE_DURATION, 14 days);
        cfg.setUint(ConfigKeys.DEALS_HARD_DELETE_DURATION, 30 days);

        // Reward parameters.
        cfg.setUint(ConfigKeys.REWARD_EMISSION_PER_EPOCH, 1_000_000e18);
        cfg.setUint(ConfigKeys.REWARD_CAPACITY_SHARE_BPS, 9_200);
        cfg.setUint(ConfigKeys.REWARD_VERIFIER_SHARE_BPS, 800);
        cfg.setUint(ConfigKeys.rewardTypeWeight(0), 10_000); // Storage gets full capacity pool in these tests
        cfg.setUint(ConfigKeys.REWARD_MULT_MIN, 0);
        cfg.setUint(ConfigKeys.REWARD_MULT_MAX, 10_000);
        cfg.setUint(ConfigKeys.REWARD_HEARTBEAT_THRESHOLD_BPS, 9_000); // 90%
        cfg.setUint(ConfigKeys.REWARD_RESERVE_SWEEP_EPOCHS, 2);
        cfg.setUint(ConfigKeys.REWARD_FINALITY_WINDOW, 1 hours);
        cfg.setAddress(ConfigKeys.REWARD_METRIC_SIGNER, metricSigner);
        // Storage factor weights (tokenomics-mvp.md §6.3), node type 0.
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 0), 3_000); // retrieval speed
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 1), 2_000); // uptime
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 2), 1_500); // latency/TTFB
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 3), 1_000); // I/O throughput
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 4), 1_000); // network bandwidth
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 5), 800); // available storage
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 6), 700); // used storage
        cfg.setUint(ConfigKeys.rewardStagePenaltyBps(1), 7_000); // Stage 1 -> 70%
        cfg.setUint(ConfigKeys.rewardStagePenaltyBps(2), 4_000); // Stage 2 -> 40%
        cfg.setUint(ConfigKeys.rewardStagePenaltyBps(3), 0);
        cfg.setUint(ConfigKeys.rewardStagePenaltyBps(4), 0);

        // Slashing parameters (node-runner-incentives-and-penalties.md worked examples).
        cfg.setUint(ConfigKeys.SLASHING_STAGE3_PCT_PER_EPOCH_BPS, 500); // 5%
        cfg.setUint(ConfigKeys.SLASHING_STAGE3_CAP_BPS, 2_500); // 25%
        cfg.setUint(ConfigKeys.SLASHING_STAGE4_IMMEDIATE_BPS, 2_000); // 20%
        cfg.setUint(ConfigKeys.SLASHING_STAGE4_ONGOING_BPS, 500); // 5%
        cfg.setUint(ConfigKeys.SLASHING_BAN_DURATION, 90 days);
        cfg.setUint(ConfigKeys.SLASHING_RECOVERY_CLEAN_EPOCHS, 3);
        cfg.setUint(ConfigKeys.SLASHING_DISPOSITION, 0); // burn
        cfg.setAddress(ConfigKeys.SLASHING_REDISTRIBUTION_TARGET, redistributionTarget);
        cfg.setAddress(ConfigKeys.SLASHING_FAULT_SIGNER, faultSigner);

        // Non-hardware violation percentages.
        cfg.setUint(ConfigKeys.slashingViolationPctBps(0), 1_500); // Overcommitment
        cfg.setUint(ConfigKeys.slashingViolationPctBps(1), 3_000); // RegionVerificationFraud
        cfg.setUint(ConfigKeys.slashingViolationPctBps(2), 3_000); // RepeatedVerificationFailure
        vm.stopPrank();
    }

    function _deployProxy(address implementation, bytes memory initData) internal returns (address) {
        ERC1967Proxy proxy = new ERC1967Proxy(implementation, initData);
        return address(proxy);
    }

    function _fund(address who, uint256 amount) internal {
        token.mint(who, amount);
        vm.prank(who);
        token.approve(address(registry), type(uint256).max);
    }

    function _signMetric(address node, uint256 epoch, uint256 score, uint256 heartbeatBps)
        internal
        view
        returns (bytes memory)
    {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(rewards), node, epoch, score, heartbeatBps))
            )
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(metricSignerPk, digest);
        return abi.encodePacked(r, s, v);
    }

    function _signFactorMetrics(address node, uint256 epoch, uint256[7] memory values, uint256 heartbeatBps)
        internal
        view
        returns (bytes memory)
    {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(rewards), node, epoch, values, heartbeatBps))
            )
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(metricSignerPk, digest);
        return abi.encodePacked(r, s, v);
    }

    function _signFault(address node, uint8 stage, uint256 epoch, bool auditPassed) internal view returns (bytes memory) {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(slashing), node, stage, epoch, auditPassed))
            )
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(faultSignerPk, digest);
        return abi.encodePacked(r, s, v);
    }
}
