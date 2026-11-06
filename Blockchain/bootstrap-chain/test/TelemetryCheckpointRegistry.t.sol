// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {MockProtocolConfig} from "../src/config/MockProtocolConfig.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {EpochController} from "../src/EpochController.sol";
import {TelemetryCheckpointRegistry} from "../src/TelemetryCheckpointRegistry.sol";

/// @dev Only `statusOf` is exercised by the registry under test.
contract StatusOnlyRegistry {
    mapping(address => INodeRegistry.NodeStatus) public statusOf;

    function set(address node, INodeRegistry.NodeStatus s) external {
        statusOf[node] = s;
    }
}

/// @notice Issue #75 on-chain checkpointing and aggregated-record tests (QA §2.4, 2.6–2.9).
contract TelemetryCheckpointRegistryTest is Test {
    uint256 internal constant EPOCH = 1 days;
    uint256 internal constant FINALITY = 15 minutes;

    MockProtocolConfig internal cfg;
    TelemetryCheckpointRegistry internal reg;
    StatusOnlyRegistry internal nodes;
    address internal configOwner = makeAddr("configOwner");
    address internal timelock = makeAddr("timelock");
    uint256 internal genesis;

    uint256[] internal pks;
    address[] internal members; // ascending

    address internal node = makeAddr("node");

    bytes32 internal constant CP_TYPEHASH = keccak256(
        "TelemetryCheckpoint(uint256 epochIndex,uint256 checkpointIndex,bytes32 merkleRoot,bytes32 blobAddress,uint32 sampleCount)"
    );
    bytes32 internal constant AGG_TYPEHASH =
        keccak256("AggregatedRecords(uint256 epochIndex,uint256 chunkIndex,uint256 chunkCount,bytes32 recordsHash)");
    uint256 internal constant COMMIT_WINDOW = 30 minutes; // TELEMETRY_COMMIT_WINDOW default

    function setUp() public {
        vm.warp(1_700_000_000);
        genesis = block.timestamp;
        cfg = new MockProtocolConfig(configOwner);
        nodes = new StatusOnlyRegistry();
        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.GOVERNANCE_TIMELOCK, timelock);
        cfg.setAddress(ConfigKeys.REGISTRY_ADDRESS, address(nodes));
        cfg.setAddress(
            ConfigKeys.EPOCH_CONTROLLER, address(new EpochController(cfg, uint64(genesis), uint32(EPOCH), uint32(FINALITY)))
        );
        cfg.setUint(ConfigKeys.TELEMETRY_SELF_CHECKPOINT_GRACE, 30 minutes);
        cfg.setUint(ConfigKeys.TELEMETRY_DIVERGENCE_UPTIME_BPS, 1_000);
        cfg.setUint(ConfigKeys.TELEMETRY_DIVERGENCE_LATENCY_BPS, 5_000);
        vm.stopPrank();

        // Three members, sorted by address, threshold 2.
        uint256[3] memory keys = [uint256(0xC1), 0xC2, 0xC3];
        for (uint256 i; i < 3; ++i) {
            for (uint256 j = i + 1; j < 3; ++j) {
                if (vm.addr(keys[j]) < vm.addr(keys[i])) (keys[i], keys[j]) = (keys[j], keys[i]);
            }
        }
        for (uint256 i; i < 3; ++i) {
            pks.push(keys[i]);
            members.push(vm.addr(keys[i]));
        }

        reg = TelemetryCheckpointRegistry(
            address(
                new ERC1967Proxy(
                    address(new TelemetryCheckpointRegistry()),
                    abi.encodeCall(TelemetryCheckpointRegistry.initialize, (cfg, members, 2))
                )
            )
        );
    }

    // --- helpers ---

    function _domain() internal view returns (bytes32) {
        return keccak256(
            abi.encode(
                keccak256("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"),
                keccak256("SymposiaTelemetry"),
                keccak256("1"),
                block.chainid,
                address(reg)
            )
        );
    }

    function _sigs(bytes32 structHash, uint256 count) internal view returns (bytes[] memory s) {
        bytes32 digest = keccak256(abi.encodePacked("\x19\x01", _domain(), structHash));
        s = new bytes[](count);
        for (uint256 i; i < count; ++i) {
            (uint8 v, bytes32 r, bytes32 ss) = vm.sign(pks[i], digest);
            s[i] = abi.encodePacked(r, ss, v);
        }
    }

    function _cpSigs(uint256 epoch, uint256 idx, bytes32 root, uint32 n, uint256 count)
        internal
        view
        returns (bytes[] memory)
    {
        return _sigs(keccak256(abi.encode(CP_TYPEHASH, epoch, idx, root, bytes32("blob"), n)), count);
    }

    function _commit(uint256 epoch, uint256 idx, bytes32 root) internal {
        reg.commitCheckpoint(epoch, idx, root, bytes32("blob"), 12, _cpSigs(epoch, idx, root, 12, 2));
    }

    function _input(uint32 received, uint32 expected) internal view returns (TelemetryCheckpointRegistry.AggregatedMetricInput memory r) {
        r.node = node;
        r.heartbeatsReceived = received;
        r.heartbeatsExpected = expected;
        r.lastCheckpoint = 23;
        r.selfReported.uptimeBps = 9_900;
        r.selfReported.latencyMs = 50;
    }

    function _submit(uint256 epoch, TelemetryCheckpointRegistry.AggregatedMetricInput[] memory inputs) internal {
        bytes32 h = keccak256(abi.encode(inputs));
        reg.submitAggregatedRecords(epoch, 0, 1, inputs, _sigs(keccak256(abi.encode(AGG_TYPEHASH, epoch, 0, 1, h)), 2));
    }

    function _one(TelemetryCheckpointRegistry.AggregatedMetricInput memory r)
        internal
        pure
        returns (TelemetryCheckpointRegistry.AggregatedMetricInput[] memory a)
    {
        a = new TelemetryCheckpointRegistry.AggregatedMetricInput[](1);
        a[0] = r;
    }

    /// @dev One second past the telemetry deadline (finality + commit window); the deadline second itself is in-window.
    function _afterFinality(uint256 epoch) internal {
        vm.warp(genesis + (epoch + 1) * EPOCH + FINALITY + COMMIT_WINDOW + 1);
    }

    // --- committee ---

    function test_CommitteeConfigured() public view {
        assertEq(reg.CHECKPOINT_TYPEHASH(), CP_TYPEHASH);
        assertEq(reg.AGGREGATE_TYPEHASH(), AGG_TYPEHASH);
        (address[] memory m, uint256 t) = reg.committee();
        assertEq(m.length, 3);
        assertEq(t, 2);
        assertTrue(reg.isCommitteeMember(members[0]));
        assertFalse(reg.isCommitteeMember(node));
    }

    function test_RevertWhen_MinorityThreshold() public {
        vm.prank(timelock);
        vm.expectRevert(TelemetryCheckpointRegistry.InvalidCommittee.selector);
        reg.setCommittee(members, 1);
    }

    function test_RevertWhen_NonTimelockSetsCommittee() public {
        vm.expectRevert();
        reg.setCommittee(members, 2);
    }

    function test_RotationRevokesOldMembers() public {
        address[] memory next = new address[](1);
        next[0] = makeAddr("newMember");
        vm.prank(timelock);
        reg.setCommittee(next, 1);
        assertFalse(reg.isCommitteeMember(members[0]));
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.NotCommitteeMember.selector, members[0]));
        _commit(0, 0, keccak256("r"));
    }

    // --- checkpoints (CP-*) ---

    function test_CommitCheckpoint() public {
        _commit(0, 3, keccak256("root"));
        TelemetryCheckpointRegistry.TelemetryCheckpoint memory c = reg.getCheckpoint(0, 3);
        assertEq(c.merkleRoot, keccak256("root"));
        assertEq(c.sampleCount, 12);
        assertEq(reg.checkpointCount(0), 1);
    }

    function test_RevertWhen_BelowThreshold() public {
        bytes32 root = keccak256("root");
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.InsufficientSignatures.selector, 1, 2));
        reg.commitCheckpoint(0, 0, root, bytes32("blob"), 12, _cpSigs(0, 0, root, 12, 1));
    }

    function test_RevertWhen_DuplicateSigner() public {
        bytes32 root = keccak256("root");
        bytes[] memory s = _cpSigs(0, 0, root, 12, 2);
        s[1] = s[0];
        vm.expectRevert(TelemetryCheckpointRegistry.SignersNotSorted.selector);
        reg.commitCheckpoint(0, 0, root, bytes32("blob"), 12, s);
    }

    function test_RevertWhen_TamperedRoot() public {
        bytes[] memory s = _cpSigs(0, 0, keccak256("root"), 12, 2);
        vm.expectRevert(); // recovers to non-members
        reg.commitCheckpoint(0, 0, keccak256("other"), bytes32("blob"), 12, s);
    }

    function test_RevertWhen_CheckpointCommittedTwice() public {
        _commit(0, 0, keccak256("root"));
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.CheckpointAlreadyCommitted.selector, 0, 0));
        _commit(0, 0, keccak256("root2"));
    }

    function test_RevertWhen_IndexOutOfRange() public {
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.CheckpointIndexOutOfRange.selector, 24, 24));
        _commit(0, 24, keccak256("root"));
    }

    function test_LateCheckpointInsideFinalityWindowAccepted() public {
        vm.warp(genesis + EPOCH + FINALITY - 1); // epoch 0 over, finality window still open
        _commit(0, 23, keccak256("late"));
        assertEq(reg.checkpointCount(0), 1);
    }

    function test_CheckpointAtExactTelemetryDeadlineAccepted() public {
        // #158: checkpoints get the commit window beyond #72's finality deadline.
        vm.warp(genesis + EPOCH + FINALITY + COMMIT_WINDOW);
        assertEq(reg.telemetryDeadline(0), block.timestamp);
        _commit(0, 23, keccak256("on time"));
        assertGt(reg.getCheckpoint(0, 23).committedAt, 0);
    }

    function test_RevertWhen_CheckpointAfterFinality() public {
        _afterFinality(0);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.TelemetryDeadlineElapsed.selector, 0));
        _commit(0, 23, keccak256("late"));
    }

    function test_SampleInclusionProof() public {
        bytes32 a = keccak256("sampleA");
        bytes32 b = keccak256("sampleB");
        bytes32 root = a < b ? keccak256(abi.encodePacked(a, b)) : keccak256(abi.encodePacked(b, a));
        _commit(0, 0, root);
        bytes32[] memory proof = new bytes32[](1);
        proof[0] = b;
        assertTrue(reg.verifySampleInclusion(0, 0, address(0), a, proof));
        assertFalse(reg.verifySampleInclusion(0, 0, address(0), keccak256("forged"), proof));
        assertFalse(reg.verifySampleInclusion(0, 1, address(0), a, proof)); // uncommitted window
    }

    // --- self-checkpoint fallback (CP-04 / GS-07) ---

    function test_SelfCheckpointAfterGrace() public {
        nodes.set(node, INodeRegistry.NodeStatus.Active);
        vm.warp(reg.checkpointWindowEnd(0, 0) + 30 minutes);
        vm.prank(node);
        reg.selfCheckpoint(0, 0, keccak256("mine"), bytes32("blob"), 12);
        assertEq(reg.getSelfCheckpoint(0, 0, node).merkleRoot, keccak256("mine"));
    }

    function test_RevertWhen_SelfCheckpointBeforeGrace() public {
        nodes.set(node, INodeRegistry.NodeStatus.Active);
        vm.warp(reg.checkpointWindowEnd(0, 0) + 30 minutes - 1);
        vm.prank(node);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.SelfCheckpointNotYetAllowed.selector, 0, 0));
        reg.selfCheckpoint(0, 0, keccak256("mine"), bytes32("blob"), 12);
    }

    function test_RevertWhen_SelfCheckpointWhenCommitteeCommitted() public {
        nodes.set(node, INodeRegistry.NodeStatus.Active);
        _commit(0, 0, keccak256("root"));
        vm.warp(reg.checkpointWindowEnd(0, 0) + 1 hours);
        vm.prank(node);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.CheckpointAlreadyCommitted.selector, 0, 0));
        reg.selfCheckpoint(0, 0, keccak256("mine"), bytes32("blob"), 12);
    }

    function test_RevertWhen_InactiveNodeSelfCheckpoints() public {
        vm.warp(reg.checkpointWindowEnd(0, 0) + 1 hours);
        vm.prank(node);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.NotActiveNode.selector, node));
        reg.selfCheckpoint(0, 0, keccak256("mine"), bytes32("blob"), 12);
    }

    // --- aggregated records (HC-*, EL-*, DH-*) ---

    function test_FullComplianceRecord() public {
        _afterFinality(0);
        _submit(0, _one(_input(288, 288)));
        TelemetryCheckpointRegistry.AggregatedMetricRecord memory r = reg.getEpochMetricRecord(node, 0);
        assertTrue(r.exists);
        assertTrue(r.eligible);
        assertEq(r.heartbeatComplianceBps, 10_000);
        assertFalse(r.divergenceFlag);
    }

    function test_MissedHeartbeatsCompliance() public {
        _afterFinality(0);
        _submit(0, _one(_input(259, 288))); // 29 missed -> 89.93%
        assertEq(reg.getEpochMetricRecord(node, 0).heartbeatComplianceBps, 8_993);
    }

    function test_ZeroHeartbeatsIneligible() public {
        _afterFinality(0);
        _submit(0, _one(_input(0, 288)));
        TelemetryCheckpointRegistry.AggregatedMetricRecord memory r = reg.getEpochMetricRecord(node, 0);
        assertTrue(r.exists);
        assertFalse(r.eligible);
        assertEq(r.heartbeatComplianceBps, 0);
    }

    function test_NoRecordMeansNotExists() public view {
        assertFalse(reg.getEpochMetricRecord(node, 5).exists);
    }

    function test_DivergenceFlaggedOnUptimeGap() public {
        TelemetryCheckpointRegistry.AggregatedMetricInput memory r = _input(288, 288);
        r.attestationCount = 3;
        r.verifierAttested.uptimeBps = 8_000; // self says 99%, verifiers 80%
        r.verifierAttested.latencyMs = 50;
        _afterFinality(0);
        _submit(0, _one(r));
        TelemetryCheckpointRegistry.AggregatedMetricRecord memory rec = reg.getEpochMetricRecord(node, 0);
        assertTrue(rec.divergenceFlag);
        assertEq(rec.selfReported.uptimeBps, 9_900); // both kept, not merged
        assertEq(rec.verifierAttested.uptimeBps, 8_000);
    }

    function test_DivergenceFlaggedOnLatencyGap() public {
        TelemetryCheckpointRegistry.AggregatedMetricInput memory r = _input(288, 288);
        r.attestationCount = 1;
        r.verifierAttested.uptimeBps = 9_900;
        r.verifierAttested.latencyMs = 200; // self 50ms vs 200ms observed
        _afterFinality(0);
        _submit(0, _one(r));
        assertTrue(reg.getEpochMetricRecord(node, 0).divergenceFlag);
    }

    function test_NoDivergenceWithoutAttestations() public {
        TelemetryCheckpointRegistry.AggregatedMetricInput memory r = _input(288, 288);
        r.verifierAttested.uptimeBps = 0;
        _afterFinality(0);
        _submit(0, _one(r));
        assertFalse(reg.getEpochMetricRecord(node, 0).divergenceFlag);
    }

    function test_RevertWhen_RecordsBeforeTelemetryDeadline() public {
        vm.warp(genesis + EPOCH + FINALITY + COMMIT_WINDOW); // the deadline second itself is still the commit window
        TelemetryCheckpointRegistry.AggregatedMetricInput[] memory a = _one(_input(288, 288));
        bytes32 h = keccak256(abi.encode(a));
        bytes[] memory s = _sigs(keccak256(abi.encode(AGG_TYPEHASH, uint256(0), 0, 1, h)), 2);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.TelemetryDeadlineNotElapsed.selector, 0));
        reg.submitAggregatedRecords(0, 0, 1, a, s);
    }

    function test_RevertWhen_RecordWrittenTwice() public {
        // The same node in two different chunks of one epoch is rejected record by record.
        _afterFinality(0);
        _submitChunk(0, 0, 2, _one(_input(288, 288)));
        TelemetryCheckpointRegistry.AggregatedMetricInput[] memory a = _one(_input(100, 288));
        bytes32 h = keccak256(abi.encode(a));
        bytes[] memory s = _sigs(keccak256(abi.encode(AGG_TYPEHASH, uint256(0), 1, 2, h)), 2);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.RecordAlreadyWritten.selector, 0, node));
        reg.submitAggregatedRecords(0, 1, 2, a, s);
    }

    function test_RevertWhen_ReceivedExceedsExpected() public {
        _afterFinality(0);
        TelemetryCheckpointRegistry.AggregatedMetricInput[] memory a = _one(_input(289, 288));
        bytes32 h = keccak256(abi.encode(a));
        bytes[] memory s = _sigs(keccak256(abi.encode(AGG_TYPEHASH, uint256(0), 0, 1, h)), 2);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.InvalidRecord.selector, node));
        reg.submitAggregatedRecords(0, 0, 1, a, s);
    }

    function test_RevertWhen_RecordsSignedForOtherEpoch() public {
        _afterFinality(1);
        TelemetryCheckpointRegistry.AggregatedMetricInput[] memory a = _one(_input(288, 288));
        bytes32 h = keccak256(abi.encode(a));
        bytes[] memory s = _sigs(keccak256(abi.encode(AGG_TYPEHASH, uint256(1), 0, 1, h)), 2);
        vm.expectRevert();
        reg.submitAggregatedRecords(0, 0, 1, a, s);
    }

    function test_HashRecordsMatchesAbiEncoding() public view {
        TelemetryCheckpointRegistry.AggregatedMetricInput[] memory a = _one(_input(1, 2));
        assertEq(reg.hashRecords(a), keccak256(abi.encode(a)));
    }

    function testFuzz_ComplianceBps(uint32 received, uint32 expected) public {
        expected = uint32(bound(expected, 1, 10_000));
        received = uint32(bound(received, 0, expected));
        _afterFinality(0);
        _submit(0, _one(_input(received, expected)));
        TelemetryCheckpointRegistry.AggregatedMetricRecord memory r = reg.getEpochMetricRecord(node, 0);
        assertEq(r.heartbeatComplianceBps, uint256(received) * 10_000 / expected);
        assertEq(r.eligible, received > 0);
    }

    function test_PausedBlocksCommits() public {
        vm.prank(timelock);
        reg.pause();
        bytes32 root = keccak256("r");
        bytes[] memory s = _cpSigs(0, 0, root, 12, 2);
        vm.expectRevert();
        reg.commitCheckpoint(0, 0, root, bytes32("blob"), 12, s);
    }

    // --- #158: relayed self-checkpoints ---

    bytes32 internal constant SELF_TYPEHASH = keccak256(
        "SelfCheckpoint(address node,uint256 epochIndex,uint256 checkpointIndex,bytes32 merkleRoot,bytes32 blobAddress,uint32 sampleCount)"
    );

    function _selfSig(uint256 pk, address who, bytes32 root) internal view returns (bytes memory) {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19\x01", _domain(), keccak256(abi.encode(SELF_TYPEHASH, who, uint256(0), uint256(0), root, bytes32("blob"), uint32(12)))
            )
        );
        (uint8 v, bytes32 r, bytes32 ss) = vm.sign(pk, digest);
        return abi.encodePacked(r, ss, v);
    }

    function test_SelfCheckpointForRelayedWithNodeSignature() public {
        uint256 pk = 0xBEEF;
        address signer = vm.addr(pk);
        nodes.set(signer, INodeRegistry.NodeStatus.Active);
        vm.warp(reg.checkpointWindowEnd(0, 0) + 30 minutes);
        vm.prank(makeAddr("relayer"));
        reg.selfCheckpointFor(signer, 0, 0, keccak256("mine"), bytes32("blob"), 12, _selfSig(pk, signer, keccak256("mine")));
        assertEq(reg.getSelfCheckpoint(0, 0, signer).merkleRoot, keccak256("mine"));
    }

    function test_RevertWhen_SelfCheckpointForSignedByAnotherKey() public {
        address victim = makeAddr("victim");
        nodes.set(victim, INodeRegistry.NodeStatus.Active);
        vm.warp(reg.checkpointWindowEnd(0, 0) + 30 minutes);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.InvalidNodeSignature.selector, victim));
        reg.selfCheckpointFor(victim, 0, 0, keccak256("mine"), bytes32("blob"), 12, _selfSig(0xBAD, victim, keccak256("mine")));
    }

    function test_RevertWhen_SelfCheckpointAfterTelemetryDeadline() public {
        nodes.set(node, INodeRegistry.NodeStatus.Active);
        _afterFinality(0);
        vm.prank(node);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.TelemetryDeadlineElapsed.selector, 0));
        reg.selfCheckpoint(0, 0, keccak256("mine"), bytes32("blob"), 12);
    }

    // --- #158: chunked records ---

    function _submitChunk(uint256 epoch, uint256 idx, uint256 count, TelemetryCheckpointRegistry.AggregatedMetricInput[] memory inputs)
        internal
    {
        bytes32 h = keccak256(abi.encode(inputs));
        reg.submitAggregatedRecords(epoch, idx, count, inputs, _sigs(keccak256(abi.encode(AGG_TYPEHASH, epoch, idx, count, h)), 2));
    }

    function test_RecordsFinalizeWhenEveryChunkLands() public {
        _afterFinality(0);
        TelemetryCheckpointRegistry.AggregatedMetricInput memory b = _input(100, 288);
        b.node = makeAddr("nodeB");
        _submitChunk(0, 1, 2, _one(b));
        assertFalse(reg.recordsFinalized(0));
        _submitChunk(0, 0, 2, _one(_input(288, 288)));
        assertTrue(reg.recordsFinalized(0));
        assertEq(reg.recordsFinalizedAt(0), block.timestamp);
        assertTrue(reg.getEpochMetricRecord(b.node, 0).exists);
    }

    function test_EmptyEpochFinalizedByOneEmptyChunk() public {
        _afterFinality(0);
        _submitChunk(0, 0, 1, new TelemetryCheckpointRegistry.AggregatedMetricInput[](0));
        assertTrue(reg.recordsFinalized(0));
    }

    function test_RevertWhen_ChunkResubmittedOrCountChanges() public {
        _afterFinality(0);
        _submitChunk(0, 0, 2, new TelemetryCheckpointRegistry.AggregatedMetricInput[](0));
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.ChunkAlreadyWritten.selector, 0, 0));
        _submitChunk(0, 0, 2, new TelemetryCheckpointRegistry.AggregatedMetricInput[](0));
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.InvalidChunk.selector, 1, 3));
        _submitChunk(0, 1, 3, new TelemetryCheckpointRegistry.AggregatedMetricInput[](0));
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.InvalidChunk.selector, 2, 2));
        _submitChunk(0, 2, 2, new TelemetryCheckpointRegistry.AggregatedMetricInput[](0));
    }

    function test_RevertWhen_ChunkExceedsRecordsPerTx() public {
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.TELEMETRY_RECORDS_PER_TX, 1);
        _afterFinality(0);
        TelemetryCheckpointRegistry.AggregatedMetricInput[] memory two = new TelemetryCheckpointRegistry.AggregatedMetricInput[](2);
        two[0] = _input(1, 288);
        two[1] = _input(1, 288);
        vm.expectRevert(abi.encodeWithSelector(TelemetryCheckpointRegistry.TooManyRecords.selector, 2, 1));
        _submitChunk(0, 0, 1, two);
    }
}
