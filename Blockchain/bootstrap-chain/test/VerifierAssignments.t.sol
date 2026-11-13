// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {MockTelemetryRecords} from "./TypeScoring.t.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {VerifierAssignments} from "../src/VerifierAssignments.sol";
import {VerifierFactorReader} from "../src/libraries/VerifierFactorReader.sol";
import {TelemetryCheckpointRegistry} from "../src/TelemetryCheckpointRegistry.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

/// @notice #227: on-chain Verifier assignments, attestations, peer-agreement accuracy and Verifier payouts.
contract VerifierAssignmentsTest is CoreProtocolTestBase {
    INodeRegistry.NodeType internal constant VERIFIER = INodeRegistry.NodeType.Verifier;
    INodeRegistry.NodeType internal constant GATEWAY = INodeRegistry.NodeType.BlobGateway;
    uint256 internal constant SLICE = (1_000_000e18 * 800) / 10_000;

    VerifierAssignments internal va;
    MockTelemetryRecords internal telemetry;
    uint256[] internal vPks;
    address[] internal vs;
    address[] internal gws;

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 1_000_000_000e18);
        va = VerifierAssignments(_deployProxy(address(new VerifierAssignments()), abi.encodeCall(VerifierAssignments.initialize, (cfg))));
        telemetry = new MockTelemetryRecords();
        telemetry.setDeadline(0, epochs.finalityDeadline(0) + 30 minutes);
        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.REWARD_VERIFIER_ASSIGNMENTS, address(va));
        cfg.setAddress(ConfigKeys.REWARD_TELEMETRY_REGISTRY, address(telemetry));
        cfg.setUint(ConfigKeys.registryStakeBase(5), 125e18);
        cfg.setUint(ConfigKeys.registryStakeBase(8), 40e18);
        cfg.setUint(ConfigKeys.VERIFIER_K, 3);
        cfg.setUint(ConfigKeys.VERIFIER_TOLERANCE_REL_BPS, 1_000);
        cfg.setUint(ConfigKeys.verifierToleranceAbs(0), 200); // uptime bps
        cfg.setUint(ConfigKeys.verifierToleranceAbs(1), 200); // success bps
        cfg.setUint(ConfigKeys.verifierToleranceAbs(2), 50); // p95 ms
        cfg.setUint(ConfigKeys.verifierToleranceAbs(3), 0); // bytes: relative only
        vm.stopPrank();
    }

    // ---------------------------------------------------------------- helpers

    function _register(address node, INodeRegistry.NodeType s, uint256 stake) internal {
        _fund(node, 1_000_000e18);
        vm.prank(node);
        registry.registerService(s, 0, "us-east", stake);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    function _network(uint256 verifiers, uint256 gateways) internal {
        for (uint256 i; i < verifiers; i++) {
            uint256 pk = 0x1000 + vPks.length;
            vPks.push(pk);
            vs.push(vm.addr(pk));
            _register(vm.addr(pk), VERIFIER, 125e18);
            va.sync(vm.addr(pk));
        }
        for (uint256 i; i < gateways; i++) {
            address g = makeAddr(string(abi.encodePacked("gw", vm.toString(gws.length))));
            gws.push(g);
            _register(g, GATEWAY, 40e18);
            va.sync(g);
        }
    }

    function _pkOf(address v) internal view returns (uint256) {
        for (uint256 i; i < vs.length; i++) {
            if (vs[i] == v) return vPks[i];
        }
        revert("unknown verifier");
    }

    function _values(uint256 up, uint256 ok, uint256 p95, uint256 bytesServed) internal pure returns (uint256[] memory v) {
        v = new uint256[](4);
        (v[0], v[1], v[2], v[3]) = (up, ok, p95, bytesServed);
    }

    function _sign(uint256 pk, uint256 epoch, address verifier, VerifierAssignments.Item[] memory items) internal view returns (bytes memory) {
        bytes32 digest = keccak256(abi.encode(address(va), block.chainid, epoch, verifier, keccak256(abi.encode(items))));
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(pk, keccak256(abi.encodePacked("\x19Ethereum Signed Message:\n32", digest)));
        return abi.encodePacked(r, s, v);
    }

    function _attestOne(address verifier, address target, uint256[] memory values) internal {
        address[] memory chosen = va.assignedVerifiers(0, target);
        uint8 slot = type(uint8).max;
        for (uint256 d; d < chosen.length; d++) {
            if (chosen[d] == verifier) slot = uint8(d);
        }
        require(slot != type(uint8).max, "not assigned");
        VerifierAssignments.Item[] memory items = new VerifierAssignments.Item[](1);
        items[0] = VerifierAssignments.Item(target, slot, values);
        va.attest(0, verifier, items, _sign(_pkOf(verifier), 0, verifier, items));
    }

    function _record(uint16 compliance) internal pure returns (TelemetryCheckpointRegistry.AggregatedMetricRecord memory r) {
        r.heartbeatComplianceBps = compliance;
        r.heartbeatsReceived = 1;
        r.heartbeatsExpected = 288;
        r.eligible = true;
    }

    // ---------------------------------------------------------------- assignment

    function test_AssignmentsAreDistinctDeterministicAndNeverSelf() public {
        _network(5, 4);
        va.seedEpoch(0);

        for (uint256 g; g < gws.length; g++) {
            address[] memory chosen = va.assignedVerifiers(0, gws[g]);
            assertEq(chosen.length, 3);
            for (uint256 i; i < 3; i++) {
                assertTrue(chosen[i] != gws[g]);
                for (uint256 j; j < i; j++) assertTrue(chosen[i] != chosen[j], "distinct");
            }
            address[] memory again = va.assignedVerifiers(0, gws[g]);
            for (uint256 i; i < 3; i++) assertEq(again[i], chosen[i], "deterministic");
        }
    }

    function test_KIsCappedByVerifiersOtherThanTheTarget() public {
        _network(2, 0);
        _register(vs[0], GATEWAY, 40e18); // verifier 0 also runs a gateway
        va.sync(vs[0]);
        va.seedEpoch(0);

        address[] memory chosen = va.assignedVerifiers(0, vs[0]);
        assertEq(chosen.length, 1);
        assertEq(chosen[0], vs[1]);
    }

    function test_LoneVerifierCheckingItself_HasNoAssignments() public {
        _network(1, 0);
        _register(vs[0], GATEWAY, 40e18);
        va.sync(vs[0]);
        va.seedEpoch(0);

        assertEq(va.assignedVerifiers(0, vs[0]).length, 0);
        (uint256 coverage, uint256 accuracy) = va.factorsOf(0, vs[0]);
        assertEq(coverage, 10_000);
        assertEq(accuracy, 10_000);
    }

    function test_SnapshotFreezesPools_LaterStatusChangesDontMoveAssignments() public {
        _network(4, 2);
        va.seedEpoch(0);
        address[] memory before = va.assignedVerifiers(0, gws[0]);

        _network(3, 0); // more verifiers after the seed
        assertEq(va.verifierCount(0), 4);
        address[] memory after_ = va.assignedVerifiers(0, gws[0]);
        for (uint256 i; i < before.length; i++) assertEq(after_[i], before[i]);
    }

    function test_SyncRemovesInactiveNodes_SwapAndPopKeepsTheRest() public {
        _network(3, 0);
        vm.prank(verifierRole);
        registry.recordVerification(vs[0], false, 0); // Active -> Suspended
        va.sync(vs[0]);
        va.seedEpoch(0);

        assertEq(va.verifierCount(0), 2);
        assertTrue(va.verifierAt(0, 0) != vs[0] && va.verifierAt(0, 1) != vs[0]);
    }

    function test_RevertWhen_SeedOutsideTheEpoch() public {
        vm.expectRevert(abi.encodeWithSelector(VerifierAssignments.OutsideEpoch.selector, 1));
        va.seedEpoch(1);
    }

    function test_AssignmentsOf_PagesAVerifiersWork() public {
        _network(4, 6);
        va.seedEpoch(0);
        uint256 total;
        for (uint256 v; v < vs.length; v++) {
            (address[] memory first, , uint256 next) = va.assignmentsOf(0, vs[v], 0, 3);
            (address[] memory rest, , ) = va.assignmentsOf(0, vs[v], next, 10);
            total += first.length + rest.length;
        }
        assertEq(total, 6 * 3, "every slot of every target belongs to exactly one verifier");
    }

    // ---------------------------------------------------------------- attestation

    function test_RevertWhen_UnassignedOrBadlySigned() public {
        _network(4, 1);
        va.seedEpoch(0);
        address[] memory chosen = va.assignedVerifiers(0, gws[0]);
        address outsider;
        for (uint256 i; i < vs.length; i++) {
            if (vs[i] != chosen[0] && vs[i] != chosen[1] && vs[i] != chosen[2]) outsider = vs[i];
        }

        VerifierAssignments.Item[] memory items = new VerifierAssignments.Item[](1);
        items[0] = VerifierAssignments.Item(gws[0], 0, _values(10_000, 10_000, 20, 1_000));
        vm.expectRevert(abi.encodeWithSelector(VerifierAssignments.NotAssigned.selector, 0, gws[0], 0, outsider));
        va.attest(0, outsider, items, _sign(_pkOf(outsider), 0, outsider, items));

        vm.expectRevert(VerifierAssignments.InvalidSignature.selector);
        va.attest(0, chosen[0], items, _sign(_pkOf(outsider), 0, chosen[0], items));
    }

    function test_RevertWhen_AttestingAfterTheWindow() public {
        _network(3, 1);
        va.seedEpoch(0);
        vm.warp(epochs.finalityDeadline(0) + 1);
        address v = va.assignedVerifiers(0, gws[0])[0];
        VerifierAssignments.Item[] memory items = new VerifierAssignments.Item[](1);
        items[0] = VerifierAssignments.Item(gws[0], 0, _values(1, 1, 1, 1));
        vm.expectRevert(abi.encodeWithSelector(VerifierAssignments.WindowClosed.selector, 0));
        va.attest(0, v, items, _sign(_pkOf(v), 0, v, items));
    }

    function test_ResubmissionReplaces_AndCountsOnce() public {
        _network(3, 1);
        va.seedEpoch(0);
        address v = va.assignedVerifiers(0, gws[0])[0];
        _attestOne(v, gws[0], _values(1, 1, 1, 1));
        _attestOne(v, gws[0], _values(9_000, 9_000, 30, 500));

        assertEq(va.countsOf(0, v).submitted, 1);
        (, uint256[] memory values) = va.attestationOf(0, gws[0], 0);
        assertEq(values[2], 30);
    }

    // ---------------------------------------------------------------- finalize

    function test_Outlier_FailsAccuracy_HonestPairIsUpheld() public {
        _network(3, 1);
        va.seedEpoch(0);
        address[] memory chosen = va.assignedVerifiers(0, gws[0]);
        _attestOne(chosen[0], gws[0], _values(10_000, 9_900, 40, 1_000_000));
        _attestOne(chosen[1], gws[0], _values(9_980, 9_950, 42, 1_050_000));
        _attestOne(chosen[2], gws[0], _values(2_000, 1_000, 900, 10)); // wildly off

        vm.warp(epochs.finalityDeadline(0) + 1);
        assertTrue(va.finalize(0, 100));

        (, uint256 a0) = va.factorsOf(0, chosen[0]);
        (, uint256 a1) = va.factorsOf(0, chosen[1]);
        (, uint256 a2) = va.factorsOf(0, chosen[2]);
        assertEq(a0, 10_000);
        assertEq(a1, 10_000);
        assertEq(a2, 0);
    }

    function test_SkippedAssignment_LowersCoverage() public {
        _network(3, 2);
        va.seedEpoch(0);
        address v = va.assignedVerifiers(0, gws[0])[0]; // with 3 verifiers, every verifier is on every gateway
        _attestOne(v, gws[0], _values(10_000, 10_000, 20, 1_000));

        vm.warp(epochs.finalityDeadline(0) + 1);
        va.finalize(0, 100);

        (uint256 coverage, uint256 accuracy) = va.factorsOf(0, v);
        assertEq(coverage, 5_000); // 1 of 2 assignments
        assertEq(accuracy, 10_000); // fewer than 3 attestations: nothing judged
    }

    function test_RevertWhen_FinalizingWhileTheWindowIsOpen() public {
        _network(3, 1);
        va.seedEpoch(0);
        vm.expectRevert(abi.encodeWithSelector(VerifierAssignments.WindowOpen.selector, 0));
        va.finalize(0, 10);
    }

    function test_FinalizeInChunks() public {
        _network(3, 5);
        va.seedEpoch(0);
        vm.warp(epochs.finalityDeadline(0) + 1);
        assertFalse(va.finalize(0, 2));
        assertFalse(va.finalize(0, 2));
        assertTrue(va.finalize(0, 2));
        assertTrue(va.isFinalized(0));
        assertEq(va.countsOf(0, vs[0]).assigned, 5);
    }

    // ---------------------------------------------------------------- settlement

    function test_SealWaitsForFinalize_ThenPaysVerifiersFromTheSlice() public {
        _network(3, 1);
        va.seedEpoch(0);
        address[] memory chosen = va.assignedVerifiers(0, gws[0]);
        _attestOne(chosen[0], gws[0], _values(10_000, 10_000, 40, 1_000));
        _attestOne(chosen[1], gws[0], _values(10_000, 10_000, 40, 1_000));
        // chosen[2] skips: coverage 0
        for (uint256 i; i < 3; i++) telemetry.setRecord(0, vs[i], _record(10_000));
        telemetry.finalize(0);
        vm.warp(epochs.finalityDeadline(0) + 1);

        vm.expectRevert(abi.encodeWithSelector(VerifierFactorReader.VerifierFinalizePending.selector, 0));
        rewards.sealEpoch(0, vs);

        va.finalize(0, 100);
        rewards.sealEpoch(0, vs);

        uint256 full = rewards.settlementOf(0, chosen[0]).score;
        uint256 skipped = rewards.settlementOf(0, chosen[2]).score;
        assertEq(full, 1e18);
        assertEq(skipped, 0.7e18); // uptime 0.4 + accuracy 0.3 (nothing judged) + coverage 0
        uint256 paid = rewards.payoutOf(0, vs[0]) + rewards.payoutOf(0, vs[1]) + rewards.payoutOf(0, vs[2]);
        assertLe(paid, SLICE);
        assertApproxEqAbs(paid, SLICE, 10);
    }

    function test_VerifierWithoutATelemetryRecord_IsIneligible() public {
        _network(1, 0);
        va.seedEpoch(0);
        telemetry.finalize(0);
        vm.warp(epochs.finalityDeadline(0) + 1);
        va.finalize(0, 10);
        rewards.sealEpoch(0, vs);

        assertEq(uint8(rewards.settlementOf(0, vs[0]).reason), uint8(RewardDistributor.SettlementReason.Ineligible));
    }

    function test_SignedVerifierReport_CantOverrideOnChainScores() public {
        _network(3, 1);
        va.seedEpoch(0);
        for (uint256 i; i < 3; i++) telemetry.setRecord(0, vs[i], _record(10_000));
        telemetry.finalize(0);
        // The metric signer claims perfect coverage for a verifier that attested nothing.
        uint256[] memory claimed = new uint256[](3);
        (claimed[0], claimed[1], claimed[2]) = (10_000, 10_000, 10_000);
        rewards.submitServiceFactorMetrics(vs[0], VERIFIER, 0, claimed, 10_000,
            _signFactorService(vs[0], VERIFIER, 0, claimed, 10_000));
        vm.warp(epochs.finalityDeadline(0) + 1);
        va.finalize(0, 100);
        rewards.sealEpoch(0, vs);

        assertEq(rewards.settlementOf(0, vs[0]).score, 0.7e18, "on-chain coverage 0 wins over the signed report");
    }

    function test_UnseededEpoch_FullMarksAndSealDoesNotWait() public {
        _network(2, 1);
        for (uint256 i; i < 2; i++) telemetry.setRecord(0, vs[i], _record(10_000));
        telemetry.finalize(0);
        vm.warp(epochs.finalityDeadline(0) + 1);
        rewards.sealEpoch(0, vs);

        assertEq(rewards.settlementOf(0, vs[0]).score, 1e18);
    }

    function test_GatewayRewardsUnchanged() public {
        // AC7: Blob Gateways are still scored from the metric signer's signed reports.
        _network(3, 1);
        uint256[] memory g = _values(10_000, 10_000, 40, 1_000);
        rewards.submitServiceFactorMetrics(gws[0], GATEWAY, 0, g, 10_000, _signFactorService(gws[0], GATEWAY, 0, g, 10_000));
        assertTrue(rewards.metricOf(0, rewards.accountOf(gws[0], GATEWAY)).submitted);
    }

    function _signFactorService(address node, INodeRegistry.NodeType s, uint256 epoch, uint256[] memory values, uint256 hb)
        internal
        view
        returns (bytes memory)
    {
        bytes32 digest = keccak256(abi.encodePacked("\x19Ethereum Signed Message:\n32",
            keccak256(abi.encode(address(rewards), node, uint8(s), epoch, values, hb))));
        (uint8 v, bytes32 r, bytes32 sig) = vm.sign(metricSignerPk, digest);
        return abi.encodePacked(r, sig, v);
    }
}

/// @notice A registry stand-in for gas sizing at scale: small addresses are Active Blob Gateways, the rest Active Verifiers.
contract AllActiveRegistry {
    function serviceStatusOf(address node, INodeRegistry.NodeType service) external pure returns (INodeRegistry.NodeStatus) {
        bool gateway = uint160(node) <= 0xFFFFFF;
        return (service == INodeRegistry.NodeType.BlobGateway) == gateway ? INodeRegistry.NodeStatus.Active : INodeRegistry.NodeStatus.Suspended;
    }
}

/// @notice #227 AC6: 100 Verifiers × 1,000 targets at k=3 stays within half a 30M block per transaction.
contract VerifierAssignmentsGasTest is CoreProtocolTestBase {
    uint256 internal constant HALF_BLOCK = 15_000_000;

    /// @notice What the Blockchain Gateway's sealer sends per finalize tx (about 75k gas per target).
    uint256 internal constant FINALIZE_CHUNK = 100;

    VerifierAssignments internal va;

    function setUp() public {
        _deployCoreProtocol();
        va = VerifierAssignments(_deployProxy(address(new VerifierAssignments()), abi.encodeCall(VerifierAssignments.initialize, (cfg))));
        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_ADDRESS, address(new AllActiveRegistry()));
        cfg.setUint(ConfigKeys.VERIFIER_K, 3);
        cfg.setUint(ConfigKeys.VERIFIER_TOLERANCE_REL_BPS, 1_000);
        vm.stopPrank();
    }

    function test_Gas_100Verifiers1000Targets() public {
        address[] memory verifiers = new address[](100);
        for (uint256 i; i < 100; i++) verifiers[i] = vm.addr(0x2000 + i);
        va.syncMany(verifiers);
        address[] memory batch = new address[](100);
        for (uint256 b; b < 10; b++) {
            for (uint256 i; i < 100; i++) batch[i] = address(uint160(0xA0000 + (b * 100) + i));
            va.syncMany(batch);
        }
        va.seedEpoch(0);
        assertEq(va.verifierCount(0), 100);
        assertEq(va.targetCount(0), 1_000);

        // One verifier's whole epoch as one tx (about 30 items: 1,000 targets × 3 slots over 100 verifiers).
        address v = verifiers[0];
        (address[] memory targets, uint8[] memory slots, ) = va.assignmentsOf(0, v, 0, 1_000);
        VerifierAssignments.Item[] memory items = new VerifierAssignments.Item[](targets.length);
        uint256[] memory values = new uint256[](4);
        (values[0], values[1], values[2], values[3]) = (10_000, 10_000, 40, 1_000);
        for (uint256 i; i < targets.length; i++) items[i] = VerifierAssignments.Item(targets[i], slots[i], values);
        bytes32 digest = va.attestationDigest(0, v, items);
        (uint8 sv, bytes32 r, bytes32 s) = vm.sign(0x2000, keccak256(abi.encodePacked("\x19Ethereum Signed Message:\n32", digest)));
        uint256 g0 = gasleft();
        va.attest(0, v, items, abi.encodePacked(r, s, sv));
        uint256 attestGas = g0 - gasleft();
        emit log_named_uint("attest items", items.length);
        assertGt(items.length, 10, "a fair share of the 3,000 slots");
        emit log_named_uint("attest gas", attestGas);
        assertLt(attestGas, HALF_BLOCK);

        vm.warp(epochs.finalityDeadline(0) + 1);
        uint256 worst;
        bool done;
        while (!done) {
            g0 = gasleft();
            done = va.finalize(0, FINALIZE_CHUNK);
            uint256 used = g0 - gasleft();
            if (used > worst) worst = used;
        }
        emit log_named_uint("worst finalize chunk gas", worst);
        assertLt(worst, HALF_BLOCK);
    }
}
