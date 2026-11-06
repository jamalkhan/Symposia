// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {NodeServices} from "../src/libraries/NodeServices.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";

/// @notice #163: the single source of which Node Services exist and how each is funded.
contract NodeServicesLibraryTest is Test {
    function test_CountMatchesTheEnum() public pure {
        assertEq(NodeServices.COUNT, 11);
        assertEq(uint8(type(INodeRegistry.NodeType).max), NodeServices.COUNT - 1);
    }

    function test_Funding() public pure {
        bool[11] memory capacity = [true, true, true, true, true, false, false, false, true, false, false];
        bool[11] memory paid = [true, true, true, true, true, true, false, false, true, false, false];
        for (uint8 t; t < NodeServices.COUNT; t++) {
            assertEq(NodeServices.isCapacity(t), capacity[t]);
            assertEq(NodeServices.isPaid(t), paid[t]);
        }
        assertFalse(NodeServices.isPaid(uint8(INodeRegistry.NodeType.EmailIP)), "Email IP Address never earns");
    }
}
