// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title ProtocolActivations
/// @notice The network's schedule of protocol cutovers (issue #145). A release first
/// *offers* a protocol feature (nodes advertise it in /v1/node/services); governance then
/// schedules the moment it becomes *required*, after which gateways stop routing to nodes
/// that do not advertise it. Recording that moment here, instead of in each gateway's
/// configuration, gives every operator one public deadline and every gateway the same rule.
///
/// Operators must get notice: an activation is at least `minNotice` seconds away when
/// scheduled, can be postponed or cancelled only before it takes effect, is never moved
/// earlier, and is permanent once in effect. The minimum protocol version only rises.
///
/// Non-upgradeable by design, matching NodeRegistry: a successor is a new deployment
/// that gateways are pointed at. The owner is the foundation key until governance (#54).
contract ProtocolActivations {
    struct Feature {
        string name;
        uint64 activatesAt;
    }

    address public owner;
    uint64 public immutable minNotice;

    Feature[] private _features;
    mapping(bytes32 => uint256) private _indexPlusOne;

    uint32 private _effectiveVersion;
    uint32 private _pendingVersion;
    uint64 private _pendingVersionAt;

    event FeatureScheduled(string name, uint64 activatesAt);
    event FeatureCancelled(string name);
    event MinProtocolVersionScheduled(uint32 version, uint64 activatesAt);
    event OwnershipTransferred(address indexed previousOwner, address indexed newOwner);

    error NotOwner();
    error InsufficientNotice(uint64 earliest);
    error AlreadyActive();
    error EarlierThanScheduled(uint64 scheduled);
    error NotScheduled();
    error VersionNotHigher(uint32 current);
    error EmptyName();
    error ZeroOwner();

    modifier onlyOwner() {
        if (msg.sender != owner) revert NotOwner();
        _;
    }

    constructor(address owner_, uint64 minNotice_) {
        if (owner_ == address(0)) revert ZeroOwner();
        owner = owner_;
        minNotice = minNotice_;
        emit OwnershipTransferred(address(0), owner_);
    }

    /// @notice Schedules (or postpones) the time `name` becomes required.
    function schedule(string calldata name, uint64 activatesAt) external onlyOwner {
        if (bytes(name).length == 0) revert EmptyName();
        _requireNotice(activatesAt);

        bytes32 key = keccak256(bytes(name));
        uint256 slot = _indexPlusOne[key];
        if (slot == 0) {
            _features.push(Feature(name, activatesAt));
            _indexPlusOne[key] = _features.length;
        } else {
            Feature storage f = _features[slot - 1];
            if (_inEffect(f.activatesAt)) revert AlreadyActive();
            if (f.activatesAt != 0 && activatesAt < f.activatesAt) revert EarlierThanScheduled(f.activatesAt);
            f.activatesAt = activatesAt;
        }

        emit FeatureScheduled(name, activatesAt);
    }

    /// @notice Withdraws a scheduled activation that has not taken effect. The feature stays
    /// listed (with time 0) as a record.
    function cancel(string calldata name) external onlyOwner {
        uint256 slot = _indexPlusOne[keccak256(bytes(name))];
        if (slot == 0 || _features[slot - 1].activatesAt == 0) revert NotScheduled();
        Feature storage f = _features[slot - 1];
        if (_inEffect(f.activatesAt)) revert AlreadyActive();
        f.activatesAt = 0;
        emit FeatureCancelled(name);
    }

    /// @notice Schedules a rise of the minimum protocol version nodes must advertise. Replaces a
    /// pending rise that has not taken effect, with an equal or higher version, never earlier.
    function scheduleMinProtocolVersion(uint32 version, uint64 activatesAt) external onlyOwner {
        _settleVersion();
        if (version <= _effectiveVersion) revert VersionNotHigher(_effectiveVersion);
        _requireNotice(activatesAt);
        if (_pendingVersion != 0) {
            if (version < _pendingVersion) revert VersionNotHigher(_pendingVersion);
            // Never earlier, even for a higher version: operators were promised the pending deadline.
            if (activatesAt < _pendingVersionAt) revert EarlierThanScheduled(_pendingVersionAt);
        }

        _pendingVersion = version;
        _pendingVersionAt = activatesAt;
        emit MinProtocolVersionScheduled(version, activatesAt);
    }

    function transferOwnership(address newOwner) external onlyOwner {
        if (newOwner == address(0)) revert ZeroOwner();
        emit OwnershipTransferred(owner, newOwner);
        owner = newOwner;
    }

    /// @notice When `name` becomes (or became) required; 0 if not scheduled.
    function activationOf(string calldata name) external view returns (uint64) {
        uint256 slot = _indexPlusOne[keccak256(bytes(name))];
        return slot == 0 ? 0 : _features[slot - 1].activatesAt;
    }

    function featureCount() external view returns (uint256) {
        return _features.length;
    }

    function featureAt(uint256 index) external view returns (string memory name, uint64 activatesAt) {
        Feature storage f = _features[index];
        return (f.name, f.activatesAt);
    }

    /// @notice The minimum protocol version in effect now, and any scheduled rise.
    function minProtocolVersion() external view returns (uint32 effective, uint32 pending, uint64 pendingAt) {
        if (_pendingVersion != 0 && _inEffect(_pendingVersionAt)) {
            return (_pendingVersion, 0, 0);
        }

        return (_effectiveVersion, _pendingVersion, _pendingVersionAt);
    }

    function _settleVersion() private {
        if (_pendingVersion != 0 && _inEffect(_pendingVersionAt)) {
            _effectiveVersion = _pendingVersion;
            _pendingVersion = 0;
            _pendingVersionAt = 0;
        }
    }

    function _requireNotice(uint64 activatesAt) private view {
        uint64 earliest = uint64(block.timestamp) + minNotice;
        if (activatesAt < earliest) revert InsufficientNotice(earliest);
    }

    function _inEffect(uint64 activatesAt) private view returns (bool) {
        return activatesAt != 0 && activatesAt <= block.timestamp;
    }
}
