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
        assertEq(d.config.owner(), deployer);
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
        vm.warp(d.rewards.epochFinalityDeadline(0) + 1);
        uint256 before = d.token.balanceOf(node);
        address[] memory nodes = new address[](1);
        nodes[0] = node;
        d.rewards.sealEpoch(0, nodes);

        // 100% heartbeat, so reliability stays 1.0x and the lone node takes the whole capacity share.
        uint256 scheduled = d.emission.primaryEmission(0);
        assertEq(d.emission.emittedAmount(0), scheduled);
        assertApproxEqAbs(d.token.balanceOf(node) - before, (scheduled * 9_200) / 10_000, 2);
    }

    function _signMetric(address who, uint256 epoch, uint256 score, uint256 heartbeatBps) internal view returns (bytes memory) {
        bytes32 digest = keccak256(
            abi.encodePacked("\x19Ethereum Signed Message:\n32", keccak256(abi.encode(address(d.rewards), who, epoch, score, heartbeatBps)))
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(deployerPk, digest);
        return abi.encodePacked(r, s, v);
    }
}
