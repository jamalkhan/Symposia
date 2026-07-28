// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Initializable} from "@openzeppelin/contracts/proxy/utils/Initializable.sol";
import {UUPSUpgradeable} from "@openzeppelin/contracts/proxy/utils/UUPSUpgradeable.sol";
import {IProtocolConfig} from "../config/IProtocolConfig.sol";
import {ConfigKeys} from "../config/ConfigKeys.sol";

/// @title GovernedUpgradeable
/// @notice Shared base for the four core protocol contracts (issue #52):
/// NodeRegistry-equivalent staking registry, BlobDeals, RewardDistributor,
/// SlashingController. Implements the uniform governance/upgrade shape
/// described in the Arch pass:
///
/// - UUPS-upgradeable, with `_authorizeUpgrade` restricted to
///   `IProtocolConfig.getAddress(GOVERNANCE_TIMELOCK)` — i.e. the upgrade
///   authorizer address is itself config-supplied (FR-6 / AC-3), so rotating
///   the Timelock is a governed config write, not a contract upgrade.
/// - A `pause()`/`unpause()` circuit breaker gated by the same Timelock
///   address, scoped to state-mutating entry points only (view functions
///   remain callable during pause, per the Arch pass's resolution of QA
///   open question #1).
///
/// Every concrete contract stores its own `IProtocolConfig` pointer,
/// itself updatable only by the Timelock, so pointing at #54's real
/// governance module later is a config-address swap, not a redeploy.
abstract contract GovernedUpgradeable is Initializable, UUPSUpgradeable {
    /// @custom:storage-location erc7201:symposia.GovernedUpgradeable
    struct GovernedStorage {
        IProtocolConfig config;
        bool paused;
    }

    // keccak256(abi.encode(uint256(keccak256("symposia.storage.GovernedUpgradeable")) - 1)) & ~bytes32(uint256(0xff))
    bytes32 private constant GOVERNED_STORAGE_LOCATION = keccak256("symposia.storage.GovernedUpgradeable");

    event Paused(address indexed by);
    event Unpaused(address indexed by);
    event ConfigChanged(address indexed previousConfig, address indexed newConfig);

    error NotTimelock(address caller);
    error ContractPaused();

    function _governedStorage() private pure returns (GovernedStorage storage $) {
        bytes32 slot = GOVERNED_STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }

    // solhint-disable-next-line func-name-mixedcase
    function __GovernedUpgradeable_init(IProtocolConfig initialConfig) internal onlyInitializing {
        _governedStorage().config = initialConfig;
    }

    /// @notice The `IProtocolConfig` this contract reads every economic and
    /// network parameter from.
    function config() public view returns (IProtocolConfig) {
        return _governedStorage().config;
    }

    /// @notice The address currently authorized to approve upgrades, pause,
    /// and update `config()` itself — read live from config on every call,
    /// never a constructor-baked immutable (FR-6/AC-3).
    function timelock() public view returns (address) {
        return _governedStorage().config.getAddress(ConfigKeys.GOVERNANCE_TIMELOCK);
    }

    function paused() public view returns (bool) {
        return _governedStorage().paused;
    }

    modifier onlyTimelock() {
        if (msg.sender != timelock()) revert NotTimelock(msg.sender);
        _;
    }

    modifier whenNotPaused() {
        if (_governedStorage().paused) revert ContractPaused();
        _;
    }

    /// @notice Points this contract at a new `IProtocolConfig`. Gated by the
    /// Timelock like any other governed action — no direct admin override.
    function setConfig(IProtocolConfig newConfig) external onlyTimelock {
        emit ConfigChanged(address(_governedStorage().config), address(newConfig));
        _governedStorage().config = newConfig;
    }

    /// @notice Emergency circuit breaker (governance.md's shortened
    /// time-lock, pause-only scope). Halts state-mutating functions only;
    /// does not alter economic parameters or move funds by itself.
    function pause() external onlyTimelock {
        _governedStorage().paused = true;
        emit Paused(msg.sender);
    }

    function unpause() external onlyTimelock {
        _governedStorage().paused = false;
        emit Unpaused(msg.sender);
    }

    function _authorizeUpgrade(address) internal view override onlyTimelock {}
}
