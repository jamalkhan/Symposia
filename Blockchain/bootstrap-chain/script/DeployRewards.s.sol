// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Script, console} from "forge-std/Script.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {MockProtocolConfig} from "../src/config/MockProtocolConfig.sol";
import {IProtocolConfig} from "../src/config/IProtocolConfig.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {SYMToken} from "../src/SYMToken.sol";
import {StakingNodeRegistry} from "../src/StakingNodeRegistry.sol";
import {SlashingController} from "../src/SlashingController.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {ReliabilityRegistry} from "../src/ReliabilityRegistry.sol";
import {TelemetryCheckpointRegistry} from "../src/TelemetryCheckpointRegistry.sol";
import {NetworkRewardsEmission} from "../src/NetworkRewardsEmission.sol";
import {EpochController} from "../src/EpochController.sol";
import {ScoringWeights} from "../src/ScoringWeights.sol";
import {IEpochClock} from "../src/interfaces/IEpochClock.sol";
import {IFeeTreasury} from "../src/interfaces/IFeeTreasury.sol";
import {ISYMMinter} from "../src/interfaces/ISYMMinter.sol";

/// @notice Deploys and wires the node-rewards stack (issues #70, #73, #75, #79, #80):
/// epoch controller (#72), SYM token, staking registry, slashing, reward distributor, reliability, telemetry
/// checkpoints and the emission schedule. Separate from Deploy.s.sol, which stays the
/// Phase 0 set the Gateway and NodeHost LocalDev depend on.
///
/// Dev/test networks only as written: config is the owner-settable MockProtocolConfig
/// (there is no governed config contract yet), and every role defaults to the deployer.
/// On a real network GOVERNANCE_TIMELOCK must be the Tier 3 multi-sig: it becomes the
/// SYM upgrade authority, which cannot be changed without an upgrade.
///
/// Env (all optional): GOVERNANCE_TIMELOCK, METRIC_SIGNER, FAULT_SIGNER, VERIFIER_ROLE,
/// RELIABILITY_CHALLENGER, TELEMETRY_COMMITTEE (comma-separated addresses),
/// TELEMETRY_THRESHOLD, DEV_MINT_ALLOWANCE (wei the deployer may mint for staking;
/// default 10M SYM on chain 31337, 0 elsewhere), DEPLOYMENT_OUT (addresses JSON path).
contract DeployRewards is Script {
    struct Roles {
        address timelock;
        address metricSigner;
        address faultSigner;
        address verifier;
        address challenger;
        address[] committee;
        uint256 committeeThreshold;
        uint256 devMintAllowance;
    }

    struct Deployment {
        MockProtocolConfig config;
        EpochController epochs;
        ScoringWeights weights;
        SYMToken token;
        StakingNodeRegistry registry;
        SlashingController slashing;
        RewardDistributor rewards;
        ReliabilityRegistry reliability;
        TelemetryCheckpointRegistry telemetry;
        NetworkRewardsEmission emission;
    }

    error PredictedAddressMismatch(address predicted, address actual);

    function run() external returns (Deployment memory d) {
        vm.startBroadcast();
        (, address deployer,) = vm.readCallers();
        Roles memory roles = _rolesFromEnv(deployer);
        d = deploy(deployer, roles);
        vm.stopBroadcast();

        _log(d);
        _writeJson(d, vm.envOr("DEPLOYMENT_OUT", string.concat("deployments/rewards-", vm.toString(block.chainid), ".json")));
    }

    /// @notice Deploys and wires everything. Must run while `deployer` is broadcasting,
    /// since the emission contract and SYM token reference each other and the token
    /// address is predicted from the deployer's nonce.
    function deploy(address deployer, Roles memory roles) public returns (Deployment memory d) {
        d.config = new MockProtocolConfig(deployer);
        IProtocolConfig cfg = d.config;
        // 24h epochs (NetworkRewardsEmission assumes 365/year) with the spec's 15-minute finality window.
        d.epochs = new EpochController(cfg, uint64(block.timestamp), 1 days, 15 minutes);

        d.registry = StakingNodeRegistry(_proxy(address(new StakingNodeRegistry()), abi.encodeCall(StakingNodeRegistry.initialize, (cfg))));
        d.slashing = SlashingController(_proxy(address(new SlashingController()), abi.encodeCall(SlashingController.initialize, (cfg))));
        d.weights = ScoringWeights(_proxy(address(new ScoringWeights()), abi.encodeCall(ScoringWeights.initialize, (cfg))));
        d.rewards = RewardDistributor(_proxy(address(new RewardDistributor()), abi.encodeCall(RewardDistributor.initialize, (cfg))));
        d.reliability = ReliabilityRegistry(_proxy(address(new ReliabilityRegistry()), abi.encodeCall(ReliabilityRegistry.initialize, (cfg))));
        d.telemetry = TelemetryCheckpointRegistry(
            _proxy(
                address(new TelemetryCheckpointRegistry()),
                abi.encodeCall(TelemetryCheckpointRegistry.initialize, (cfg, roles.committee, roles.committeeThreshold))
            )
        );

        // Token <-> emission cycle: the token's minter list is fixed at initialize, and the
        // emission contract takes the token as an immutable. Deploy the token implementation,
        // then the emission contract pointing at the token proxy's predicted address.
        address tokenImpl = address(new SYMToken());
        address predictedToken = vm.computeCreateAddress(deployer, vm.getNonce(deployer) + 1);
        d.emission = new NetworkRewardsEmission(
            ISYMMinter(predictedToken), address(d.rewards), IEpochClock(address(d.epochs)), IFeeTreasury(address(0)), roles.timelock
        );
        d.token = SYMToken(_proxy(tokenImpl, _tokenInit(roles, address(d.emission), deployer)));
        if (address(d.token) != predictedToken) revert PredictedAddressMismatch(predictedToken, address(d.token));

        _configure(d, roles);
    }

    function _tokenInit(Roles memory roles, address emission, address deployer) internal pure returns (bytes memory) {
        bool devMint = roles.devMintAllowance != 0;
        address[] memory minters = new address[](devMint ? 2 : 1);
        uint256[] memory allowances = new uint256[](minters.length);
        (minters[0], allowances[0]) = (emission, 4_000_000_000e18); // NetworkRewardsEmission.BUCKET_TOTAL
        if (devMint) (minters[1], allowances[1]) = (deployer, roles.devMintAllowance);
        return abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", roles.timelock, minters, allowances));
    }

    /// @dev Values follow tokenomics-mvp.md and node-runner-incentives-and-penalties.md,
    /// matching the test fixtures (CoreProtocolTestBase, ReliabilityRegistry.t.sol).
    function _configure(Deployment memory d, Roles memory roles) internal {
        MockProtocolConfig cfg = d.config;

        cfg.setAddress(ConfigKeys.GOVERNANCE_TIMELOCK, roles.timelock);
        cfg.setAddress(ConfigKeys.TOKEN_ADDRESS, address(d.token));
        cfg.setAddress(ConfigKeys.REGISTRY_ADDRESS, address(d.registry));
        cfg.setAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER, address(d.slashing));
        cfg.setAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE, roles.verifier);
        cfg.setAddress(ConfigKeys.EPOCH_CONTROLLER, address(d.epochs));
        cfg.setUint(ConfigKeys.REGISTRY_UNSTAKE_COOLDOWN, 21 days);
        cfg.setUint(ConfigKeys.registryStakeBase(0), 100e18); // Storage: 100 SYM + 1 SYM/TB
        cfg.setUint(ConfigKeys.registryStakePerUnit(0), 1e18);
        cfg.setUint(ConfigKeys.registryStakeBase(5), 500e18); // Verifier: 5x the Storage base (#81)

        // Rewards (#73, #80).
        cfg.setAddress(ConfigKeys.REWARD_EMISSION_SOURCE, address(d.emission));
        cfg.setAddress(ConfigKeys.REWARD_RELIABILITY_REGISTRY, address(d.reliability));
        // #76: score storage nodes from finalized telemetry; fall back to signed reports after 24h.
        cfg.setAddress(ConfigKeys.REWARD_SCORING_WEIGHTS, address(d.weights));
        cfg.setAddress(ConfigKeys.REWARD_TELEMETRY_REGISTRY, address(d.telemetry));
        cfg.setUint(ConfigKeys.REWARD_RECORDS_MAX_WAIT, 24 hours);
        cfg.setAddress(ConfigKeys.REWARD_METRIC_SIGNER, roles.metricSigner);
        cfg.setUint(ConfigKeys.REWARD_CAPACITY_SHARE_BPS, 9_200);
        cfg.setUint(ConfigKeys.REWARD_VERIFIER_SHARE_BPS, 800);
        cfg.setUint(ConfigKeys.rewardTypeWeight(0), 10_000); // Storage is the only paid type so far
        cfg.setUint(ConfigKeys.REWARD_MULT_MIN, 0);
        cfg.setUint(ConfigKeys.REWARD_MULT_MAX, 10_000);
        cfg.setUint(ConfigKeys.REWARD_HEARTBEAT_THRESHOLD_BPS, 9_000);
        cfg.setUint(ConfigKeys.REWARD_RESERVE_SWEEP_EPOCHS, 2);
        cfg.setUint(ConfigKeys.REWARD_RELIABILITY_BONUS_THRESHOLD_BPS, 9_800);
        cfg.setUint(ConfigKeys.REWARD_RELIABILITY_BONUS_BPS, 10_500);
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 0), 3_000); // retrieval speed
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 1), 2_000); // uptime
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 2), 1_500); // latency/TTFB
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 3), 1_000); // I/O throughput
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 4), 1_000); // network bandwidth
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 5), 800); // available storage
        cfg.setUint(ConfigKeys.rewardFactorWeightBps(0, 6), 700); // used storage
        cfg.setUint(ConfigKeys.rewardStagePenaltyBps(1), 7_000);
        cfg.setUint(ConfigKeys.rewardStagePenaltyBps(2), 4_000);

        // Reliability (#79). The distributor finalizes each sealed epoch.
        cfg.setAddress(ConfigKeys.RELIABILITY_CHALLENGER, roles.challenger);
        cfg.setAddress(ConfigKeys.RELIABILITY_FINALIZER, address(d.rewards));
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_POR_BPS, 200);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_PROBE_BPS, 100);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_TIMEOUT_BPS, 50);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_HEARTBEAT_BPS_PER_PCT, 20);
        cfg.setUint(ConfigKeys.RELIABILITY_RECOVERY_BPS, 50);
        cfg.setUint(ConfigKeys.RELIABILITY_BONUS_WINDOW, 30);

        // Telemetry checkpoints (#75).
        cfg.setUint(ConfigKeys.TELEMETRY_CHECKPOINTS_PER_EPOCH, 24);
        cfg.setUint(ConfigKeys.TELEMETRY_SELF_CHECKPOINT_GRACE, 20 minutes);
        cfg.setUint(ConfigKeys.TELEMETRY_COMMIT_WINDOW, 30 minutes); // checkpoints until finality + 30 min (#158)
        cfg.setUint(ConfigKeys.TELEMETRY_RECORDS_PER_TX, 200);
        cfg.setUint(ConfigKeys.TELEMETRY_DIVERGENCE_UPTIME_BPS, 1_000);
        cfg.setUint(ConfigKeys.TELEMETRY_DIVERGENCE_LATENCY_BPS, 5_000);

        // Slashing.
        cfg.setAddress(ConfigKeys.SLASHING_FAULT_SIGNER, roles.faultSigner);
        cfg.setAddress(ConfigKeys.SLASHING_REDISTRIBUTION_TARGET, roles.timelock);
        cfg.setUint(ConfigKeys.SLASHING_STAGE3_PCT_PER_EPOCH_BPS, 500);
        cfg.setUint(ConfigKeys.SLASHING_STAGE3_CAP_BPS, 2_500);
        cfg.setUint(ConfigKeys.SLASHING_STAGE4_IMMEDIATE_BPS, 2_000);
        cfg.setUint(ConfigKeys.SLASHING_STAGE4_ONGOING_BPS, 500);
        cfg.setUint(ConfigKeys.SLASHING_BAN_DURATION, 90 days);
        cfg.setUint(ConfigKeys.SLASHING_RECOVERY_CLEAN_EPOCHS, 3);
        cfg.setUint(ConfigKeys.SLASHING_DISPOSITION, 0); // burn
        cfg.setUint(ConfigKeys.slashingViolationPctBps(0), 1_500);
        cfg.setUint(ConfigKeys.slashingViolationPctBps(1), 3_000);
        cfg.setUint(ConfigKeys.slashingViolationPctBps(2), 3_000);
    }

    function _rolesFromEnv(address deployer) internal view returns (Roles memory r) {
        r.timelock = vm.envOr("GOVERNANCE_TIMELOCK", deployer);
        r.metricSigner = vm.envOr("METRIC_SIGNER", deployer);
        r.faultSigner = vm.envOr("FAULT_SIGNER", deployer);
        r.verifier = vm.envOr("VERIFIER_ROLE", deployer);
        r.challenger = vm.envOr("RELIABILITY_CHALLENGER", deployer);
        address[] memory soloCommittee = new address[](1);
        soloCommittee[0] = deployer;
        r.committee = vm.envOr("TELEMETRY_COMMITTEE", ",", soloCommittee);
        r.committeeThreshold = vm.envOr("TELEMETRY_THRESHOLD", uint256(1));
        r.devMintAllowance = vm.envOr("DEV_MINT_ALLOWANCE", block.chainid == 31337 ? 10_000_000e18 : uint256(0));
    }

    function _proxy(address implementation, bytes memory initData) internal returns (address) {
        return address(new ERC1967Proxy(implementation, initData));
    }

    function _log(Deployment memory d) internal pure {
        console.log("ProtocolConfig:", address(d.config));
        console.log("EpochController:", address(d.epochs));
        console.log("ScoringWeights:", address(d.weights));
        console.log("SYMToken:", address(d.token));
        console.log("StakingNodeRegistry:", address(d.registry));
        console.log("SlashingController:", address(d.slashing));
        console.log("RewardDistributor:", address(d.rewards));
        console.log("ReliabilityRegistry:", address(d.reliability));
        console.log("TelemetryCheckpointRegistry:", address(d.telemetry));
        console.log("NetworkRewardsEmission:", address(d.emission));
    }

    function _writeJson(Deployment memory d, string memory path) internal {
        string memory k = "rewards";
        vm.serializeUint(k, "chainId", block.chainid);
        vm.serializeAddress(k, "ProtocolConfig", address(d.config));
        vm.serializeAddress(k, "EpochController", address(d.epochs));
        vm.serializeAddress(k, "ScoringWeights", address(d.weights));
        vm.serializeAddress(k, "SYMToken", address(d.token));
        vm.serializeAddress(k, "StakingNodeRegistry", address(d.registry));
        vm.serializeAddress(k, "SlashingController", address(d.slashing));
        vm.serializeAddress(k, "RewardDistributor", address(d.rewards));
        vm.serializeAddress(k, "ReliabilityRegistry", address(d.reliability));
        vm.serializeAddress(k, "TelemetryCheckpointRegistry", address(d.telemetry));
        vm.writeJson(vm.serializeAddress(k, "NetworkRewardsEmission", address(d.emission)), path);
    }
}
