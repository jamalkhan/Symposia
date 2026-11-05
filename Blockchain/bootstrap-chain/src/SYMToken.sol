// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Initializable} from "@openzeppelin/contracts/proxy/utils/Initializable.sol";
import {UUPSUpgradeable} from "@openzeppelin/contracts/proxy/utils/UUPSUpgradeable.sol";
import {ERC20Upgradeable} from "@openzeppelin/contracts-upgradeable/token/ERC20/ERC20Upgradeable.sol";
import {ERC20PermitUpgradeable} from
    "@openzeppelin/contracts-upgradeable/token/ERC20/extensions/ERC20PermitUpgradeable.sol";
import {ERC20VotesUpgradeable} from
    "@openzeppelin/contracts-upgradeable/token/ERC20/extensions/ERC20VotesUpgradeable.sol";
import {NoncesUpgradeable} from "@openzeppelin/contracts-upgradeable/utils/NoncesUpgradeable.sol";

/// @title SYMToken
/// @notice Symposia's native token (issue #70, tokenomics-mvp.md §2): ERC-20
/// + EIP-2612 Permit + EIP-5805 Votes, hard-capped at 10B SYM with 18
/// decimals.
///
/// Mint authority is a fixed allowlist configured once in the initializer:
/// each genesis allocation bucket (#71) and the Network Rewards emission
/// contract (#73) gets an individual allowance, decremented on every mint.
/// There is no setter for the allowlist, the cap or `upgradeAuthority`. All
/// three change only through a UUPS upgrade authorized by `upgradeAuthority`
/// (the Tier 3 timelock; the founding multi-sig during bootstrap), whose new
/// implementation runs its own `reinitializer`. That keeps every tokenomics
/// change a reviewable bytecode diff rather than a parameter write.
///
/// "Immutable" here is qualified: `MAX_SUPPLY` is constant for a given
/// implementation, and only a Tier 3-authorized upgrade can install a
/// different one. External copy should say "changeable only by Tier 3
/// governance", and the parameter viewer should surface `upgradeAuthority()`.
///
/// Invariants (enforced in `mint`, fuzzed in SYMToken.t.sol):
/// - `totalMinted <= MAX_SUPPLY`, and `totalMinted` never decreases. There
///   is no burn in v1; if one is added, `totalMinted` stays independent of
///   `totalSupply()`, so burning can never free mint headroom.
/// - each minter's cumulative mints are `<=` its configured allowance.
///
/// No transfer hooks, pause or blacklist exist, so the token stays
/// compatible with the canonical OP Stack bridge. Voting checkpoints use
/// OpenZeppelin's default block-number clock.
///
/// @custom:security Flagged for external audit before mainnet: cap
/// enforcement, the minter allowlist and `_authorizeUpgrade`. Every future
/// implementation needs the same audit, since an upgrade is the only way the
/// cap could change.
contract SYMToken is Initializable, ERC20Upgradeable, ERC20PermitUpgradeable, ERC20VotesUpgradeable, UUPSUpgradeable {
    /// @notice Hard cap on cumulative minting: 10,000,000,000 SYM (18 decimals).
    uint256 public immutable MAX_SUPPLY;

    /// @custom:storage-location erc7201:symposia.SYMToken
    struct SYMTokenStorage {
        uint256 totalMinted;
        address upgradeAuthority;
        mapping(address => uint256) minterAllowance;
        address[] minters;
        /// @dev Sum of every allowance ever configured (minted or not). Never
        /// decreases, so `totalAllocated <= MAX_SUPPLY` bounds all future mints.
        uint256 totalAllocated;
    }

    // keccak256(abi.encode(uint256(keccak256("symposia.SYMToken")) - 1)) & ~bytes32(uint256(0xff))
    bytes32 private constant SYM_TOKEN_STORAGE_LOCATION =
        0xce549f04739ca5a13b3949db2cd3dc78a478d0b25ad02a1abde4125391febe00;

    /// @notice Emitted once per minter whenever an implementation's
    /// initializer configures the allowlist. Together with `Minted` and
    /// `Upgraded`, this is the full audit trail of mint authority.
    event MinterConfigured(address indexed minter, uint256 allowance);
    event Minted(address indexed minter, address indexed to, uint256 amount);
    event UpgradeAuthoritySet(address indexed authority);

    error NotUpgradeAuthority(address caller);
    error NotMinter(address caller);
    error MintAllowanceExceeded(address minter, uint256 requested, uint256 remaining);
    error MaxSupplyExceeded(uint256 requested, uint256 headroom);
    error AllowancesExceedMaxSupply(uint256 totalAllowance, uint256 maxSupply);
    error InvalidMinterConfig();
    error DuplicateMinter(address minter);
    error ZeroAddress();
    error ZeroAmount();

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        MAX_SUPPLY = 10_000_000_000 * 10 ** 18;
        _disableInitializers();
    }

    /// @notice One-time proxy initializer. Deploy the proxy with this call
    /// encoded as `ERC1967Proxy` constructor data so initialization is atomic
    /// and can't be front-run.
    /// @param name_ Token name.
    /// @param symbol_ Token symbol (`SYM`, provisional).
    /// @param authority Sole address allowed to authorize upgrades. Per the
    /// #70 Arch plan this is deliberately a fixed address rather than the
    /// config-resolved timelock other contracts use (GovernedUpgradeable), so
    /// mutable config can never touch the cap. It MUST be the Tier 3
    /// timelock (the 3-of-5 founding multi-sig during bootstrap), never an
    /// EOA. Rotating it requires an upgrade whose reinitializer sets a new one.
    /// @param minters Allocation bucket / emission contracts allowed to mint.
    /// @param allowances Per-minter cumulative mint allowance, in wei. The sum
    /// must not exceed `MAX_SUPPLY`.
    function initialize(
        string calldata name_,
        string calldata symbol_,
        address authority,
        address[] calldata minters,
        uint256[] calldata allowances
    ) external initializer {
        if (authority == address(0)) revert ZeroAddress();
        __ERC20_init(name_, symbol_);
        __ERC20Permit_init(name_);
        __ERC20Votes_init();

        SYMTokenStorage storage $ = _symStorage();
        $.upgradeAuthority = authority;
        emit UpgradeAuthoritySet(authority);
        _configureMinters($, minters, allowances);
    }

    function _configureMinters(SYMTokenStorage storage $, address[] calldata minters, uint256[] calldata allowances)
        internal
    {
        uint256 count = minters.length;
        if (count == 0 || count != allowances.length) revert InvalidMinterConfig();

        uint256 total;
        for (uint256 i = 0; i < count; ++i) {
            address minter = minters[i];
            if (minter == address(0)) revert ZeroAddress();
            if (allowances[i] == 0) revert ZeroAmount();
            if ($.minterAllowance[minter] != 0) revert DuplicateMinter(minter);
            $.minterAllowance[minter] = allowances[i];
            $.minters.push(minter);
            total += allowances[i];
            emit MinterConfigured(minter, allowances[i]);
        }
        uint256 allocated = $.totalAllocated + total;
        if (allocated > MAX_SUPPLY) revert AllowancesExceedMaxSupply(allocated, MAX_SUPPLY);
        $.totalAllocated = allocated;
    }

    // ---------------------------------------------------------------------
    // Minting
    // ---------------------------------------------------------------------

    /// @notice Mints `amount` to `to` against the caller's allowance.
    /// @dev Reverts with a custom error, before any state is written, if the
    /// caller isn't a minter, the amount exceeds its remaining allowance, or
    /// the amount would push `totalMinted` past `MAX_SUPPLY`. The global
    /// check is redundant with the init-time allowance-sum guard but is kept
    /// on purpose so the cap holds on its own.
    function mint(address to, uint256 amount) external {
        SYMTokenStorage storage $ = _symStorage();
        uint256 remaining = $.minterAllowance[msg.sender];
        if (remaining == 0) revert NotMinter(msg.sender);
        if (amount == 0) revert ZeroAmount();
        if (amount > remaining) revert MintAllowanceExceeded(msg.sender, amount, remaining);
        uint256 headroom = MAX_SUPPLY - $.totalMinted;
        if (amount > headroom) revert MaxSupplyExceeded(amount, headroom);

        $.minterAllowance[msg.sender] = remaining - amount;
        $.totalMinted += amount;
        _mint(to, amount);
        emit Minted(msg.sender, to, amount);
    }

    // ---------------------------------------------------------------------
    // Public parameter views (tokenomics-mvp.md §12 viewer surface)
    // ---------------------------------------------------------------------

    function maxSupply() external view returns (uint256) {
        return MAX_SUPPLY;
    }

    /// @notice Cumulative amount ever minted. Monotonic; independent of burns.
    function totalMinted() external view returns (uint256) {
        return _symStorage().totalMinted;
    }

    /// @notice Sum of all allowances ever configured; always <= MAX_SUPPLY.
    function totalAllocated() external view returns (uint256) {
        return _symStorage().totalAllocated;
    }

    function remainingMintAllowance(address minter) external view returns (uint256) {
        return _symStorage().minterAllowance[minter];
    }

    /// @notice Every address ever configured as a minter, including exhausted ones.
    function minters() external view returns (address[] memory) {
        return _symStorage().minters;
    }

    /// @notice The only address that can authorize an upgrade, and so the
    /// only path to changing the cap, minter set or allowances.
    function upgradeAuthority() external view returns (address) {
        return _symStorage().upgradeAuthority;
    }

    function implementationVersion() external pure virtual returns (uint256) {
        return 1;
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    function _symStorage() internal pure returns (SYMTokenStorage storage $) {
        bytes32 slot = SYM_TOKEN_STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }

    function _authorizeUpgrade(address) internal view override {
        if (msg.sender != _symStorage().upgradeAuthority) revert NotUpgradeAuthority(msg.sender);
    }

    function _update(address from, address to, uint256 value)
        internal
        override(ERC20Upgradeable, ERC20VotesUpgradeable)
    {
        super._update(from, to, value);
    }

    function nonces(address owner) public view override(ERC20PermitUpgradeable, NoncesUpgradeable) returns (uint256) {
        return super.nonces(owner);
    }
}
