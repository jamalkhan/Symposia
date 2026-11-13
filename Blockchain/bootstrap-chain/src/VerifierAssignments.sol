// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {MessageHashUtils} from "@openzeppelin/contracts/utils/cryptography/MessageHashUtils.sol";
import {Checkpoints} from "@openzeppelin/contracts/utils/structs/Checkpoints.sol";
import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";
import {IEpochController} from "./interfaces/IEpochController.sol";
import {IScoringWeights} from "./interfaces/ITelemetryRecords.sol";
import {FactorScoring} from "./libraries/FactorScoring.sol";

/// @title VerifierAssignments
/// @notice On-chain Verifier check assignments and peer-agreement accuracy (#227).
///
/// - **Pools.** The contract keeps its own indexed sets of Active Verifiers and Active targets (Blob Gateways),
///   every write checkpointed by a global sequence. The registry has no per-service enumeration or status hook, so
///   membership is pulled: anyone may `sync` a node to match its registry status (`staleAmong` says which need it).
/// - **Seed (commit, then reveal).** `seedEpoch` (anyone, during the epoch) freezes the pools at the current sequence,
///   plus K and the tolerances, and commits to a block `SEED_DELAY` ahead. `revealSeed` (anyone, once that block is
///   mined) sets the seed from its hash. Nobody knows the seed when the pools freeze, and nobody can pick it by
///   timing a call. A commit whose block left the 256-block hash window can be renewed during the epoch.
/// - **Assignment.** Lazy: `assignedVerifiers(epoch, target)` derives min(K, N') distinct Verifiers from the seed,
///   never the target's own node. Nothing is stored per assignment; `attest` checks each claimed slot and that the
///   target is in the epoch's snapshot.
/// - **Attestation.** One batch per Verifier per epoch, signed by its node key with an increasing nonce (an older
///   batch can't be replayed over a correction), sent by anyone. A resubmission replaces an item.
/// - **Finalize.** After the window, anyone runs `finalize` in chunks (also while paused, so a pause never blocks
///   seals): it counts each Verifier's assignments and submissions, and judges each target with ≥ 3 attestations
///   against the median of all of them. Missing data is reported as `FactorScoring.NO_DATA`, so a Verifier with no
///   assignments or nothing judged is scored on what it has, neither rewarded nor punished.
contract VerifierAssignments is GovernedUpgradeable {
    using Checkpoints for Checkpoints.Trace208;

    /// @notice At most this many factors per target service fit one packed attestation slot.
    uint256 public constant MAX_FACTORS = 4;

    /// @notice Values are packed as uint56; larger readings are rejected.
    uint256 public constant MAX_VALUE = type(uint56).max;

    uint256 public constant MAX_K = 9;

    uint256 public constant DEFAULT_K = 5;

    /// @notice Blocks between the commit and the block whose hash becomes the seed.
    uint256 public constant SEED_DELAY = 4;

    /// @notice The pool key for Verifiers; targets are keyed by their service.
    uint8 internal constant VERIFIER_POOL = uint8(INodeRegistry.NodeType.Verifier);

    struct Pool {
        Checkpoints.Trace208 length;
        mapping(uint256 index => Checkpoints.Trace208) slot; // value = uint160(node)
        mapping(address node => uint256) indexPlus1; // current membership
        mapping(address node => Checkpoints.Trace208) member; // 1/0 by seq: membership at a snapshot in O(log)
        uint256 size; // current length
    }

    struct EpochInfo {
        bytes32 seed;
        uint48 snapSeq;
        uint8 k;
        uint8 targetService;
        uint8 factors;
        uint16 relBps;
        uint32 cursor; // finalize progress over the target snapshot
        bool finalized;
        uint64 commitBlock; // 0 = not committed
        uint32[MAX_FACTORS] absFloor;
    }

    struct Counts {
        uint32 assigned;
        uint32 submitted;
        uint32 judged;
        uint32 upheld;
    }

    struct Item {
        address target;
        uint8 slot;
        uint256[] values;
    }

    /// @custom:storage-location erc7201:symposia.VerifierAssignments
    struct AssignmentsStorage {
        uint48 poolSeq;
        // Pools behind a mapping (Verifiers at VERIFIER_POOL, targets at their service), so Pool can grow in an
        // upgrade without shifting anything after it.
        mapping(uint8 key => Pool) pools;
        mapping(uint256 epoch => EpochInfo) epochs;
        // packed: factor i in bits [56*i, 56*i+56), bit 255 = present
        mapping(uint256 epoch => mapping(address target => mapping(uint8 slot => uint256))) attestations;
        mapping(uint256 epoch => mapping(address verifier => Counts)) counts;
        mapping(uint256 epoch => mapping(address verifier => uint256)) lastNonce;
    }

    // keccak256(abi.encode(uint256(keccak256("symposia.VerifierAssignments")) - 1)) & ~bytes32(uint256(0xff))
    bytes32 private constant STORAGE_LOCATION = 0xc4f0770a1d4f62d22a051aa32a369508a6dd3b3c70a234a35d028aff503a7700;

    uint256 private constant PRESENT = 1 << 255;

    event PoolChanged(uint8 indexed pool, address indexed node, bool member, uint48 seq);
    event EpochCommitted(uint256 indexed epoch, uint64 commitBlock, uint48 snapSeq, uint256 verifiers, uint256 targets, uint8 k);
    event EpochSeeded(uint256 indexed epoch, bytes32 seed);
    event Attested(uint256 indexed epoch, address indexed verifier, address indexed target, uint8 slot, uint256[] values);
    event TargetJudged(uint256 indexed epoch, address indexed target, uint256 attestations, uint256 upheldMask);
    event EpochFinalized(uint256 indexed epoch, uint256 targets);

    error NotSeeded(uint256 epoch);
    error OutsideEpoch(uint256 epoch);
    error SeedNotReady(uint256 epoch, uint256 block);
    error WindowClosed(uint256 epoch);
    error WindowOpen(uint256 epoch);
    error AlreadyFinalizing(uint256 epoch);
    error NotAssigned(uint256 epoch, address target, uint8 slot, address verifier);
    error NotATarget(uint256 epoch, address target);
    error StaleNonce(uint256 nonce, uint256 last);
    error InvalidSignature();
    error BadValues(uint256 given, uint256 expected);
    error BadParameters();

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    function initialize(IProtocolConfig initialConfig) external initializer {
        __GovernedUpgradeable_init(initialConfig);
    }

    function _s() private pure returns (AssignmentsStorage storage $) {
        bytes32 slot = STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }

    // ---------------------------------------------------------------- pools

    /// @notice Makes `node`'s pool memberships match its registry status: a Verifier pool member iff its Verifier
    /// service is Active, a target pool member iff its target service is Active. Anyone may call it.
    function sync(address node) public whenNotPaused {
        (bool verifier, bool target) = _status(node);
        uint8 t = targetService();
        _setMember(VERIFIER_POOL, node, verifier);
        _setMember(t, node, target);
    }

    function syncMany(address[] calldata nodes) external {
        for (uint256 i; i < nodes.length; i++) {
            sync(nodes[i]);
        }
    }

    /// @notice Which of `nodes` have a pool membership that no longer matches the registry, so a keeper syncs only those.
    function staleAmong(address[] calldata nodes) external view returns (address[] memory stale) {
        address[] memory found = new address[](nodes.length);
        uint256 n;
        uint8 t = targetService();
        AssignmentsStorage storage $ = _s();
        for (uint256 i; i < nodes.length; i++) {
            (bool verifier, bool target) = _status(nodes[i]);
            if (verifier != ($.pools[VERIFIER_POOL].indexPlus1[nodes[i]] != 0) || target != ($.pools[t].indexPlus1[nodes[i]] != 0)) {
                found[n++] = nodes[i];
            }
        }
        stale = new address[](n);
        for (uint256 i; i < n; i++) {
            stale[i] = found[i];
        }
    }

    /// @notice Current pool members (`pool` = 5 for Verifiers, else the target service), so a keeper can sync out
    /// nodes the registry no longer lists.
    function poolMembers(uint8 pool, uint256 offset, uint256 limit) external view returns (address[] memory members) {
        Pool storage p = _s().pools[pool];
        uint256 end = offset + limit < p.size ? offset + limit : p.size;
        members = new address[](end > offset ? end - offset : 0);
        for (uint256 i = offset; i < end; i++) {
            members[i - offset] = address(uint160(p.slot[i].latest()));
        }
    }

    function _status(address node) private view returns (bool verifier, bool target) {
        INodeRegistry registry = INodeRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS));
        verifier = registry.serviceStatusOf(node, INodeRegistry.NodeType.Verifier) == INodeRegistry.NodeStatus.Active;
        target = registry.serviceStatusOf(node, INodeRegistry.NodeType(targetService())) == INodeRegistry.NodeStatus.Active;
    }

    function _setMember(uint8 key, address node, bool member) private {
        AssignmentsStorage storage $ = _s();
        Pool storage pool = $.pools[key];
        uint256 at = pool.indexPlus1[node];
        if (member == (at != 0)) return;
        uint48 seq = ++$.poolSeq;
        if (member) {
            pool.slot[pool.size].push(seq, uint208(uint160(node)));
            pool.indexPlus1[node] = ++pool.size;
        } else {
            // Swap-and-pop: the last member moves into the leaver's index.
            uint256 last = pool.size - 1;
            if (at - 1 != last) {
                address moved = address(uint160(pool.slot[last].latest()));
                pool.slot[at - 1].push(seq, uint208(uint160(moved)));
                pool.indexPlus1[moved] = at;
            }
            pool.indexPlus1[node] = 0;
            pool.size = last;
        }
        pool.length.push(seq, uint208(pool.size));
        pool.member[node].push(seq, member ? 1 : 0);
        emit PoolChanged(key, node, member, seq);
    }

    function _countAt(Pool storage pool, uint48 seq) private view returns (uint256) {
        return pool.length.upperLookup(seq);
    }

    function _at(Pool storage pool, uint256 index, uint48 seq) private view returns (address) {
        return address(uint160(pool.slot[index].upperLookup(seq)));
    }

    function targetService() public view returns (uint8) {
        uint256 t = config().getUint(ConfigKeys.VERIFIER_TARGET_SERVICE);
        return t == 0 ? uint8(INodeRegistry.NodeType.BlobGateway) : uint8(t);
    }

    // ---------------------------------------------------------------- seed

    /// @notice Commits `epoch`: freezes its pools, K and tolerances (first call only) and commits to the hash of a block
    /// `SEED_DELAY` ahead. Anyone, during the epoch; a commit that's still revealable is kept, an expired one renewed.
    function seedEpoch(uint256 epoch) external whenNotPaused {
        AssignmentsStorage storage $ = _s();
        EpochInfo storage e = $.epochs[epoch];
        if (e.seed != bytes32(0)) return;
        IEpochController epochs = _epochs();
        if (block.timestamp < epochs.epochStart(epoch) || block.timestamp >= epochs.epochStart(epoch + 1)) revert OutsideEpoch(epoch);
        if (e.commitBlock != 0 && block.number <= uint256(e.commitBlock) + 256) return; // still revealable

        if (e.commitBlock == 0) {
            IProtocolConfig cfg = config();
            uint256 k = cfg.getUint(ConfigKeys.VERIFIER_K);
            uint8 t = targetService();
            uint256 factors = IScoringWeights(cfg.getAddress(ConfigKeys.REWARD_SCORING_WEIGHTS)).factorCount(t);
            uint256 relBps = cfg.getUint(ConfigKeys.VERIFIER_TOLERANCE_REL_BPS);
            if (k == 0) k = DEFAULT_K;
            if (k > MAX_K || factors > MAX_FACTORS || relBps > 10_000) revert BadParameters();
            e.snapSeq = $.poolSeq;
            e.k = uint8(k);
            e.targetService = t;
            e.factors = uint8(factors);
            e.relBps = uint16(relBps);
            for (uint8 f; f < factors; f++) {
                uint256 abs = cfg.getUint(ConfigKeys.verifierToleranceAbs(f));
                if (abs > type(uint32).max) revert BadParameters();
                e.absFloor[f] = uint32(abs);
            }
        }
        e.commitBlock = uint64(block.number + SEED_DELAY);
        emit EpochCommitted(epoch, e.commitBlock, e.snapSeq, _countAt($.pools[VERIFIER_POOL], e.snapSeq),
            _countAt($.pools[e.targetService], e.snapSeq), e.k);
    }

    /// @notice Sets `epoch`'s seed from the committed block's hash. Anyone, once that block is mined and within 256 blocks.
    function revealSeed(uint256 epoch) external {
        EpochInfo storage e = _s().epochs[epoch];
        if (e.seed != bytes32(0)) return;
        if (e.commitBlock == 0 || block.number <= e.commitBlock || block.number > uint256(e.commitBlock) + 256) {
            revert SeedNotReady(epoch, e.commitBlock);
        }
        e.seed = keccak256(abi.encode(blockhash(e.commitBlock), epoch, block.chainid));
        emit EpochSeeded(epoch, e.seed);
    }

    function seedOf(uint256 epoch) external view returns (bytes32) {
        return _s().epochs[epoch].seed;
    }

    function epochInfo(uint256 epoch) external view returns (EpochInfo memory) {
        return _s().epochs[epoch];
    }

    /// @notice Factor values per attestation for `epoch` (the target service's factor count, fixed at commit).
    function factorsIn(uint256 epoch) external view returns (uint8) {
        return _s().epochs[epoch].factors;
    }

    function commitBlockOf(uint256 epoch) external view returns (uint64) {
        return _s().epochs[epoch].commitBlock;
    }

    function verifierCount(uint256 epoch) public view returns (uint256) {
        return _countAt(_s().pools[VERIFIER_POOL], _s().epochs[epoch].snapSeq);
    }

    function targetCount(uint256 epoch) public view returns (uint256) {
        EpochInfo storage e = _s().epochs[epoch];
        return _countAt(_s().pools[e.targetService], e.snapSeq);
    }

    function targetAt(uint256 epoch, uint256 index) public view returns (address) {
        EpochInfo storage e = _s().epochs[epoch];
        return _at(_s().pools[e.targetService], index, e.snapSeq);
    }

    function verifierAt(uint256 epoch, uint256 index) public view returns (address) {
        return _at(_s().pools[VERIFIER_POOL], index, _s().epochs[epoch].snapSeq);
    }

    /// @notice Whether `target` is in `epoch`'s target snapshot.
    function isTarget(uint256 epoch, address target) public view returns (bool) {
        EpochInfo storage e = _s().epochs[epoch];
        return e.commitBlock != 0 && _s().pools[e.targetService].member[target].upperLookup(e.snapSeq) == 1;
    }

    // ---------------------------------------------------------------- assignment

    /// @notice The Verifiers checking `target` in `epoch`, in slot order: min(K, N') distinct draws from the seed,
    /// never `target` itself (N' = the snapshot's Verifiers other than the target). Empty when unseeded.
    function assignedVerifiers(uint256 epoch, address target) public view returns (address[] memory chosen) {
        AssignmentsStorage storage $ = _s();
        EpochInfo storage e = $.epochs[epoch];
        if (e.seed == bytes32(0)) return chosen;
        Pool storage verifiers = $.pools[VERIFIER_POOL];
        uint48 seq = e.snapSeq;
        uint256 n = _countAt(verifiers, seq);
        // A target that is itself a Verifier is never drawn: one O(log) membership lookup, not a scan.
        bool selfIn = verifiers.member[target].upperLookup(seq) == 1;
        uint256 eligible = selfIn ? n - 1 : n;
        uint256 k = e.k < eligible ? e.k : eligible;
        chosen = new address[](k);
        uint256[] memory picked = new uint256[](k);
        for (uint256 d; d < k; d++) {
            uint256 idx = uint256(keccak256(abi.encode(e.seed, target, d))) % n;
            address v = _at(verifiers, idx, seq);
            // Linear probe past already-picked indices and the target's own: at most k + 1 steps.
            while (v == target || _picked(picked, d, idx)) {
                idx = (idx + 1) % n;
                v = _at(verifiers, idx, seq);
            }
            picked[d] = idx;
            chosen[d] = v;
        }
    }

    function _picked(uint256[] memory picked, uint256 count, uint256 idx) private pure returns (bool) {
        for (uint256 i; i < count; i++) {
            if (picked[i] == idx) return true;
        }
        return false;
    }

    /// @notice A page of `verifier`'s assignments in `epoch`: targets [offset, offset+limit) of the snapshot.
    function assignmentsOf(uint256 epoch, address verifier, uint256 offset, uint256 limit)
        external
        view
        returns (address[] memory targets, uint8[] memory slots, uint256 next)
    {
        uint256 total = targetCount(epoch);
        next = offset + limit < total ? offset + limit : total;
        address[] memory t = new address[](next > offset ? next - offset : 0);
        uint8[] memory sl = new uint8[](t.length);
        uint256 found;
        for (uint256 i = offset; i < next; i++) {
            address target = targetAt(epoch, i);
            address[] memory vs = assignedVerifiers(epoch, target);
            for (uint256 d; d < vs.length; d++) {
                if (vs[d] == verifier) {
                    (t[found], sl[found]) = (target, uint8(d));
                    found++;
                    break;
                }
            }
        }
        targets = new address[](found);
        slots = new uint8[](found);
        for (uint256 i; i < found; i++) {
            (targets[i], slots[i]) = (t[i], sl[i]);
        }
    }

    // ---------------------------------------------------------------- attestation

    /// @notice The digest a Verifier's node key signs (eth-signed-message prefix) for one batch.
    function attestationDigest(uint256 epoch, address verifier, uint256 nonce, Item[] calldata items) public view returns (bytes32) {
        return keccak256(abi.encode(address(this), block.chainid, epoch, verifier, nonce, keccak256(abi.encode(items))));
    }

    /// @notice Records `verifier`'s attestations for `epoch`, signed by its node key. Anyone may send it; `nonce` must
    /// exceed the last one used for (epoch, verifier), so an older batch can't be replayed over a newer one.
    function attest(uint256 epoch, address verifier, uint256 nonce, Item[] calldata items, bytes calldata signature) external whenNotPaused {
        AssignmentsStorage storage $ = _s();
        EpochInfo storage e = $.epochs[epoch];
        if (e.seed == bytes32(0)) revert NotSeeded(epoch);
        if (!_epochs().acceptsReports(epoch)) revert WindowClosed(epoch);
        if (e.cursor != 0 || e.finalized) revert AlreadyFinalizing(epoch);
        if (nonce <= $.lastNonce[epoch][verifier]) revert StaleNonce(nonce, $.lastNonce[epoch][verifier]);
        if (ECDSA.recover(MessageHashUtils.toEthSignedMessageHash(attestationDigest(epoch, verifier, nonce, items)), signature) != verifier) {
            revert InvalidSignature();
        }
        $.lastNonce[epoch][verifier] = nonce;

        for (uint256 i; i < items.length; i++) {
            Item calldata it = items[i];
            if (it.values.length != e.factors) revert BadValues(it.values.length, e.factors);
            // Only snapshot targets count: an attestation for any other address can't earn coverage.
            if (!isTarget(epoch, it.target)) revert NotATarget(epoch, it.target);
            address[] memory vs = assignedVerifiers(epoch, it.target);
            if (it.slot >= vs.length || vs[it.slot] != verifier) revert NotAssigned(epoch, it.target, it.slot, verifier);
            uint256 packed = PRESENT;
            for (uint256 f; f < it.values.length; f++) {
                if (it.values[f] > MAX_VALUE) revert BadValues(f, MAX_VALUE);
                packed |= it.values[f] << (56 * f);
            }
            $.attestations[epoch][it.target][it.slot] = packed;
            emit Attested(epoch, verifier, it.target, it.slot, it.values);
        }
    }

    function attestationOf(uint256 epoch, address target, uint8 slot) external view returns (bool present, uint256[] memory values) {
        uint256 packed = _s().attestations[epoch][target][slot];
        present = packed & PRESENT != 0;
        values = _unpack(packed, _s().epochs[epoch].factors);
    }

    function lastNonceOf(uint256 epoch, address verifier) external view returns (uint256) {
        return _s().lastNonce[epoch][verifier];
    }

    function _unpack(uint256 packed, uint256 factors) private pure returns (uint256[] memory values) {
        values = new uint256[](factors);
        for (uint256 f; f < factors; f++) {
            values[f] = (packed >> (56 * f)) & MAX_VALUE;
        }
    }

    // ---------------------------------------------------------------- finalize

    /// @notice Judges up to `maxTargets` more targets of `epoch` once its report window has closed. Anyone may call it,
    /// paused or not (RewardDistributor's seal waits for `isFinalized`, so a pause must never block every seal). Each
    /// target: count its Verifiers' assignments and submissions, and with ≥ 3 attestations uphold each one whose every
    /// factor is within tolerance of the median of all of them.
    function finalize(uint256 epoch, uint256 maxTargets) external returns (bool done) {
        AssignmentsStorage storage $ = _s();
        EpochInfo storage e = $.epochs[epoch];
        if (e.seed == bytes32(0)) revert NotSeeded(epoch);
        if (e.finalized) return true;
        if (_epochs().acceptsReports(epoch)) revert WindowOpen(epoch);

        uint256 total = targetCount(epoch);
        uint256 end = e.cursor + maxTargets < total ? e.cursor + maxTargets : total;
        for (uint256 i = e.cursor; i < end; i++) {
            _judge(epoch, e, targetAt(epoch, i));
        }
        e.cursor = uint32(end);
        if (end == total) {
            e.finalized = true;
            emit EpochFinalized(epoch, total);
        }
        return e.finalized;
    }

    function _judge(uint256 epoch, EpochInfo storage e, address target) private {
        AssignmentsStorage storage $ = _s();
        address[] memory vs = assignedVerifiers(epoch, target);
        uint256[] memory packed = new uint256[](vs.length);
        uint256 present;
        for (uint256 d; d < vs.length; d++) {
            Counts storage c = $.counts[epoch][vs[d]];
            c.assigned++;
            packed[d] = $.attestations[epoch][target][uint8(d)];
            if (packed[d] & PRESENT != 0) {
                c.submitted++; // counted here, over snapshot targets only
                present++;
            }
        }
        if (present < 3) {
            emit TargetJudged(epoch, target, present, 0);
            return;
        }

        uint256[] memory medians = new uint256[](e.factors);
        uint256[] memory column = new uint256[](present);
        for (uint256 f; f < e.factors; f++) {
            uint256 j;
            for (uint256 d; d < vs.length; d++) {
                if (packed[d] & PRESENT != 0) column[j++] = (packed[d] >> (56 * f)) & MAX_VALUE;
            }
            medians[f] = _median(column);
        }

        uint256 mask;
        for (uint256 d; d < vs.length; d++) {
            if (packed[d] & PRESENT == 0) continue;
            Counts storage c = $.counts[epoch][vs[d]];
            c.judged++;
            bool upheld = true;
            for (uint256 f; f < e.factors && upheld; f++) {
                uint256 v = (packed[d] >> (56 * f)) & MAX_VALUE;
                uint256 rel = (medians[f] * e.relBps) / 10_000;
                uint256 tol = rel > e.absFloor[f] ? rel : e.absFloor[f];
                uint256 diff = v > medians[f] ? v - medians[f] : medians[f] - v;
                upheld = diff <= tol;
            }
            if (upheld) {
                c.upheld++;
                mask |= 1 << d;
            }
        }
        emit TargetJudged(epoch, target, present, mask);
    }

    /// @dev Sorts in place (n ≤ MAX_K) and returns the median; an even count averages the middle two.
    function _median(uint256[] memory a) private pure returns (uint256) {
        for (uint256 i = 1; i < a.length; i++) {
            uint256 x = a[i];
            uint256 j = i;
            while (j > 0 && a[j - 1] > x) {
                a[j] = a[j - 1];
                j--;
            }
            a[j] = x;
        }
        uint256 mid = a.length / 2;
        return a.length % 2 == 1 ? a[mid] : (a[mid - 1] + a[mid]) / 2;
    }

    /// @notice Whether `epoch`'s Verifier results are final: an epoch never seeded has nothing to finalize.
    function isFinalized(uint256 epoch) public view returns (bool) {
        EpochInfo storage e = _s().epochs[epoch];
        return e.seed == bytes32(0) || e.finalized;
    }

    function countsOf(uint256 epoch, address verifier) external view returns (Counts memory) {
        return _s().counts[epoch][verifier];
    }

    /// @notice A Verifier's coverage (submitted/assigned) and accuracy (upheld/judged) for `epoch`, in bps, or
    /// `FactorScoring.NO_DATA` when there is nothing to measure (unseeded, nothing assigned, nothing judged): that
    /// factor is then left out of the Verifier's score instead of awarding full or zero marks.
    function factorsOf(uint256 epoch, address verifier) external view returns (uint256 coverageBps, uint256 accuracyBps) {
        Counts memory c = _s().counts[epoch][verifier];
        coverageBps = c.assigned == 0 ? FactorScoring.NO_DATA : (uint256(c.submitted) * 10_000) / c.assigned;
        accuracyBps = c.judged == 0 ? FactorScoring.NO_DATA : (uint256(c.upheld) * 10_000) / c.judged;
    }

    function _epochs() private view returns (IEpochController) {
        return IEpochController(config().getAddress(ConfigKeys.EPOCH_CONTROLLER));
    }
}
