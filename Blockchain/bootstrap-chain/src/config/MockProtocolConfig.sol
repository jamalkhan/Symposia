// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IProtocolConfig} from "./IProtocolConfig.sol";

/// @title MockProtocolConfig
/// @notice Minimal owner-settable `IProtocolConfig` stub for local
/// development and testing pending issue #54's real governance/config
/// module landing. NOT for production use — `setUint`/`setAddress`/`setBool`
/// are gated only by a simple owner, not a governance vote + time-lock.
/// Production deployments must point the four core contracts at #54's real
/// config contract instead (a config-address swap, not a redeploy of this
/// issue's contracts — see Arch pass on issue #52).
contract MockProtocolConfig is IProtocolConfig {
    address public owner;

    mapping(bytes32 => uint256) private _uints;
    mapping(bytes32 => address) private _addresses;
    mapping(bytes32 => bool) private _bools;

    event UintSet(bytes32 indexed key, uint256 value);
    event AddressSet(bytes32 indexed key, address value);
    event BoolSet(bytes32 indexed key, bool value);
    event OwnerChanged(address indexed previousOwner, address indexed newOwner);

    modifier onlyOwner() {
        require(msg.sender == owner, "MockProtocolConfig: not owner");
        _;
    }

    constructor(address initialOwner) {
        owner = initialOwner;
    }

    function transferOwnership(address newOwner) external onlyOwner {
        emit OwnerChanged(owner, newOwner);
        owner = newOwner;
    }

    function setUint(bytes32 key, uint256 value) external onlyOwner {
        _uints[key] = value;
        emit UintSet(key, value);
    }

    function setAddress(bytes32 key, address value) external onlyOwner {
        _addresses[key] = value;
        emit AddressSet(key, value);
    }

    function setBool(bytes32 key, bool value) external onlyOwner {
        _bools[key] = value;
        emit BoolSet(key, value);
    }

    function getUint(bytes32 key) external view returns (uint256) {
        return _uints[key];
    }

    function getAddress(bytes32 key) external view returns (address) {
        return _addresses[key];
    }

    function getBool(bytes32 key) external view returns (bool) {
        return _bools[key];
    }
}
