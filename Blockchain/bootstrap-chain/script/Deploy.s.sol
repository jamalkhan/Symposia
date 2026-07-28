// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Script, console} from "forge-std/Script.sol";
import {NodeRegistry} from "../src/NodeRegistry.sol";
import {EpochRootRegistry} from "../src/EpochRootRegistry.sol";

/// @notice Deploys the Phase 0 minimal bootstrap chain contract set
/// (issue #110) and logs the resulting addresses for the Gateway/tests to
/// consume.
contract Deploy is Script {
    function run() external {
        vm.startBroadcast();

        NodeRegistry nodeRegistry = new NodeRegistry();
        EpochRootRegistry epochRootRegistry = new EpochRootRegistry(nodeRegistry);

        vm.stopBroadcast();

        console.log("NodeRegistry:", address(nodeRegistry));
        console.log("EpochRootRegistry:", address(epochRootRegistry));
    }
}
