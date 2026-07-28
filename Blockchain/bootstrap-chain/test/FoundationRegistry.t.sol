// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {ISlashingController} from "../src/interfaces/ISlashingController.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {FoundationRegistry} from "../src/FoundationRegistry.sol";
import {RegionDistribution} from "../src/foundation/RegionDistribution.sol";

/// @notice Test suite for issue #57 (Foundation Node Infrastructure
/// Deployment, Phase 0), covering AC2 (stake sourcing/5x minimum), AC3
/// (quorum-3 attestation with the same-operator waiver), AC4/AC5 (12-month
/// floor + early exit + slashing trigger), AC6 (surplus routing, including
/// the Y<=X no-surplus case and non-foundation-node non-interference), and
/// AC8 (no bypass of standard verification via a differential test).
contract FoundationRegistryTest is CoreProtocolTestBase {
    FoundationRegistry internal foundation;

    address internal opsRole = makeAddr("foundationOps");
    address internal reserve = makeAddr("foundationReserve");
    address internal ecosystemReserve = makeAddr("ecosystemReserve");

    uint256 internal constant VERIFIER_MIN = 500e18;
    uint256 internal constant FIVE_X = VERIFIER_MIN * 5;

    function setUp() public {
        _deployCoreProtocol();

        foundation = FoundationRegistry(
            _deployProxy(address(new FoundationRegistry()), abi.encodeCall(FoundationRegistry.initialize, (cfg)))
        );

        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.FOUNDATION_OPS_ROLE, opsRole);
        cfg.setAddress(ConfigKeys.FOUNDATION_RESERVE_ADDRESS, reserve);
        cfg.setAddress(ConfigKeys.ECOSYSTEM_RESERVE_ADDRESS, ecosystemReserve);
        cfg.setAddress(ConfigKeys.FOUNDATION_REGISTRY_ADDRESS, address(foundation));
        cfg.setUint(ConfigKeys.REGION_DISTRIBUTION_CAP_BPS, 3_000); // 30%
        // Verifier (NodeType index 5) stake minimum: 500e18 flat.
        cfg.setUint(ConfigKeys.registryStakeBase(uint8(INodeRegistry.NodeType.Verifier)), VERIFIER_MIN);
        cfg.setUint(ConfigKeys.registryStakePerUnit(uint8(INodeRegistry.NodeType.Verifier)), 0);
        // StakeCommitmentViolation (index 3) penalty: 10%.
        cfg.setUint(ConfigKeys.slashingViolationPctBps(3), 1_000);
        // Verifier node type gets a reward-pool weight too, for the surplus-routing tests.
        cfg.setUint(ConfigKeys.rewardTypeWeight(uint8(INodeRegistry.NodeType.Verifier)), 10_000);
        vm.stopPrank();

        token.mint(reserve, 1_000_000e18);
        vm.prank(reserve);
        token.approve(address(foundation), type(uint256).max);
    }

    // --- helpers ---

    function _selfRegisterAsVerifier(address node, uint256 initialStake) internal {
        _fund(node, initialStake);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Verifier, 0, "us-east", initialStake);
    }

    function _registerFoundation(address node, string memory region, uint256 initialStake, uint256 topUpAmount)
        internal
    {
        _selfRegisterAsVerifier(node, initialStake);
        vm.prank(opsRole);
        foundation.registerFoundationNode(node, region, topUpAmount, "");
    }

    // --- AC2: stake sourcing / 5x minimum ---

    function test_registerFoundationNode_meetsFiveXMinimum_andFlagsAtomically() public {
        address node = makeAddr("node1");
        uint256 reserveBefore = token.balanceOf(reserve);

        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        assertTrue(foundation.isFoundationNode(node));
        assertEq(registry.stakeOf(node), FIVE_X);
        assertEq(token.balanceOf(reserve), reserveBefore - (FIVE_X - VERIFIER_MIN));

        FoundationRegistry.FoundationRecord memory rec = foundation.getFoundationRecord(node);
        assertEq(rec.stake, FIVE_X);
        assertEq(rec.floorEndDate, block.timestamp + 365 days);
    }

    function test_registerFoundationNode_belowFiveX_reverts() public {
        address node = makeAddr("node2");
        _selfRegisterAsVerifier(node, VERIFIER_MIN);

        vm.prank(opsRole);
        vm.expectRevert(
            abi.encodeWithSelector(FoundationRegistry.InsufficientFoundationStake.selector, VERIFIER_MIN, FIVE_X)
        );
        foundation.registerFoundationNode(node, "us-east", 0, "");

        assertFalse(foundation.isFoundationNode(node));
    }

    function test_registerFoundationNode_onlyFoundationOps() public {
        address node = makeAddr("node3");
        _selfRegisterAsVerifier(node, VERIFIER_MIN);

        vm.expectRevert(abi.encodeWithSelector(FoundationRegistry.NotFoundationOps.selector, address(this)));
        foundation.registerFoundationNode(node, "us-east", FIVE_X - VERIFIER_MIN, "");
    }

    // --- AC1 / region distribution cap ---

    function test_regionCap_blocksOverConcentration() public {
        address nodeA = makeAddr("regionA");
        address nodeB = makeAddr("regionB");
        address nodeC = makeAddr("regionC");
        address nodeD = makeAddr("regionD-2nd-useast");

        _registerFoundation(nodeA, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(nodeB, "eu-west", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(nodeC, "ap-south", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        assertEq(foundation.foundationNodeCount(), 3);

        // A 4th node piling into an already-represented region (us-east)
        // would be 2/4 = 50% > 30% cap -> must revert.
        _selfRegisterAsVerifier(nodeD, VERIFIER_MIN);
        vm.prank(opsRole);
        vm.expectRevert();
        foundation.registerFoundationNode(nodeD, "us-east", FIVE_X - VERIFIER_MIN, "");
    }

    // --- AC3: quorum-3 attestation with same-operator waiver ---

    function test_genesisAttestation_bootstrapsFirstNode() public {
        address node = makeAddr("genesisNode");
        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        // FoundationRegistry must hold the verifier role to record verification.
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE, address(foundation));

        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.PendingVerification));

        vm.prank(timelockAddr);
        foundation.genesisAttestFoundationNode(node, 0);

        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Active));
    }

    function test_genesisAttestation_onlyOnce() public {
        address nodeA = makeAddr("genA");
        address nodeB = makeAddr("genB");
        _registerFoundation(nodeA, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(nodeB, "eu-west", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE, address(foundation));

        vm.prank(timelockAddr);
        foundation.genesisAttestFoundationNode(nodeA, 0);

        vm.prank(timelockAddr);
        vm.expectRevert(FoundationRegistry.GenesisAlreadyUsed.selector);
        foundation.genesisAttestFoundationNode(nodeB, 0);
    }

    function test_quorumAttestation_requiresThreeFoundationAttestors() public {
        address n1 = makeAddr("q1");
        address n2 = makeAddr("q2");
        address n3 = makeAddr("q3");
        address newNode = makeAddr("qNew");

        _registerFoundation(n1, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(n2, "eu-west", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(n3, "ap-south", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(newNode, "sa-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE, address(foundation));

        address[] memory attestors = new address[](3);
        attestors[0] = n1;
        attestors[1] = n2;
        attestors[2] = n3;

        vm.prank(opsRole);
        foundation.attestRegionVerification(newNode, attestors, 0);

        assertEq(uint8(registry.statusOf(newNode)), uint8(INodeRegistry.NodeStatus.Active));
    }

    function test_quorumAttestation_revertsUnderQuorum() public {
        address n1 = makeAddr("qq1");
        address n2 = makeAddr("qq2");
        address newNode = makeAddr("qqNew");
        _registerFoundation(n1, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(n2, "eu-west", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(newNode, "ap-south", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        address[] memory attestors = new address[](2);
        attestors[0] = n1;
        attestors[1] = n2;

        vm.prank(opsRole);
        vm.expectRevert(abi.encodeWithSelector(FoundationRegistry.InsufficientQuorum.selector, 2, 3));
        foundation.attestRegionVerification(newNode, attestors, 0);
    }

    function test_quorumAttestation_revertsIfAnyAttestorNotFoundation() public {
        address n1 = makeAddr("qf1");
        address n2 = makeAddr("qf2");
        address notFoundation = makeAddr("notFoundation");
        address newNode = makeAddr("qfNew");
        _registerFoundation(n1, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(n2, "eu-west", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _registerFoundation(newNode, "ap-south", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        address[] memory attestors = new address[](3);
        attestors[0] = n1;
        attestors[1] = n2;
        attestors[2] = notFoundation;

        vm.prank(opsRole);
        vm.expectRevert(
            abi.encodeWithSelector(FoundationRegistry.AttestorNotFoundationVerifier.selector, notFoundation)
        );
        foundation.attestRegionVerification(newNode, attestors, 0);
    }

    // --- AC4 / AC5: 12-month floor, early exit, slashing trigger ---

    function test_earlyExit_before12Months_emitsEarlyExitAndSlashes() public {
        address node = makeAddr("early");
        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        vm.warp(block.timestamp + 120 days); // ~4 months

        uint256 stakeBefore = registry.stakeOf(node);
        bytes32 reason = bytes32("ops-early-exit");

        vm.expectEmit(true, false, false, false, address(foundation));
        emit FoundationRegistry.EarlyExitEvent(node, reason, 0, 0);
        vm.prank(opsRole);
        foundation.deregisterFoundationNode(node, reason);

        assertFalse(foundation.isFoundationNode(node));
        // Slashing applied via triggerStakeCommitmentViolation (10% configured above).
        uint256 expectedSlash = (stakeBefore * 1_000) / 10_000;
        assertEq(registry.stakeOf(node), stakeBefore - expectedSlash);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Banned));
    }

    function test_onTimeExit_atExactFloorEndDate_isNotEarly() public {
        address node = makeAddr("onTime");
        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        FoundationRegistry.FoundationRecord memory rec = foundation.getFoundationRecord(node);
        vm.warp(rec.floorEndDate); // inclusive boundary: exactly floorEndDate is on-time.

        uint256 stakeBefore = registry.stakeOf(node);
        vm.prank(opsRole);
        foundation.deregisterFoundationNode(node, bytes32(0));

        assertFalse(foundation.isFoundationNode(node));
        assertEq(registry.stakeOf(node), stakeBefore); // untouched -> no slash triggered.
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.PendingVerification));
    }

    function test_normalExit_after13Months_noEarlyExitOrPenalty() public {
        address node = makeAddr("normal");
        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        vm.warp(block.timestamp + 395 days); // ~13 months

        uint256 stakeBefore = registry.stakeOf(node);
        vm.prank(opsRole);
        foundation.deregisterFoundationNode(node, bytes32(0));

        assertFalse(foundation.isFoundationNode(node));
        assertEq(registry.stakeOf(node), stakeBefore);
    }

    // --- AC6: reward-surplus routing ---

    function test_surplusRouting_capsOperatorPayout_andRoutesSurplusToEcosystemReserve() public {
        address node = makeAddr("rewardedFoundationNode");
        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE, address(foundation));
        vm.prank(timelockAddr);
        foundation.genesisAttestFoundationNode(node, 0);

        uint256 baseline = 10e18;
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.operationalCostBaseline(node), baseline);
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.FOUNDATION_REWARD_HOOK, address(foundation));

        token.mint(address(rewards), 1_000_000e18);
        rewards.submitMetricReport(node, 0, 100, 9_500, _signMetric(node, 0, 100, 9_500));
        vm.warp(rewards.epochFinalityDeadline(0) + 1);

        address[] memory nodes = new address[](1);
        nodes[0] = node;

        uint256 ecosystemBefore = token.balanceOf(ecosystemReserve);
        uint256 nodeBefore = token.balanceOf(node);

        rewards.sealEpoch(0, nodes);

        uint256 grossPayout = rewards.payoutOf(0, node);
        assertGt(grossPayout, baseline); // sanity: sole-eligible node earns the full pool, > tiny baseline.

        uint256 nodePaid = token.balanceOf(node) - nodeBefore;
        uint256 ecosystemPaid = token.balanceOf(ecosystemReserve) - ecosystemBefore;

        assertEq(nodePaid, baseline);
        assertEq(ecosystemPaid, grossPayout - baseline);
    }

    function test_surplusRouting_noSurplus_whenRewardAtOrBelowBaseline() public {
        address node = makeAddr("smallReward");
        address otherNode = makeAddr("otherNode"); // dilutes the pool so `node`'s share stays small.
        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);
        _selfRegisterAsVerifier(otherNode, VERIFIER_MIN);
        vm.prank(verifierRole);
        registry.recordVerification(otherNode, true, 0);

        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE, address(foundation));
        vm.prank(timelockAddr);
        foundation.genesisAttestFoundationNode(node, 0);

        uint256 hugeBaseline = 1_000_000e18; // ensures Y <= X.
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.operationalCostBaseline(node), hugeBaseline);
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.FOUNDATION_REWARD_HOOK, address(foundation));

        token.mint(address(rewards), 1_000_000e18);
        rewards.submitMetricReport(node, 0, 100, 9_500, _signMetric(node, 0, 100, 9_500));
        rewards.submitMetricReport(otherNode, 0, 100, 9_500, _signMetric(otherNode, 0, 100, 9_500));
        vm.warp(rewards.epochFinalityDeadline(0) + 1);

        address[] memory nodes = new address[](2);
        nodes[0] = node;
        nodes[1] = otherNode;

        uint256 ecosystemBefore = token.balanceOf(ecosystemReserve);
        uint256 nodeBefore = token.balanceOf(node);

        rewards.sealEpoch(0, nodes);

        uint256 grossPayout = rewards.payoutOf(0, node);
        uint256 nodePaid = token.balanceOf(node) - nodeBefore;

        assertEq(nodePaid, grossPayout); // full amount retained, no cap applied.
        assertEq(token.balanceOf(ecosystemReserve), ecosystemBefore); // no surplus transferred.
    }

    function test_surplusRouting_doesNotAffectNonFoundationNodes() public {
        address communityNode = makeAddr("community");
        _selfRegisterAsVerifier(communityNode, VERIFIER_MIN);
        vm.prank(verifierRole);
        registry.recordVerification(communityNode, true, 0);

        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.operationalCostBaseline(communityNode), 1); // even a tiny baseline...
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.FOUNDATION_REWARD_HOOK, address(foundation));

        token.mint(address(rewards), 1_000_000e18);
        rewards.submitMetricReport(communityNode, 0, 100, 9_500, _signMetric(communityNode, 0, 100, 9_500));
        vm.warp(rewards.epochFinalityDeadline(0) + 1);

        address[] memory nodes = new address[](1);
        nodes[0] = communityNode;

        uint256 ecosystemBefore = token.balanceOf(ecosystemReserve);
        uint256 nodeBefore = token.balanceOf(communityNode);

        rewards.sealEpoch(0, nodes);

        uint256 grossPayout = rewards.payoutOf(0, communityNode);
        uint256 nodePaid = token.balanceOf(communityNode) - nodeBefore;

        // Non-foundation node: hook passes the full amount through untouched.
        assertEq(nodePaid, grossPayout);
        assertEq(token.balanceOf(ecosystemReserve), ecosystemBefore);
    }

    // --- AC8: no bypass of standard verification/slashing (differential) ---

    function test_foundationNode_subjectToSameSlashingStagesAsCommunityNode() public {
        address node = makeAddr("faultyFoundation");
        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE, address(foundation));
        vm.prank(timelockAddr);
        foundation.genesisAttestFoundationNode(node, 0);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Active));

        uint256 before = registry.stakeOf(node);
        bytes memory sig = _signFault(node, 3, 1, false);
        slashing.submitFaultAttestation(node, 3, 1, false, sig);

        uint256 expected = (before * 500) / 10_000; // Stage 3 -> same 5% as any node (CoreProtocolTestBase config).
        assertEq(registry.stakeOf(node), before - expected);
        assertEq(slashing.stageOf(node), 3);
    }

    function test_foundationRegistry_cannotBypassMinVerifierStakeViaDirectRegistryCall() public {
        // AC8 differential: a foundation node is still bound by the exact
        // same StakingNodeRegistry stake-minimum invariant as a community
        // node -- registering directly against the core registry with too
        // little stake reverts identically for both.
        address node = makeAddr("bypassAttempt");
        _fund(node, 1e18);
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Verifier, 0);
        vm.prank(node);
        vm.expectRevert(abi.encodeWithSelector(bytes4(keccak256("InsufficientStake(uint256,uint256)")), 1e18, required));
        registry.register(INodeRegistry.NodeType.Verifier, 0, "us-east", 1e18);
    }

    function test_deregisterFoundationNode_onlyFoundationOps() public {
        address node = makeAddr("depRole");
        _registerFoundation(node, "us-east", VERIFIER_MIN, FIVE_X - VERIFIER_MIN);

        vm.expectRevert(abi.encodeWithSelector(FoundationRegistry.NotFoundationOps.selector, address(this)));
        foundation.deregisterFoundationNode(node, bytes32(0));
    }
}
