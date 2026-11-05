// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {Initializable} from "@openzeppelin/contracts/proxy/utils/Initializable.sol";
import {UUPSUpgradeable} from "@openzeppelin/contracts/proxy/utils/UUPSUpgradeable.sol";
import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {SYMToken} from "../src/SYMToken.sol";

/// @dev Example V2 implementation exercising the Tier 3 upgrade path: adds
/// a new minter via `reinitializer(2)` without touching existing state.
contract SYMTokenV2Mock is SYMToken {
    function initializeV2(address[] calldata newMinters, uint256[] calldata allowances) external reinitializer(2) {
        _configureMinters(_symStorage(), newMinters, allowances);
    }

    function implementationVersion() external pure override returns (uint256) {
        return 2;
    }
}

/// @dev Builds a token proxy with the six genesis buckets (tokenomics-mvp.md §3).
abstract contract SYMTokenFixture is Test {
    uint256 internal constant ONE = 1e18;
    uint256 internal constant CAP = 10_000_000_000 * ONE;

    address internal authority = makeAddr("tier3Timelock");
    address internal networkRewards = makeAddr("networkRewards"); // 40%
    address internal ecosystem = makeAddr("ecosystem"); // 15%
    address internal foundation = makeAddr("foundation"); // 10%
    address internal team = makeAddr("team"); // 15%
    address internal investors = makeAddr("investors"); // 15%
    address internal publicLaunch = makeAddr("publicLaunch"); // 5%

    function _buckets() internal view returns (address[] memory m, uint256[] memory a) {
        m = new address[](6);
        a = new uint256[](6);
        (m[0], a[0]) = (networkRewards, 4_000_000_000 * ONE);
        (m[1], a[1]) = (ecosystem, 1_500_000_000 * ONE);
        (m[2], a[2]) = (foundation, 1_000_000_000 * ONE);
        (m[3], a[3]) = (team, 1_500_000_000 * ONE);
        (m[4], a[4]) = (investors, 1_500_000_000 * ONE);
        (m[5], a[5]) = (publicLaunch, 500_000_000 * ONE);
    }

    function _deploy(address[] memory m, uint256[] memory a) internal returns (SYMToken) {
        SYMToken impl = new SYMToken();
        bytes memory init = abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", authority, m, a));
        return SYMToken(address(new ERC1967Proxy(address(impl), init)));
    }
}

contract SYMTokenTest is SYMTokenFixture {
    SYMToken internal token;

    function setUp() public {
        (address[] memory m, uint256[] memory a) = _buckets();
        token = _deploy(m, a);
    }

    // --- Standard / metadata (TC-01..03) ---

    function test_Metadata() public view {
        assertEq(token.name(), "Symposia");
        assertEq(token.symbol(), "SYM");
        assertEq(token.decimals(), 18);
        assertEq(token.MAX_SUPPLY(), CAP);
        assertEq(token.maxSupply(), CAP);
        assertEq(token.upgradeAuthority(), authority);
        assertEq(token.implementationVersion(), 1);
        assertEq(token.minters().length, 6);
        assertEq(token.totalSupply(), 0);
    }

    function test_StorageUsesErc7201Slot() public {
        bytes32 slot = keccak256(abi.encode(uint256(keccak256("symposia.SYMToken")) - 1)) & ~bytes32(uint256(0xff));
        vm.prank(networkRewards);
        token.mint(address(this), 42);
        assertEq(uint256(vm.load(address(token), slot)), 42); // first field: totalMinted
        assertEq(address(uint160(uint256(vm.load(address(token), bytes32(uint256(slot) + 1))))), authority);
    }

    function test_ClockIsBlockNumber() public view {
        assertEq(token.clock(), uint48(block.number));
        assertEq(token.CLOCK_MODE(), "mode=blocknumber&from=default");
    }

    // --- Minting (TC-08..17, Gherkin) ---

    function test_GoldenPathMint() public {
        uint256 amount = 2_191_781 * ONE;
        vm.expectEmit(true, true, false, true);
        emit SYMToken.Minted(networkRewards, address(this), amount);
        vm.prank(networkRewards);
        token.mint(address(this), amount);
        assertEq(token.totalSupply(), amount);
        assertEq(token.totalMinted(), amount);
        assertEq(token.balanceOf(address(this)), amount);
        assertEq(token.remainingMintAllowance(networkRewards), 4_000_000_000 * ONE - amount);
    }

    function test_RevertWhen_UnauthorizedMint() public {
        address rando = makeAddr("rando");
        vm.prank(rando);
        vm.expectRevert(abi.encodeWithSelector(SYMToken.NotMinter.selector, rando));
        token.mint(rando, 1);
    }

    function test_RevertWhen_UpgradeAuthorityMints() public {
        vm.prank(authority);
        vm.expectRevert(abi.encodeWithSelector(SYMToken.NotMinter.selector, authority));
        token.mint(authority, 1);
    }

    function test_RevertWhen_AllowanceExhausted() public {
        vm.startPrank(foundation);
        token.mint(foundation, 1_000_000_000 * ONE);
        vm.expectRevert(abi.encodeWithSelector(SYMToken.NotMinter.selector, foundation));
        token.mint(foundation, 1);
        vm.stopPrank();
        assertLt(token.totalMinted(), CAP); // global headroom remained
    }

    function test_RevertWhen_MintExceedsRemainingAllowance() public {
        vm.prank(publicLaunch);
        vm.expectRevert(
            abi.encodeWithSelector(
                SYMToken.MintAllowanceExceeded.selector, publicLaunch, 500_000_000 * ONE + 1, 500_000_000 * ONE
            )
        );
        token.mint(publicLaunch, 500_000_000 * ONE + 1);
    }

    function test_AllowancesAreIsolated() public {
        uint256 before = token.remainingMintAllowance(team);
        vm.prank(investors);
        token.mint(investors, 123 * ONE);
        assertEq(token.remainingMintAllowance(team), before);
    }

    function test_RevertWhen_ZeroMint() public {
        vm.prank(networkRewards);
        vm.expectRevert(SYMToken.ZeroAmount.selector);
        token.mint(networkRewards, 0);
    }

    function test_RevertWhen_MintToZeroAddress() public {
        vm.prank(networkRewards);
        vm.expectRevert();
        token.mint(address(0), 1);
        assertEq(token.totalMinted(), 0);
    }

    function test_MintUpToExactCap() public {
        (address[] memory m, uint256[] memory a) = _buckets();
        for (uint256 i; i < m.length; ++i) {
            vm.prank(m[i]);
            token.mint(m[i], a[i]);
        }
        assertEq(token.totalSupply(), CAP);
        assertEq(token.totalMinted(), CAP);
    }

    function test_NoBurnFunctionExposed() public {
        // v1 ships without burn (Arch), so TC-16/17 are N/A; assert the selector is absent.
        (bool ok,) = address(token).call(abi.encodeWithSignature("burn(uint256)", 1));
        assertFalse(ok);
    }

    // --- Global cap guard in isolation (TC-09..11) ---

    function test_GlobalCapBoundaryWithOverAllocatingUpgrade() public {
        // The init guard prevents allowances summing above the cap, so the
        // per-mint global check can only be reached directly via an
        // implementation whose allowance sum equals the cap. Mint to
        // CAP - 100 and verify exactly 100 more succeeds and 1 more fails.
        address[] memory m = new address[](2);
        uint256[] memory a = new uint256[](2);
        (m[0], a[0]) = (networkRewards, CAP - 100 * ONE);
        (m[1], a[1]) = (ecosystem, 100 * ONE);
        SYMToken t = _deploy(m, a);
        vm.prank(networkRewards);
        t.mint(networkRewards, CAP - 100 * ONE);
        vm.prank(ecosystem);
        vm.expectRevert(abi.encodeWithSelector(SYMToken.MintAllowanceExceeded.selector, ecosystem, 101 * ONE, 100 * ONE));
        t.mint(ecosystem, 101 * ONE);
        vm.prank(ecosystem);
        t.mint(ecosystem, 100 * ONE);
        assertEq(t.totalSupply(), CAP);
    }

    // --- Initializer guards (TC-18, 19, 47) ---

    function test_RevertWhen_AllowancesExceedCap() public {
        (address[] memory m, uint256[] memory a) = _buckets();
        a[0] += 1;
        SYMToken impl = new SYMToken();
        bytes memory init = abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", authority, m, a));
        vm.expectRevert(abi.encodeWithSelector(SYMToken.AllowancesExceedMaxSupply.selector, CAP + 1, CAP));
        new ERC1967Proxy(address(impl), init);
    }

    function test_RevertWhen_DuplicateMinter() public {
        (address[] memory m, uint256[] memory a) = _buckets();
        m[1] = m[0];
        SYMToken impl = new SYMToken();
        bytes memory init = abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", authority, m, a));
        vm.expectRevert(abi.encodeWithSelector(SYMToken.DuplicateMinter.selector, m[0]));
        new ERC1967Proxy(address(impl), init);
    }

    function test_RevertWhen_EmptyMinterList() public {
        SYMToken impl = new SYMToken();
        bytes memory init =
            abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", authority, new address[](0), new uint256[](0)));
        vm.expectRevert(SYMToken.InvalidMinterConfig.selector);
        new ERC1967Proxy(address(impl), init);
    }

    function test_RevertWhen_LengthMismatch() public {
        SYMToken impl = new SYMToken();
        bytes memory init =
            abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", authority, new address[](1), new uint256[](2)));
        vm.expectRevert(SYMToken.InvalidMinterConfig.selector);
        new ERC1967Proxy(address(impl), init);
    }

    function test_RevertWhen_ZeroAuthority() public {
        (address[] memory m, uint256[] memory a) = _buckets();
        SYMToken impl = new SYMToken();
        bytes memory init = abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", address(0), m, a));
        vm.expectRevert(SYMToken.ZeroAddress.selector);
        new ERC1967Proxy(address(impl), init);
    }

    function test_RevertWhen_ReinitializeProxy() public {
        (address[] memory m, uint256[] memory a) = _buckets();
        vm.expectRevert(Initializable.InvalidInitialization.selector);
        token.initialize("X", "X", authority, m, a);
    }

    function test_RevertWhen_ImplementationInitializedDirectly() public {
        SYMToken impl = new SYMToken();
        (address[] memory m, uint256[] memory a) = _buckets();
        vm.expectRevert(Initializable.InvalidInitialization.selector);
        impl.initialize("X", "X", address(this), m, a);
    }

    // --- Upgrade path (TC-20..24) ---

    function test_RevertWhen_NonAuthorityUpgrades() public {
        SYMTokenV2Mock v2 = new SYMTokenV2Mock();
        vm.expectRevert(abi.encodeWithSelector(SYMToken.NotUpgradeAuthority.selector, address(this)));
        token.upgradeToAndCall(address(v2), "");
    }

    function test_AuthorityUpgradeAddsMinterAndPreservesState() public {
        // Free up 1B of headroom by deploying with a smaller ecosystem bucket.
        (address[] memory m, uint256[] memory a) = _buckets();
        a[1] = 500_000_000 * ONE;
        SYMToken t = _deploy(m, a);
        vm.prank(networkRewards);
        t.mint(address(this), 10 * ONE);

        address grants2 = makeAddr("grants2");
        address[] memory nm = new address[](1);
        uint256[] memory na = new uint256[](1);
        (nm[0], na[0]) = (grants2, 1_000_000_000 * ONE);

        SYMTokenV2Mock v2 = new SYMTokenV2Mock();
        vm.prank(authority);
        t.upgradeToAndCall(address(v2), abi.encodeCall(SYMTokenV2Mock.initializeV2, (nm, na)));

        assertEq(t.implementationVersion(), 2);
        assertEq(t.balanceOf(address(this)), 10 * ONE);
        assertEq(t.totalMinted(), 10 * ONE);
        assertEq(t.remainingMintAllowance(grants2), 1_000_000_000 * ONE);
        assertEq(t.minters().length, 7);
    }

    function test_RevertWhen_UpgradeOverAllocates() public {
        address[] memory nm = new address[](1);
        uint256[] memory na = new uint256[](1);
        (nm[0], na[0]) = (makeAddr("extra"), 1);
        SYMTokenV2Mock v2 = new SYMTokenV2Mock();
        vm.prank(authority);
        vm.expectRevert(abi.encodeWithSelector(SYMToken.AllowancesExceedMaxSupply.selector, CAP + 1, CAP));
        token.upgradeToAndCall(address(v2), abi.encodeCall(SYMTokenV2Mock.initializeV2, (nm, na)));
    }

    function test_RevertWhen_ImplementationUpgradedDirectly() public {
        SYMToken impl = new SYMToken();
        vm.expectRevert(UUPSUpgradeable.UUPSUnauthorizedCallContext.selector);
        impl.upgradeToAndCall(address(impl), "");
    }

    // --- Permit (TC-26..31) ---

    bytes32 internal constant PERMIT_TYPEHASH =
        keccak256("Permit(address owner,address spender,uint256 value,uint256 nonce,uint256 deadline)");

    function _signPermit(uint256 pk, address spender, uint256 value, uint256 deadline)
        internal
        view
        returns (uint8 v, bytes32 r, bytes32 s)
    {
        address owner = vm.addr(pk);
        bytes32 structHash = keccak256(abi.encode(PERMIT_TYPEHASH, owner, spender, value, token.nonces(owner), deadline));
        return vm.sign(pk, keccak256(abi.encodePacked("\x19\x01", token.DOMAIN_SEPARATOR(), structHash)));
    }

    function test_PermitSetsAllowanceAndRejectsReplay() public {
        uint256 pk = 0xB0B;
        address owner = vm.addr(pk);
        address spender = makeAddr("spender");
        uint256 deadline = block.timestamp + 1 hours;
        (uint8 v, bytes32 r, bytes32 s) = _signPermit(pk, spender, 500 * ONE, deadline);

        token.permit(owner, spender, 500 * ONE, deadline, v, r, s);
        assertEq(token.allowance(owner, spender), 500 * ONE);
        assertEq(token.nonces(owner), 1);

        vm.expectRevert();
        token.permit(owner, spender, 500 * ONE, deadline, v, r, s);
    }

    function test_RevertWhen_PermitExpired() public {
        uint256 pk = 0xB0B;
        uint256 deadline = block.timestamp + 1;
        (uint8 v, bytes32 r, bytes32 s) = _signPermit(pk, address(1), 1, deadline);
        vm.warp(deadline + 1);
        vm.expectRevert();
        token.permit(vm.addr(pk), address(1), 1, deadline, v, r, s);
    }

    function test_RevertWhen_PermitWrongSigner() public {
        uint256 deadline = block.timestamp + 1 hours;
        (uint8 v, bytes32 r, bytes32 s) = _signPermit(0xBAD, address(1), 1, deadline);
        vm.expectRevert();
        token.permit(vm.addr(0xB0B), address(1), 1, deadline, v, r, s);
    }

    function test_RevertWhen_PermitHighS() public {
        uint256 pk = 0xB0B;
        uint256 deadline = block.timestamp + 1 hours;
        (uint8 v, bytes32 r, bytes32 s) = _signPermit(pk, address(1), 1, deadline);
        bytes32 highS = bytes32(0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141 - uint256(s));
        vm.expectRevert();
        token.permit(vm.addr(pk), address(1), 1, deadline, v == 27 ? 28 : 27, r, highS);
    }

    function test_DomainSeparatorBindsChainId() public {
        bytes32 before = token.DOMAIN_SEPARATOR();
        vm.chainId(block.chainid + 1);
        assertTrue(token.DOMAIN_SEPARATOR() != before);
    }

    // --- Votes (TC-32..37) ---

    function test_DelegationAndPastVotes() public {
        address a = makeAddr("holderA");
        address b = makeAddr("holderB");
        vm.prank(networkRewards);
        token.mint(a, 1000 * ONE);
        vm.roll(10);
        vm.prank(a);
        token.delegate(b);
        vm.roll(11);
        assertEq(token.getVotes(b), 1000 * ONE);
        assertEq(token.getVotes(a), 0);
        assertEq(token.getPastVotes(b, 10), 1000 * ONE);
        assertEq(token.getPastVotes(a, 10), 0);
        assertEq(token.getPastVotes(b, 9), 0); // before delegation
        vm.expectRevert();
        token.getPastVotes(b, 11); // current block
    }

    function test_MintUpdatesDelegateCheckpoint() public {
        address a = makeAddr("holderA");
        vm.prank(a);
        token.delegate(a);
        vm.roll(5);
        vm.prank(networkRewards);
        token.mint(a, 7 * ONE);
        vm.roll(6);
        assertEq(token.getPastVotes(a, 5), 7 * ONE);
        assertEq(token.getPastTotalSupply(5), 7 * ONE);
    }

    function test_TransfersMoveVotes() public {
        address a = makeAddr("holderA");
        address b = makeAddr("holderB");
        vm.prank(networkRewards);
        token.mint(a, 10 * ONE);
        vm.prank(a);
        token.delegate(a);
        vm.prank(b);
        token.delegate(b);
        vm.prank(a);
        IERC20(address(token)).transfer(b, 4 * ONE);
        assertEq(token.getVotes(a), 6 * ONE);
        assertEq(token.getVotes(b), 4 * ONE);
    }

    // --- Fuzz (TC-06/07 example-level; invariant suite below) ---

    function testFuzz_MintNeverExceedsAllowanceOrCap(uint256 amount) public {
        uint256 allowance = token.remainingMintAllowance(networkRewards);
        vm.prank(networkRewards);
        if (amount == 0 || amount > allowance) {
            vm.expectRevert();
            token.mint(networkRewards, amount);
        } else {
            token.mint(networkRewards, amount);
            assertEq(token.remainingMintAllowance(networkRewards), allowance - amount);
        }
        assertLe(token.totalMinted(), CAP);
    }
}

/// @dev Invariant handler: arbitrary mint/transfer/delegate sequences from
/// minters and non-minters alike.
contract SYMTokenHandler is Test {
    SYMToken internal token;
    address[] internal actors;
    uint256 public ghostMinted;

    constructor(SYMToken token_, address[] memory minters) {
        token = token_;
        actors = minters;
        actors.push(makeAddr("outsider1"));
        actors.push(makeAddr("outsider2"));
    }

    function mint(uint256 actorSeed, uint256 toSeed, uint256 amount) external {
        address actor = actors[actorSeed % actors.length];
        address to = actors[toSeed % actors.length];
        amount = bound(amount, 1, 5_000_000_000e18);
        vm.prank(actor);
        try token.mint(to, amount) {
            ghostMinted += amount;
        } catch {}
    }

    function transfer(uint256 fromSeed, uint256 toSeed, uint256 amount) external {
        address from = actors[fromSeed % actors.length];
        amount = bound(amount, 0, token.balanceOf(from));
        vm.prank(from);
        token.transfer(actors[toSeed % actors.length], amount);
    }

    function delegate(uint256 fromSeed, uint256 toSeed) external {
        vm.prank(actors[fromSeed % actors.length]);
        token.delegate(actors[toSeed % actors.length]);
    }
}

contract SYMTokenInvariantTest is SYMTokenFixture {
    SYMToken internal token;
    SYMTokenHandler internal handler;
    uint256 internal initialAllowanceSum;

    function setUp() public {
        (address[] memory m, uint256[] memory a) = _buckets();
        token = _deploy(m, a);
        for (uint256 i; i < a.length; ++i) {
            initialAllowanceSum += a[i];
        }
        handler = new SYMTokenHandler(token, m);
        targetContract(address(handler));
    }

    function invariant_TotalSupplyNeverExceedsCap() public view {
        assertLe(token.totalSupply(), token.MAX_SUPPLY());
    }

    function invariant_TotalMintedNeverExceedsCap() public view {
        assertLe(token.totalMinted(), token.MAX_SUPPLY());
        assertEq(token.totalMinted(), handler.ghostMinted());
    }

    function invariant_RemainingPlusMintedEqualsConfigured() public view {
        address[] memory m = token.minters();
        uint256 remaining;
        for (uint256 i; i < m.length; ++i) {
            remaining += token.remainingMintAllowance(m[i]);
        }
        assertEq(remaining + token.totalMinted(), initialAllowanceSum);
        assertEq(token.totalAllocated(), initialAllowanceSum);
        assertLe(token.totalAllocated(), token.MAX_SUPPLY());
    }
}
