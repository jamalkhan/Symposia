// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {StakingNodeRegistry} from "../src/StakingNodeRegistry.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

/// @notice Issue #159: a Node runs 0, 1 or many Node Services, each staked,
/// penalized and rewarded on its own.
contract NodeServicesTest is CoreProtocolTestBase {
    address internal node = makeAddr("node");
    address internal storagePsm = makeAddr("storagePsm");
    address internal computePsm = makeAddr("computePsm");

    INodeRegistry.NodeType internal constant STORAGE = INodeRegistry.NodeType.Storage;
    INodeRegistry.NodeType internal constant OLTP = INodeRegistry.NodeType.OLTP;
    INodeRegistry.NodeType internal constant VERIFIER = INodeRegistry.NodeType.Verifier;
    INodeRegistry.NodeType internal constant EMAIL = INodeRegistry.NodeType.EmailIP;
    INodeRegistry.NodeType internal constant COMPUTE = INodeRegistry.NodeType.Compute;

    uint256 internal constant STORAGE_STAKE = 25_000e18;
    uint256 internal constant VERIFIER_STAKE = 125_000e18;

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 1_000_000_000e18);
        vm.startPrank(configOwner);
        cfg.setUint(ConfigKeys.registryStakeBase(0), STORAGE_STAKE);
        cfg.setUint(ConfigKeys.registryStakePerUnit(0), 0);
        cfg.setUint(ConfigKeys.registryStakeBase(1), 10_000e18);
        cfg.setUint(ConfigKeys.registryStakeBase(5), VERIFIER_STAKE);
        cfg.setUint(ConfigKeys.registryStakeBase(7), 1_000e18);
        cfg.setAddress(ConfigKeys.STORAGE_PENALTY_STATE_MACHINE_ADDRESS, storagePsm);
        cfg.setAddress(ConfigKeys.COMPUTE_PENALTY_STATE_MACHINE_ADDRESS, computePsm);
        vm.stopPrank();
        _fund(node, 1_000_000e18);
    }

    function _registerService(INodeRegistry.NodeType s, uint256 stake) internal {
        vm.prank(node);
        registry.registerService(s, 0, "us-east", stake);
    }

    function _storageAndVerifier() internal {
        _registerService(STORAGE, STORAGE_STAKE);
        _registerService(VERIFIER, VERIFIER_STAKE);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    // --- Registry: REG-* ---

    function test_TwoServicesEachWithOwnStake() public {
        _registerService(STORAGE, STORAGE_STAKE);
        _registerService(VERIFIER, VERIFIER_STAKE);

        INodeRegistry.NodeType[] memory list = registry.servicesOf(node);
        assertEq(list.length, 2);
        assertEq(uint8(list[0]), uint8(STORAGE));
        assertEq(uint8(list[1]), uint8(VERIFIER));
        assertEq(registry.totalStakeOf(node), STORAGE_STAKE + VERIFIER_STAKE);
        assertEq(registry.stakeOf(node), STORAGE_STAKE + VERIFIER_STAKE);
        assertEq(registry.serviceStakeOf(node, VERIFIER), VERIFIER_STAKE);
        assertEq(uint8(registry.typeOf(node)), uint8(STORAGE), "primary is the first registered");
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.PendingVerification));
        assertEq(token.balanceOf(address(registry)), STORAGE_STAKE + VERIFIER_STAKE);
    }

    function test_RevertWhen_ServiceRegisteredTwice() public {
        _registerService(STORAGE, STORAGE_STAKE);
        _registerService(VERIFIER, VERIFIER_STAKE);
        vm.expectRevert(abi.encodeWithSelector(StakingNodeRegistry.ServiceAlreadyRegistered.selector, node, VERIFIER));
        _registerService(VERIFIER, VERIFIER_STAKE);
    }

    function test_RevertWhen_SecondServiceUnderItsOwnMinimum() public {
        _registerService(STORAGE, STORAGE_STAKE);
        vm.expectRevert(abi.encodeWithSelector(StakingNodeRegistry.InsufficientStake.selector, 100_000e18, VERIFIER_STAKE));
        _registerService(VERIFIER, 100_000e18);
    }

    function test_RegionVerificationAppliesToEveryService() public {
        _storageAndVerifier();
        assertEq(uint8(registry.serviceStatusOf(node, STORAGE)), uint8(INodeRegistry.NodeStatus.Active));
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Active));
    }

    function test_ServiceVerificationTargetsOneService() public {
        _registerService(STORAGE, STORAGE_STAKE);
        _registerService(VERIFIER, VERIFIER_STAKE);
        vm.prank(verifierRole);
        registry.recordServiceVerification(node, VERIFIER, true, 0);
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Active));
        assertEq(uint8(registry.serviceStatusOf(node, STORAGE)), uint8(INodeRegistry.NodeStatus.PendingVerification));
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Active), "any Active service");
    }

    function test_UnstakingOneServiceLeavesTheOtherAlone() public {
        _storageAndVerifier();
        uint256 before = token.balanceOf(node);

        vm.prank(node);
        registry.requestServiceUnstake(VERIFIER, VERIFIER_STAKE);
        assertEq(uint8(registry.serviceStatusOf(node, STORAGE)), uint8(INodeRegistry.NodeStatus.Active));
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Active));

        vm.warp(block.timestamp + 21 days);
        vm.prank(node);
        registry.withdrawServiceUnstake(VERIFIER);

        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Unregistered));
        assertEq(registry.serviceStakeOf(node, STORAGE), STORAGE_STAKE);
        assertEq(token.balanceOf(node), before + VERIFIER_STAKE);
        assertTrue(registry.hasPrimaryService(node));
    }

    function test_RevertWhen_PartialServiceUnstakeBelowItsMinimum() public {
        _storageAndVerifier();
        vm.prank(node);
        vm.expectRevert(
            abi.encodeWithSelector(StakingNodeRegistry.RemainingBelowMinimum.selector, VERIFIER_STAKE - 1e18, VERIFIER_STAKE)
        );
        registry.requestServiceUnstake(VERIFIER, 1e18);
    }

    function test_TopUpServiceRaisesOnlyThatService() public {
        _storageAndVerifier();
        address funder = makeAddr("funder");
        _fund(funder, 5e18);
        vm.prank(funder);
        registry.topUpService(node, VERIFIER, 5e18);
        assertEq(registry.serviceStakeOf(node, VERIFIER), VERIFIER_STAKE + 5e18);
        assertEq(registry.serviceStakeOf(node, STORAGE), STORAGE_STAKE);
    }

    function test_PrimaryStaysUntilNodeRunsNothing() public {
        _storageAndVerifier();
        uint256 t0 = block.timestamp;
        vm.startPrank(node);
        registry.requestServiceUnstake(STORAGE, STORAGE_STAKE);
        vm.warp(t0 + 21 days);
        registry.withdrawServiceUnstake(STORAGE);
        assertEq(uint8(registry.typeOf(node)), uint8(STORAGE), "primary fixed while Verifier remains");

        registry.requestServiceUnstake(VERIFIER, VERIFIER_STAKE);
        vm.warp(t0 + 42 days);
        registry.withdrawServiceUnstake(VERIFIER);
        vm.stopPrank();
        assertFalse(registry.hasPrimaryService(node));
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Unregistered));

        // Re-registering makes the new first service primary and reuses the history entry.
        _registerService(VERIFIER, VERIFIER_STAKE);
        assertEq(uint8(registry.typeOf(node)), uint8(VERIFIER));
        assertEq(registry.servicesOf(node).length, 2);
    }

    function test_EmailIpRegistersWithZeroStake() public {
        _registerService(EMAIL, 0);
        assertEq(uint8(registry.serviceStatusOf(node, EMAIL)), uint8(INodeRegistry.NodeStatus.PendingVerification));
        assertEq(registry.totalStakeOf(node), 0);
    }

    /// @dev Writes a pre-#159 single-type record straight into the legacy `records` mapping.
    function _writeLegacy(address who, INodeRegistry.NodeType t, uint256 stake, INodeRegistry.NodeStatus st) internal {
        bytes32 base = keccak256("symposia.storage.StakingNodeRegistry");
        uint256 slot = uint256(keccak256(abi.encode(who, base)));
        vm.store(address(registry), bytes32(slot), bytes32(uint256(uint8(t))));
        vm.store(address(registry), bytes32(slot + 3), bytes32(stake));
        vm.store(address(registry), bytes32(slot + 4), bytes32(uint256(uint8(st))));
    }

    function test_LegacyRecordReadsBeforeAndAfterMigration() public {
        _writeLegacy(node, VERIFIER, VERIFIER_STAKE, INodeRegistry.NodeStatus.Active);
        assertEq(registry.stakeOf(node), VERIFIER_STAKE, "visible before migration");
        assertEq(uint8(registry.typeOf(node)), uint8(VERIFIER));
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Active));

        registry.migrateLegacy(node);
        registry.migrateLegacy(node); // idempotent
        assertEq(registry.servicesOf(node).length, 1);
        assertEq(registry.serviceStakeOf(node, VERIFIER), VERIFIER_STAKE);

        // A write path works on the migrated record, and new services join it.
        _registerService(STORAGE, STORAGE_STAKE);
        assertEq(registry.totalStakeOf(node), VERIFIER_STAKE + STORAGE_STAKE);
        assertEq(uint8(registry.typeOf(node)), uint8(VERIFIER), "legacy type stays primary");
    }

    function test_LegacyRecordAutoMigratesOnWrite() public {
        _writeLegacy(node, STORAGE, STORAGE_STAKE, INodeRegistry.NodeStatus.Active);
        _registerService(VERIFIER, VERIFIER_STAKE);
        assertEq(registry.servicesOf(node).length, 2);
        assertEq(registry.serviceStakeOf(node, STORAGE), STORAGE_STAKE);
    }

    // --- Penalties: SLASH-* ---

    function test_StoragePenaltySlashHitsOnlyStorage() public {
        _storageAndVerifier();
        vm.prank(storagePsm);
        uint256 applied = slashing.applyStoragePenaltySlash(node, 1_000e18);
        assertEq(applied, 1_000e18);
        assertEq(registry.serviceStakeOf(node, STORAGE), STORAGE_STAKE - 1_000e18);
        assertEq(registry.serviceStakeOf(node, VERIFIER), VERIFIER_STAKE);
    }

    function test_StoragePenaltySlashCappedAtStorageStake() public {
        _storageAndVerifier();
        vm.prank(storagePsm);
        uint256 applied = slashing.applyStoragePenaltySlash(node, STORAGE_STAKE * 2);
        assertEq(applied, STORAGE_STAKE);
        assertEq(registry.serviceStakeOf(node, VERIFIER), VERIFIER_STAKE);
    }

    function test_StorageForceDeregisterLeavesVerifierActive() public {
        _storageAndVerifier();
        vm.prank(storagePsm);
        slashing.forceDeregisterStorageNode(node);
        assertEq(uint8(registry.serviceStatusOf(node, STORAGE)), uint8(INodeRegistry.NodeStatus.Banned));
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Active));
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Active));
    }

    function test_ComputeForceDeregisterBansOnlyCompute() public {
        _registerService(STORAGE, STORAGE_STAKE);
        _registerService(COMPUTE, 1_000e18);
        vm.prank(computePsm);
        registry.forceDeregister(node);
        assertEq(uint8(registry.serviceStatusOf(node, COMPUTE)), uint8(INodeRegistry.NodeStatus.Banned));
        assertEq(uint8(registry.serviceStatusOf(node, STORAGE)), uint8(INodeRegistry.NodeStatus.PendingVerification));
    }

    function test_NodeLevelSlashSplitsProRata() public {
        _storageAndVerifier();
        uint256 total = STORAGE_STAKE + VERIFIER_STAKE;
        vm.prank(address(slashing));
        registry.applySlash(node, 1_000e18, address(0));
        uint256 storageCut = STORAGE_STAKE - registry.serviceStakeOf(node, STORAGE);
        uint256 verifierCut = VERIFIER_STAKE - registry.serviceStakeOf(node, VERIFIER);
        assertEq(storageCut + verifierCut, 1_000e18, "exact total");
        assertEq(storageCut, (1_000e18 * STORAGE_STAKE) / total);
        assertEq(registry.totalStakeOf(node), total - 1_000e18);
    }

    function test_NodeLevelSlashCappedAtTotalStake() public {
        _storageAndVerifier();
        vm.prank(address(slashing));
        registry.applySlash(node, type(uint128).max, redistributionTarget);
        assertEq(registry.totalStakeOf(node), 0);
        assertEq(token.balanceOf(redistributionTarget), STORAGE_STAKE + VERIFIER_STAKE);
    }

    function test_NodeLevelBanBansEveryServiceAndBlocksNewOnes() public {
        _storageAndVerifier();
        vm.prank(address(slashing));
        registry.banNode(node, block.timestamp + 90 days);
        assertEq(uint8(registry.serviceStatusOf(node, STORAGE)), uint8(INodeRegistry.NodeStatus.Banned));
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Banned));
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Banned));

        vm.expectRevert(abi.encodeWithSelector(StakingNodeRegistry.StillBanned.selector, node, block.timestamp + 90 days));
        _registerService(OLTP, 10_000e18);
    }

    function test_ServiceOvercommitmentReportsTheNode() public {
        _storageAndVerifier();
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.registryStakeBase(5), VERIFIER_STAKE + 1);
        registry.checkServiceOvercommitment(node, VERIFIER);
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Banned));
    }

    function test_RevertWhen_ServiceNotOvercommitted() public {
        _storageAndVerifier();
        vm.expectRevert(abi.encodeWithSelector(StakingNodeRegistry.NoOvercommitment.selector, node));
        registry.checkServiceOvercommitment(node, VERIFIER);
    }

    // --- Rewards: RWD-* ---

    function _twoCapacityServices() internal {
        vm.startPrank(configOwner);
        cfg.setUint(ConfigKeys.rewardTypeWeight(0), 5_000);
        cfg.setUint(ConfigKeys.rewardTypeWeight(1), 5_000);
        vm.stopPrank();
        _registerService(STORAGE, STORAGE_STAKE);
        _registerService(OLTP, 10_000e18);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    function _signService(INodeRegistry.NodeType s, uint256 epoch, uint256 score, uint256 hb)
        internal
        view
        returns (bytes memory)
    {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(rewards), node, uint8(s), epoch, score, hb))
            )
        );
        (uint8 v, bytes32 r, bytes32 sig) = vm.sign(metricSignerPk, digest);
        return abi.encodePacked(r, sig, v);
    }

    function _seal(uint256 epoch) internal {
        address[] memory nodes = new address[](1);
        nodes[0] = node;
        vm.warp(rewards.epochFinalityDeadline(epoch) + 1);
        rewards.sealEpoch(epoch, nodes);
    }

    function test_AccountOfPrimaryIsTheNode() public {
        _twoCapacityServices();
        assertEq(rewards.accountOf(node, STORAGE), node);
        address oltpAccount = rewards.accountOf(node, OLTP);
        assertTrue(oltpAccount != node && oltpAccount != address(0));
    }

    function test_SecondaryServiceSettlesToItsAccountAndPaysTheNode() public {
        _twoCapacityServices();
        rewards.submitServiceMetricReport(node, STORAGE, 0, 100, 9_500, _signService(STORAGE, 0, 100, 9_500));
        rewards.submitServiceMetricReport(node, OLTP, 0, 100, 9_500, _signService(OLTP, 0, 100, 9_500));
        assertEq(rewards.reporterCount(0), 2, "one per service account");
        assertEq(rewards.reportingNodeCount(0), 1, "one node");

        uint256 before = token.balanceOf(node);
        _seal(0);

        uint256 pool = (1_000_000e18 * 9_200 / 10_000) * 5_000 / 10_000;
        address oltpAccount = rewards.accountOf(node, OLTP);
        assertEq(rewards.payoutOf(0, node), pool, "storage pool to the primary account");
        assertEq(rewards.payoutOf(0, oltpAccount), pool, "oltp pool to the derived account");
        assertEq(uint8(rewards.settlementOf(0, oltpAccount).reason), uint8(RewardDistributor.SettlementReason.Paid));
        assertEq(token.balanceOf(node), before + 2 * pool, "both paid to the node");
        assertEq(token.balanceOf(oltpAccount), 0, "derived accounts never hold tokens");
    }

    function test_EmitsServiceSettledPerService() public {
        _twoCapacityServices();
        rewards.submitServiceMetricReport(node, OLTP, 0, 100, 9_500, _signService(OLTP, 0, 100, 9_500));
        uint256 pool = (1_000_000e18 * 9_200 / 10_000) * 5_000 / 10_000;
        address oltpAccount = rewards.accountOf(node, OLTP);
        vm.expectEmit(true, true, true, true, address(rewards));
        emit RewardDistributor.ServiceSettled(
            node, STORAGE, 0, RewardDistributor.SettlementReason.Ineligible, node, 0, 0, 0
        );
        vm.expectEmit(true, true, true, true, address(rewards));
        emit RewardDistributor.ServiceSettled(
            node, OLTP, 0, RewardDistributor.SettlementReason.Paid, oltpAccount, pool, pool, 0
        );
        _seal(0);
    }

    function test_RevertWhen_ServiceReportReplayedForAnotherService() public {
        _twoCapacityServices();
        bytes memory sig = _signService(STORAGE, 0, 100, 9_500);
        vm.expectRevert(RewardDistributor.InvalidSignature.selector);
        rewards.submitServiceMetricReport(node, OLTP, 0, 100, 9_500, sig);
    }

    function test_SecondaryReporterMustBeCovered() public {
        _twoCapacityServices();
        rewards.submitServiceMetricReport(node, OLTP, 0, 100, 9_500, _signService(OLTP, 0, 100, 9_500));
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.IncompleteNodeSet.selector, 0, 0, 1));
        rewards.sealEpoch(0, new address[](0));
    }

    function test_EmailIpServiceNeverEarns() public {
        vm.startPrank(configOwner);
        cfg.setUint(ConfigKeys.rewardTypeWeight(6), 10_000); // even a configured weight is ignored
        vm.stopPrank();
        _registerService(EMAIL, 0);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
        rewards.submitServiceMetricReport(node, EMAIL, 0, 100, 9_500, _signService(EMAIL, 0, 100, 9_500));
        _seal(0);
        RewardDistributor.SettlementRecord memory r = rewards.settlementOf(0, node);
        assertEq(uint8(r.reason), uint8(RewardDistributor.SettlementReason.ExcludedType));
        assertEq(r.amount, 0);
    }

    function test_ServiceStakeBelowMinimumIsIneligible() public {
        _twoCapacityServices();
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.registryStakeBase(1), 10_000e18 + 1);
        rewards.submitServiceMetricReport(node, OLTP, 0, 100, 9_500, _signService(OLTP, 0, 100, 9_500));
        _seal(0);
        address oltpAccount = rewards.accountOf(node, OLTP);
        assertEq(uint8(rewards.settlementOf(0, oltpAccount).reason), uint8(RewardDistributor.SettlementReason.Ineligible));
    }

    function test_SecondaryReserveIsSeparateAndClaimable() public {
        _twoCapacityServices();
        // Non-compliant heartbeat: the OLTP payout is held in the OLTP account's reserve.
        rewards.submitServiceMetricReport(node, OLTP, 0, 100, 5_000, _signService(OLTP, 0, 100, 5_000));
        _seal(0);
        address oltpAccount = rewards.accountOf(node, OLTP);
        uint256 held = rewards.reserveBalanceOf(oltpAccount);
        assertGt(held, 0);
        assertEq(rewards.reserveBalanceOf(node), 0, "storage reserve untouched");

        vm.prank(node);
        vm.expectRevert(RewardDistributor.NoReserveToSweep.selector);
        rewards.claimServiceReserve(OLTP);
    }
}
