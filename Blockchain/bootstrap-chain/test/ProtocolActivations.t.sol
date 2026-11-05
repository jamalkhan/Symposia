// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ProtocolActivations} from "../src/ProtocolActivations.sol";

/// @notice Issue #145 QA plan TC-1..TC-10.
contract ProtocolActivationsTest is Test {
    uint64 constant NOTICE = 7 days;
    address constant GOV = address(0xF00D);
    ProtocolActivations pa;

    function setUp() public {
        vm.warp(1_000_000);
        pa = new ProtocolActivations(GOV, NOTICE);
    }

    function _at(uint64 delta) private view returns (uint64) {
        return uint64(vm.getBlockTimestamp()) + delta;
    }

    function test_ownerSchedules_listsAndEmits() public {
        vm.expectEmit(false, false, false, true);
        emit ProtocolActivations.FeatureScheduled("storage.grpc.tls", _at(NOTICE));
        vm.prank(GOV);
        pa.schedule("storage.grpc.tls", _at(NOTICE));

        assertEq(pa.activationOf("storage.grpc.tls"), _at(NOTICE));
        assertEq(pa.featureCount(), 1);
        (string memory name, uint64 at) = pa.featureAt(0);
        assertEq(name, "storage.grpc.tls");
        assertEq(at, _at(NOTICE));
    }

    function test_nonOwner_reverts() public {
        vm.expectRevert(ProtocolActivations.NotOwner.selector);
        pa.schedule("x", _at(NOTICE));
    }

    function test_noticeBoundary() public {
        vm.startPrank(GOV);
        vm.expectRevert(abi.encodeWithSelector(ProtocolActivations.InsufficientNotice.selector, _at(NOTICE)));
        pa.schedule("x", _at(NOTICE - 1));
        pa.schedule("x", _at(NOTICE));
        vm.stopPrank();
    }

    function test_postponeAllowed_earlierRejected() public {
        vm.startPrank(GOV);
        pa.schedule("x", _at(NOTICE + 10));
        pa.schedule("x", _at(NOTICE + 20));
        vm.expectRevert(abi.encodeWithSelector(ProtocolActivations.EarlierThanScheduled.selector, _at(NOTICE + 20)));
        pa.schedule("x", _at(NOTICE + 15));
        vm.stopPrank();
    }

    function test_cancelBeforeActivation_keepsRecord() public {
        vm.startPrank(GOV);
        pa.schedule("x", _at(NOTICE));
        pa.cancel("x");
        vm.stopPrank();
        assertEq(pa.activationOf("x"), 0);
        assertEq(pa.featureCount(), 1);

        // Rescheduling after a cancel needs fresh notice, but isn't "earlier than" the cancelled time.
        vm.prank(GOV);
        pa.schedule("x", _at(NOTICE));
        assertEq(pa.activationOf("x"), _at(NOTICE));
    }

    function test_inEffect_isPermanent() public {
        vm.prank(GOV);
        pa.schedule("x", _at(NOTICE));
        vm.warp(vm.getBlockTimestamp() + NOTICE);

        vm.startPrank(GOV);
        vm.expectRevert(ProtocolActivations.AlreadyActive.selector);
        pa.schedule("x", _at(NOTICE * 2));
        vm.expectRevert(ProtocolActivations.AlreadyActive.selector);
        pa.cancel("x");
        vm.stopPrank();
    }

    function test_cancelUnscheduled_reverts() public {
        vm.prank(GOV);
        vm.expectRevert(ProtocolActivations.NotScheduled.selector);
        pa.cancel("nope");
    }

    function test_minVersion_onlyRises() public {
        vm.startPrank(GOV);
        pa.scheduleMinProtocolVersion(2, _at(NOTICE + 10));
        (uint32 effective, uint32 pending, uint64 pendingAt) = pa.minProtocolVersion();
        assertEq(effective, 0);
        assertEq(pending, 2);
        assertEq(pendingAt, _at(NOTICE + 10));

        // A pending rise may be replaced by a higher version, or the same version later, but not lowered or brought forward.
        vm.expectRevert(abi.encodeWithSelector(ProtocolActivations.VersionNotHigher.selector, 2));
        pa.scheduleMinProtocolVersion(1, _at(NOTICE + 10));
        vm.expectRevert(abi.encodeWithSelector(ProtocolActivations.EarlierThanScheduled.selector, _at(NOTICE + 10)));
        pa.scheduleMinProtocolVersion(2, _at(NOTICE + 5));
        pa.scheduleMinProtocolVersion(2, _at(NOTICE + 20));
        vm.stopPrank();

        vm.warp(vm.getBlockTimestamp() + NOTICE + 20);
        (effective, pending,) = pa.minProtocolVersion();
        assertEq(effective, 2);
        assertEq(pending, 0);

        vm.startPrank(GOV);
        vm.expectRevert(abi.encodeWithSelector(ProtocolActivations.VersionNotHigher.selector, 2));
        pa.scheduleMinProtocolVersion(2, _at(NOTICE));
        pa.scheduleMinProtocolVersion(3, _at(NOTICE));
        vm.stopPrank();
        (effective, pending,) = pa.minProtocolVersion();
        assertEq(effective, 2);
        assertEq(pending, 3);
    }

    function test_scheduleTwice_noDuplicate() public {
        vm.startPrank(GOV);
        pa.schedule("x", _at(NOTICE));
        pa.schedule("x", _at(NOTICE + 1));
        vm.stopPrank();
        assertEq(pa.featureCount(), 1);
    }

    function test_zeroNotice_allowsNow() public {
        ProtocolActivations dev = new ProtocolActivations(GOV, 0);
        vm.prank(GOV);
        dev.schedule("x", uint64(block.timestamp));
        assertEq(dev.activationOf("x"), block.timestamp);
    }

    function test_ownershipTransfer() public {
        address next = address(0xBEEF);
        vm.prank(GOV);
        pa.transferOwnership(next);
        vm.prank(GOV);
        vm.expectRevert(ProtocolActivations.NotOwner.selector);
        pa.schedule("x", _at(NOTICE));
        vm.prank(next);
        pa.schedule("x", _at(NOTICE));
    }

    function test_emptyName_reverts() public {
        vm.prank(GOV);
        vm.expectRevert(ProtocolActivations.EmptyName.selector);
        pa.schedule("", _at(NOTICE));
    }
}
