// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {StakingNodeRegistry} from "../../src/StakingNodeRegistry.sol";

/// @notice Trivial "V2" used only to exercise the UUPS upgrade path in
/// tests — adds one new view function, storage layout otherwise untouched
/// (new logic only, no new storage variables, so no storage-gap concerns).
contract StakingNodeRegistryV2Mock is StakingNodeRegistry {
    function version() external pure returns (string memory) {
        return "v2-test";
    }
}
