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

    /// #81/#159/#160: Node Service economics the NodeHost catalog mirrors.
    function test_NodeServiceEconomics() public view {
        assertEq(d.config.getUint(ConfigKeys.rewardTypeWeight(0)), 9_000, "Storage pool");
        assertEq(d.config.getUint(ConfigKeys.rewardTypeWeight(8)), 1_000, "Blob Gateway pool");
        assertEq(d.config.getUint(ConfigKeys.REWARD_CAPACITY_SHARE_BPS), 9_200);
        assertEq(d.config.getUint(ConfigKeys.REWARD_VERIFIER_SHARE_BPS), 800);
        assertEq(d.config.getUint(ConfigKeys.registryStakeBase(5)), 500e18, "Verifier = 5x Storage base");
        assertEq(d.config.getUint(ConfigKeys.registryStakeBase(8)), 40e18, "Blob Gateway flat stake");
        assertEq(d.config.getUint(ConfigKeys.registryStakePerUnit(8)), 0);
        assertEq(d.config.getUint(ConfigKeys.rewardTypeWeight(6)), 0, "Email IP Address never earns");
        (uint16[] memory bg, bool[] memory lower) = d.weights.weightsFor(8, 0);
        assertEq(bg.length, 4);
        assertEq(bg[0], 3_000);
        assertEq(bg[1], 3_000);
        assertEq(bg[2], 2_000);
        assertEq(bg[3], 2_000);
        assertTrue(lower[2] && !lower[0] && !lower[1] && !lower[3], "only p95 latency is lower-is-better");
        (uint16[] memory v,) = d.weights.weightsFor(5, 0);
        assertEq(v[0], 4_000);
        assertEq(v[1], 3_000);
        assertEq(v[2], 3_000);
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
