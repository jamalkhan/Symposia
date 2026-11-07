// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {DeployRewards} from "../script/DeployRewards.s.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";

/// @notice Runs the rewards deploy script and drives one epoch end to end: a node
/// stakes dev-minted SYM, reports metrics, and is paid scheduled emission at seal.
contract DeployRewardsTest is DeployRewards, Test {
    Deployment internal d;

    uint256 internal deployerPk = 0xD3910;
    address internal deployer;
    address internal node = makeAddr("node");

    function setUp() public {
        deployer = vm.addr(deployerPk);
        address[] memory committee = new address[](1);
        committee[0] = deployer;
        Roles memory roles = Roles({
            timelock: makeAddr("timelock"),
            metricSigner: deployer,
            faultSigner: deployer,
            verifier: deployer,
            challenger: deployer,
            committee: committee,
            committeeThreshold: 1,
            devMintAllowance: 1_000_000e18
        });
        vm.startBroadcast(deployer);
        d = deploy(deployer, roles);
        vm.stopBroadcast();
    }

    /// #181: production timing is the default; LocalDev's 1-hour profile deploys and wires through.
    function test_DefaultTimingIsProduction() public view {
        assertEq(d.epochs.epochLength(), 1 days);
        assertEq(d.config.getUint(ConfigKeys.TELEMETRY_CHECKPOINTS_PER_EPOCH), 24);
        assertEq(d.config.getUint(ConfigKeys.TELEMETRY_COMMIT_WINDOW), 30 minutes);
        assertEq(d.config.getUint(ConfigKeys.REWARD_RECORDS_MAX_WAIT), 24 hours);
    }

    function test_LocalDevTimingDeploys() public {
        timing = Timing(1 hours, 5 minutes, 4, 300, 5 minutes, 5 minutes, 1 hours);
        address[] memory committee = new address[](1);
        committee[0] = node;
        vm.startBroadcast(deployer);
        Deployment memory dev = deploy(deployer, Roles(makeAddr("timelock"), deployer, deployer, deployer, deployer, committee, 1, 0));
        vm.stopBroadcast();

        assertEq(dev.epochs.epochLength(), 1 hours);
        assertEq(dev.epochs.finalityWindow(), 5 minutes);
        assertEq(dev.config.getUint(ConfigKeys.TELEMETRY_CHECKPOINTS_PER_EPOCH), 4);
        assertEq(dev.config.getUint(ConfigKeys.TELEMETRY_COMMIT_WINDOW), 5 minutes);
        assertEq(dev.config.getUint(ConfigKeys.TELEMETRY_SELF_CHECKPOINT_GRACE), 5 minutes);
        assertEq(dev.config.getUint(ConfigKeys.REWARD_RECORDS_MAX_WAIT), 1 hours);
        (address[] memory members, uint256 threshold) = dev.telemetry.committee();
        assertEq(members.length, 1);
        assertEq(members[0], node);
        assertEq(threshold, 1);
    }

    function test_ValidateTiming_RejectsWindowsWithoutWholeHeartbeats() public {
        // 1h / (7 windows * 300s) isn't whole.
        vm.expectRevert(abi.encodeWithSelector(InvalidTiming.selector, "telemetry windows must each hold a whole number of heartbeats (>= 1)"));
        this.validateTiming(Timing(1 hours, 5 minutes, 7, 300, 5 minutes, 5 minutes, 1 hours));
    }

    function test_ValidateTiming_RejectsWindowsShorterThanAHeartbeat() public {
        // 24 windows in an hour are 150s each: a 300s heartbeat can't land in every one.
        vm.expectRevert(abi.encodeWithSelector(InvalidTiming.selector, "telemetry windows must each hold a whole number of heartbeats (>= 1)"));
        this.validateTiming(Timing(1 hours, 5 minutes, 24, 300, 5 minutes, 5 minutes, 1 hours));
    }

    function test_ValidateTiming_RejectsGracePastTheTelemetryDeadline() public {
        vm.expectRevert(abi.encodeWithSelector(InvalidTiming.selector, "self-checkpoint grace must end before finality + commit window"));
        this.validateTiming(Timing(1 hours, 5 minutes, 4, 300, 5 minutes, 10 minutes, 1 hours));
    }

    function test_WiresEveryContractIntoConfig() public view {
        assertEq(d.config.getAddress(ConfigKeys.TOKEN_ADDRESS), address(d.token));
        assertEq(d.config.getAddress(ConfigKeys.REWARD_EMISSION_SOURCE), address(d.emission));
        assertEq(d.config.getAddress(ConfigKeys.REWARD_RELIABILITY_REGISTRY), address(d.reliability));
        assertEq(d.config.getAddress(ConfigKeys.RELIABILITY_FINALIZER), address(d.rewards));
        assertEq(d.config.getAddress(ConfigKeys.REGISTRY_ADDRESS), address(d.registry));
        assertEq(address(d.emission.token()), address(d.token));
        assertEq(d.emission.rewardDistributor(), address(d.rewards));
        assertEq(d.config.getAddress(ConfigKeys.EPOCH_CONTROLLER), address(d.epochs));
        assertEq(d.config.getAddress(ConfigKeys.REWARD_SCORING_WEIGHTS), address(d.weights));
        assertEq(d.config.getAddress(ConfigKeys.REWARD_TELEMETRY_REGISTRY), address(d.telemetry));
        assertEq(address(d.emission.epochClock()), address(d.epochs));
        assertEq(d.epochs.finalityWindow(), 15 minutes);
        assertEq(d.config.owner(), deployer);
    }

    /// #163: the chain is configured from protocol/economics.json, and ScoringWeights' seeded
    /// tables match it, so that file is the single source the NodeHost catalog is checked against.
    function test_NodeServiceEconomicsMatchTheManifest() public view {
        string memory economics = vm.readFile(ECONOMICS_PATH);
        assertEq(d.config.getUint(ConfigKeys.REWARD_CAPACITY_SHARE_BPS), vm.parseJsonUint(economics, ".emission.capacityShareBps"));
        assertEq(d.config.getUint(ConfigKeys.REWARD_VERIFIER_SHARE_BPS), vm.parseJsonUint(economics, ".emission.verifierShareBps"));
        string[3] memory services = ["storage", "verifier", "blobGateway"];
        for (uint256 i; i < services.length; i++) {
            string memory k = string.concat(".services.", services[i]);
            uint8 id = uint8(vm.parseJsonUint(economics, string.concat(k, ".id")));
            assertEq(d.config.getUint(ConfigKeys.registryStakeBase(id)), vm.parseJsonUint(economics, string.concat(k, ".stakeBase")) * 1e18);
            assertEq(d.config.getUint(ConfigKeys.rewardTypeWeight(id)), vm.parseJsonUint(economics, string.concat(k, ".typeWeightBps")));
            uint256[] memory expected = vm.parseJsonUintArray(economics, string.concat(k, ".factorsBps"));
            (uint16[] memory onChain,) = d.weights.weightsFor(id, 0);
            assertEq(onChain.length, expected.length, services[i]);
            for (uint256 f; f < expected.length; f++) assertEq(onChain[f], expected[f], services[i]);
        }
        assertEq(d.config.getUint(ConfigKeys.rewardTypeWeight(6)), 0, "Email IP Address never earns");
        (, bool[] memory lower) = d.weights.weightsFor(8, 0);
        assertTrue(lower[2] && !lower[0] && !lower[1] && !lower[3], "only Blob Gateway p95 latency is lower-is-better");
    }

    function test_EmissionIsTheOnlyLargeMinter() public view {
        assertEq(d.token.totalSupply(), 0);
        assertEq(d.token.MAX_SUPPLY(), 10_000_000_000e18);
    }

    function test_NodeEarnsScheduledEmission() public {
        vm.prank(deployer);
        d.token.mint(node, 1_000e18);
        vm.startPrank(node);
        d.token.approve(address(d.registry), type(uint256).max);
        d.registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", d.registry.minStakeFor(INodeRegistry.NodeType.Storage, 1));
        vm.stopPrank();
        vm.prank(deployer);
        d.registry.recordVerification(node, true, 0);

        d.rewards.submitMetricReport(node, 0, 100, 10_000, _signMetric(node, 0, 100, 10_000));
        // No committee records for epoch 0, so settlement falls back to the signed report after the wait (#76).
        vm.warp(d.telemetry.telemetryDeadline(0) + 24 hours + 1);
        uint256 before = d.token.balanceOf(node);
        address[] memory nodes = new address[](1);
        nodes[0] = node;
        d.rewards.sealEpoch(0, nodes);

        // 100% heartbeat, so reliability stays 1.0x and the lone node takes the whole Storage pool:
        // 90% of the 92% capacity share (#160: the other 10% is the Blob Gateway pool).
        uint256 scheduled = d.emission.primaryEmission(0);
        assertEq(d.emission.emittedAmount(0), scheduled);
        assertApproxEqAbs(d.token.balanceOf(node) - before, (((scheduled * 9_200) / 10_000) * 9_000) / 10_000, 2);
    }

    function _signMetric(address who, uint256 epoch, uint256 score, uint256 heartbeatBps) internal view returns (bytes memory) {
        bytes32 digest = keccak256(
            abi.encodePacked("\x19Ethereum Signed Message:\n32", keccak256(abi.encode(address(d.rewards), who, epoch, score, heartbeatBps)))
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(deployerPk, digest);
        return abi.encodePacked(r, s, v);
    }
}
