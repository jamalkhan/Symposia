// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {IBlobDeals} from "./interfaces/IBlobDeals.sol";

/// @title BlobDeals
/// @notice Implements FR-3 of issue #52: records blob deal terms, exposes
/// replica/region data to the reward and slashing contracts, and enforces
/// config-supplied pricing/timing rather than literals. The prepaid credit
/// ledger and full non-payment billing state machine live in
/// `retention-and-billing.md` / a separate billing system (out of scope);
/// this contract only tracks a simple deductible credit balance per tenant
/// as the enforcement boundary, and consumes payment-status transitions
/// from an authorized billing role rather than deciding them itself.
contract BlobDeals is GovernedUpgradeable, IBlobDeals {
    enum PaymentStatus {
        Current,
        Grace,
        SoftSuspended,
        SoftDeleted,
        HardDeleted
    }

    struct DealRecord {
        bytes32 cid;
        uint256 size;
        bytes32 region;
        address[] replicas;
        uint256 startEpoch;
        uint256 termLength;
        uint8 tier;
        PaymentStatus paymentStatus;
        uint256 paymentStatusChangedAt;
        address tenant;
        bool exists;
    }

    /// @custom:storage-location erc7201:symposia.BlobDeals
    struct DealsStorage {
        mapping(bytes32 => DealRecord) deals;
        mapping(address => uint256) creditBalance;
    }

    bytes32 private constant DEALS_STORAGE_LOCATION = keccak256("symposia.storage.BlobDeals");

    event DealCreated(
        bytes32 indexed dealId, address indexed tenant, bytes32 cid, uint256 size, bytes32 region, uint256 startEpoch
    );
    event DealModified(bytes32 indexed dealId, address[] newReplicas);
    event PaymentStatusChanged(bytes32 indexed dealId, PaymentStatus previous, PaymentStatus next);
    event CreditChanged(address indexed tenant, uint256 previousBalance, uint256 newBalance);

    error DealAlreadyExists(bytes32 dealId);
    error DealNotFound(bytes32 dealId);
    error InsufficientCredit(uint256 available, uint256 required);
    error NotReplicationRole(address caller);
    error NotBillingRole(address caller);
    error InvalidStatusTransition(PaymentStatus current, PaymentStatus attempted);

    function _dealsStorage() private pure returns (DealsStorage storage $) {
        bytes32 slot = DEALS_STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    function initialize(IProtocolConfig initialConfig) external initializer {
        __GovernedUpgradeable_init(initialConfig);
    }

    modifier onlyReplicationRole() {
        if (msg.sender != config().getAddress(ConfigKeys.DEALS_REPLICATION_ROLE)) revert NotReplicationRole(msg.sender);
        _;
    }

    modifier onlyBillingRole() {
        if (msg.sender != config().getAddress(ConfigKeys.DEALS_BILLING_ROLE)) revert NotBillingRole(msg.sender);
        _;
    }

    // --- Credit (thin enforcement boundary; full ledger is out of scope) ---

    function creditBalanceOf(address tenant) external view returns (uint256) {
        return _dealsStorage().creditBalance[tenant];
    }

    /// @notice Authorized billing role credits a tenant's prepaid balance.
    function addCredit(address tenant, uint256 amount) external onlyBillingRole {
        uint256 previous = _dealsStorage().creditBalance[tenant];
        _dealsStorage().creditBalance[tenant] = previous + amount;
        emit CreditChanged(tenant, previous, previous + amount);
    }

    /// @notice Quotes the prepaid cost for storing `size` bytes for
    /// `epochs` epochs at the current config-supplied per-byte-per-epoch
    /// rate (FR-3 — never a literal in this contract).
    function quoteStorageFee(uint256 size, uint256 epochs) public view returns (uint256) {
        uint256 pricePerByteEpoch = config().getUint(ConfigKeys.DEALS_PRICE_PER_BYTE_EPOCH);
        return size * epochs * pricePerByteEpoch;
    }

    function egressFee(uint256 gigabytes) public view returns (uint256) {
        uint256 pricePerGb = config().getUint(ConfigKeys.DEALS_PRICE_EGRESS_PER_GB);
        return gigabytes * pricePerGb;
    }

    // --- Deal lifecycle ---

    function createDeal(
        bytes32 dealId,
        bytes32 cid,
        uint256 size,
        bytes32 region,
        address[] calldata replicas,
        uint256 startEpoch,
        uint256 termLength,
        uint8 tier
    ) external whenNotPaused {
        DealsStorage storage $ = _dealsStorage();
        if ($.deals[dealId].exists) revert DealAlreadyExists(dealId);

        uint256 cost = quoteStorageFee(size, termLength);
        uint256 balance = $.creditBalance[msg.sender];
        if (balance < cost) revert InsufficientCredit(balance, cost);
        $.creditBalance[msg.sender] = balance - cost;
        emit CreditChanged(msg.sender, balance, balance - cost);

        DealRecord storage rec = $.deals[dealId];
        rec.cid = cid;
        rec.size = size;
        rec.region = region;
        rec.replicas = replicas;
        rec.startEpoch = startEpoch;
        rec.termLength = termLength;
        rec.tier = tier;
        rec.paymentStatus = PaymentStatus.Current;
        rec.paymentStatusChangedAt = block.timestamp;
        rec.tenant = msg.sender;
        rec.exists = true;

        emit DealCreated(dealId, msg.sender, cid, size, region, startEpoch);
    }

    function dealExists(bytes32 dealId) public view returns (bool) {
        return _dealsStorage().deals[dealId].exists;
    }

    function replicasOf(bytes32 dealId) external view returns (address[] memory) {
        DealRecord storage rec = _dealsStorage().deals[dealId];
        if (!rec.exists) revert DealNotFound(dealId);
        return rec.replicas;
    }

    function regionOf(bytes32 dealId) external view returns (bytes32) {
        DealRecord storage rec = _dealsStorage().deals[dealId];
        if (!rec.exists) revert DealNotFound(dealId);
        return rec.region;
    }

    function paymentStatusOf(bytes32 dealId) external view returns (PaymentStatus) {
        DealRecord storage rec = _dealsStorage().deals[dealId];
        if (!rec.exists) revert DealNotFound(dealId);
        return rec.paymentStatus;
    }

    /// @notice Replaces the replica list, e.g. after re-replication
    /// following a node fault. Restricted to the authorized re-replication
    /// role (network-operated, itself config-supplied — FR-3).
    function modifyReplicas(bytes32 dealId, address[] calldata newReplicas) external onlyReplicationRole whenNotPaused {
        DealRecord storage rec = _dealsStorage().deals[dealId];
        if (!rec.exists) revert DealNotFound(dealId);
        rec.replicas = newReplicas;
        emit DealModified(dealId, newReplicas);
    }

    /// @notice Advances a deal's payment status. The contract does not
    /// decide payment status itself — it consumes a status input from the
    /// billing system's authorized role — but it does enforce that
    /// transitions move strictly one step forward through the documented
    /// grace -> soft-suspend -> soft-delete -> hard-delete schedule and are
    /// gated by the config-supplied minimum duration having elapsed since
    /// the last transition, rather than trusting an arbitrary jump (e.g.
    /// hard-delete with no prior soft-delete is rejected).
    function advancePaymentStatus(bytes32 dealId, PaymentStatus next) external onlyBillingRole whenNotPaused {
        DealRecord storage rec = _dealsStorage().deals[dealId];
        if (!rec.exists) revert DealNotFound(dealId);

        PaymentStatus current = rec.paymentStatus;
        if (uint8(next) != uint8(current) + 1) revert InvalidStatusTransition(current, next);

        uint256 minDuration = _minDurationFor(next);
        require(block.timestamp >= rec.paymentStatusChangedAt + minDuration, "BlobDeals: duration not elapsed");

        rec.paymentStatus = next;
        rec.paymentStatusChangedAt = block.timestamp;
        emit PaymentStatusChanged(dealId, current, next);
    }

    /// @notice A tenant back in good standing may be reset directly to
    /// Current by the billing role (e.g. after paying down an outstanding
    /// balance) without walking back through the forward-only ladder.
    function restoreToCurrent(bytes32 dealId) external onlyBillingRole whenNotPaused {
        DealRecord storage rec = _dealsStorage().deals[dealId];
        if (!rec.exists) revert DealNotFound(dealId);
        PaymentStatus current = rec.paymentStatus;
        rec.paymentStatus = PaymentStatus.Current;
        rec.paymentStatusChangedAt = block.timestamp;
        emit PaymentStatusChanged(dealId, current, PaymentStatus.Current);
    }

    function _minDurationFor(PaymentStatus status) private view returns (uint256) {
        if (status == PaymentStatus.Grace) return config().getUint(ConfigKeys.DEALS_GRACE_DURATION);
        if (status == PaymentStatus.SoftSuspended) return config().getUint(ConfigKeys.DEALS_SOFT_SUSPEND_DURATION);
        if (status == PaymentStatus.SoftDeleted) return config().getUint(ConfigKeys.DEALS_SOFT_DELETE_DURATION);
        if (status == PaymentStatus.HardDeleted) return config().getUint(ConfigKeys.DEALS_HARD_DELETE_DURATION);
        return 0;
    }
}
