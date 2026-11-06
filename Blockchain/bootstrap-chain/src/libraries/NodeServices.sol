// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {INodeRegistry} from "../interfaces/INodeRegistry.sol";

/// @title NodeServices
/// @notice The one place that says which Node Services exist and how each is funded (#159/#163).
/// Adding a service means appending to `INodeRegistry.NodeType` and updating this library.
library NodeServices {
    /// @notice Number of Node Service types (`INodeRegistry.NodeType` values).
    uint8 internal constant COUNT = uint8(type(INodeRegistry.NodeType).max) + 1;

    /// @notice Services paid from the 92% capacity emission, each with its own type pool:
    /// Storage, OLTP, Analytics, Consensus, Serverless (0-4) and Blob Gateway (#160).
    function isCapacity(uint8 t) internal pure returns (bool) {
        return t <= uint8(INodeRegistry.NodeType.Serverless) || t == uint8(INodeRegistry.NodeType.BlobGateway);
    }

    /// @notice Services paid from emission at all: capacity services plus Verifier (8%, #81).
    /// Email IP Address is hardcoded out, and no config can change that.
    function isPaid(uint8 t) internal pure returns (bool) {
        if (t == uint8(INodeRegistry.NodeType.EmailIP)) return false;
        return isCapacity(t) || t == uint8(INodeRegistry.NodeType.Verifier);
    }
}
