// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {BlobDeals} from "../src/BlobDeals.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

contract BlobDealsTest is CoreProtocolTestBase {
    address internal tenant = makeAddr("tenant");
    address internal replica1 = makeAddr("replica1");

    function setUp() public {
        _deployCoreProtocol();
    }

    function _grantCredit(uint256 amount) internal {
        vm.prank(billingRole);
        deals.addCredit(tenant, amount);
    }

    function test_createDeal_withSufficientCredit_succeeds() public {
        uint256 cost = deals.quoteStorageFee(1_000, 10);
        _grantCredit(cost);

        address[] memory replicas = new address[](1);
        replicas[0] = replica1;

        vm.prank(tenant);
        deals.createDeal(bytes32("deal1"), bytes32("cid1"), 1_000, "us-east", replicas, 1, 10, 0);

        assertTrue(deals.dealExists(bytes32("deal1")));
        assertEq(deals.replicasOf(bytes32("deal1")).length, 1);
        assertEq(deals.regionOf(bytes32("deal1")), bytes32("us-east"));
    }

    function test_createDeal_insufficientCredit_reverts() public {
        address[] memory replicas = new address[](1);
        replicas[0] = replica1;

        vm.prank(tenant);
        vm.expectRevert();
        deals.createDeal(bytes32("deal1"), bytes32("cid1"), 1_000, "us-east", replicas, 1, 10, 0);
    }

    function test_modifyReplicas_onlyReplicationRole() public {
        uint256 cost = deals.quoteStorageFee(1_000, 10);
        _grantCredit(cost);
        address[] memory replicas = new address[](1);
        replicas[0] = replica1;
        vm.prank(tenant);
        deals.createDeal(bytes32("deal1"), bytes32("cid1"), 1_000, "us-east", replicas, 1, 10, 0);

        address[] memory newReplicas = new address[](2);
        newReplicas[0] = replica1;
        newReplicas[1] = makeAddr("replica2");

        vm.prank(tenant);
        vm.expectRevert();
        deals.modifyReplicas(bytes32("deal1"), newReplicas);

        vm.prank(replicationRole);
        deals.modifyReplicas(bytes32("deal1"), newReplicas);
        assertEq(deals.replicasOf(bytes32("deal1")).length, 2);
    }

    function test_pricing_changesLiveFromConfig() public {
        uint256 before = deals.quoteStorageFee(1_000, 10);
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.DEALS_PRICE_PER_BYTE_EPOCH, 2);
        uint256 after_ = deals.quoteStorageFee(1_000, 10);
        assertEq(after_, before * 2);
    }

    function test_paymentStatus_nonPaymentSchedule_gatedByDuration() public {
        uint256 cost = deals.quoteStorageFee(1_000, 10);
        _grantCredit(cost);
        address[] memory replicas = new address[](1);
        replicas[0] = replica1;
        vm.prank(tenant);
        deals.createDeal(bytes32("deal1"), bytes32("cid1"), 1_000, "us-east", replicas, 1, 10, 0);

        vm.prank(billingRole);
        vm.expectRevert();
        deals.advancePaymentStatus(bytes32("deal1"), BlobDeals.PaymentStatus.Grace);

        vm.warp(block.timestamp + 7 days);
        vm.prank(billingRole);
        deals.advancePaymentStatus(bytes32("deal1"), BlobDeals.PaymentStatus.Grace);
        assertEq(uint8(deals.paymentStatusOf(bytes32("deal1"))), uint8(BlobDeals.PaymentStatus.Grace));
    }

    function test_paymentStatus_outOfOrderTransition_reverts() public {
        uint256 cost = deals.quoteStorageFee(1_000, 10);
        _grantCredit(cost);
        address[] memory replicas = new address[](1);
        replicas[0] = replica1;
        vm.prank(tenant);
        deals.createDeal(bytes32("deal1"), bytes32("cid1"), 1_000, "us-east", replicas, 1, 10, 0);

        vm.warp(block.timestamp + 30 days);
        vm.prank(billingRole);
        vm.expectRevert();
        deals.advancePaymentStatus(bytes32("deal1"), BlobDeals.PaymentStatus.HardDeleted);
    }
}
