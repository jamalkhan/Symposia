// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Script, console} from "forge-std/Script.sol";
import {NodeRegistry} from "../src/NodeRegistry.sol";
import {EpochRootRegistry} from "../src/EpochRootRegistry.sol";
import {ProtocolActivations} from "../src/ProtocolActivations.sol";

/// @notice Deploys the Phase 0 minimal bootstrap chain contract set
/// (issue #110) and logs the resulting addresses for the Gateway/tests to
/// consume. ProtocolActivations (issue #145) is owned by the deployer; its notice window
/// comes from PROTOCOL_MIN_NOTICE (seconds, default 0 for local dev).
contract Deploy is Script {
    function run() external {
        vm.startBroadcast();

        NodeRegistry nodeRegistry = new NodeRegistry();
        EpochRootRegistry epochRootRegistry = new EpochRootRegistry(nodeRegistry);
        ProtocolActivations protocolActivations =
            new ProtocolActivations(msg.sender, uint64(vm.envOr("PROTOCOL_MIN_NOTICE", uint256(0))));

        vm.stopBroadcast();

        console.log("NodeRegistry:", address(nodeRegistry));
        console.log("EpochRootRegistry:", address(epochRootRegistry));
        console.log("ProtocolActivations:", address(protocolActivations));
    }
}
