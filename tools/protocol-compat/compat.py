#!/usr/bin/env python3
"""Protocol compatibility checks (issue #146).

Pure functions compare a base version of a protocol artifact with the head version and return a
list of violations (empty = compatible). `run.sh` feeds them files from git; `test_compat.py`
covers them with inline cases. Rules: docs/engineering/protocol-compatibility.md.

Standard library only, so it runs on stock CI runners and laptops without a build step.
"""

from __future__ import annotations

import json
import re
import sys
from typing import Any

DOC = "docs/engineering/protocol-compatibility.md"
STAGES = ["offered", "required", "retired"]


def _types(node: dict[str, Any]) -> set[str]:
    t = node.get("type")
    if t is None:
        return set()
    return set(t) if isinstance(t, list) else {t}


# ── JSON Schema ────────────────────────────────────────────────────────────────

def diff_schema(base: dict[str, Any], head: dict[str, Any], path: str = "$") -> list[str]:
    """A schema may only grow: no removed or retyped properties, nothing newly required or closed."""
    problems: list[str] = []
    base_types, head_types = _types(base), _types(head)
    if base_types and not base_types <= head_types:
        problems.append(f"{path}: type narrowed from {sorted(base_types)} to {sorted(head_types)}")

    newly_required = set(head.get("required", [])) - set(base.get("required", []))
    for name in sorted(newly_required):
        problems.append(f"{path}.{name}: became required")

    if head.get("additionalProperties") is False and base.get("additionalProperties") is not False:
        problems.append(f"{path}: additionalProperties: false added (rejects fields older peers send)")

    base_props, head_props = base.get("properties", {}), head.get("properties", {})
    for name, base_prop in base_props.items():
        if name not in head_props:
            problems.append(f"{path}.{name}: property removed")
        else:
            problems.extend(diff_schema(base_prop, head_props[name], f"{path}.{name}"))

    if "items" in base and isinstance(base["items"], dict):
        problems.extend(diff_schema(base["items"], head.get("items", {}), f"{path}[]"))
    return problems


def validate(instance: Any, schema: dict[str, Any], path: str = "$") -> list[str]:
    """The subset of JSON Schema our protocol schemas use: type, required, properties, items."""
    problems: list[str] = []
    types = _types(schema)
    actual = _json_type(instance)
    if types and actual not in types and not (actual == "integer" and "number" in types):
        return [f"{path}: is {actual}, schema allows {sorted(types)}"]

    if isinstance(instance, dict):
        for name in schema.get("required", []):
            if name not in instance:
                problems.append(f"{path}.{name}: required but missing")
        props = schema.get("properties", {})
        for name, value in instance.items():
            if name in props:
                problems.extend(validate(value, props[name], f"{path}.{name}"))
            elif schema.get("additionalProperties") is False:
                problems.append(f"{path}.{name}: not allowed")
    elif isinstance(instance, list) and isinstance(schema.get("items"), dict):
        for i, item in enumerate(instance):
            problems.extend(validate(item, schema["items"], f"{path}[{i}]"))
    return problems


def _json_type(value: Any) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, int):
        return "integer"
    if isinstance(value, float):
        return "number"
    if isinstance(value, str):
        return "string"
    if isinstance(value, list):
        return "array"
    return "object"


# ── Golden fixtures ────────────────────────────────────────────────────────────

def diff_fixtures(base: dict[str, str], head: dict[str, str]) -> list[str]:
    """Fixtures (path -> content) are history: they may be added, never deleted or edited."""
    problems: list[str] = []
    for path, content in sorted(base.items()):
        if path not in head:
            problems.append(f"{path}: fixture deleted")
        elif json.loads(content) != json.loads(head[path]):
            problems.append(f"{path}: fixture edited (add a new fixture for the new shape instead)")
    return problems


# ── Feature registry ───────────────────────────────────────────────────────────

def _version(v: str) -> tuple[int, ...]:
    if not re.fullmatch(r"\d+(\.\d+)*", v):
        raise ValueError(f"release '{v}' is not a dotted version")
    return tuple(int(p) for p in v.split("."))


def check_registry(head: dict[str, Any]) -> list[str]:
    """Rules that need only the head registry: well-formed histories and retirement order."""
    problems: list[str] = []
    features = {f["name"]: f for f in head.get("features", [])}
    if len(features) != len(head.get("features", [])):
        problems.append("features: duplicate names")
    current = head.get("release", "")
    try:
        current_v = _version(current)
    except ValueError as e:
        return [f"release: {e}"]

    for name, f in features.items():
        history = f.get("history", [])
        if not history:
            problems.append(f"{name}: empty history")
            continue
        if history[0]["stage"] != "offered":
            problems.append(f"{name}: history must start at 'offered'")
        for prev, nxt in zip(history, history[1:]):
            if STAGES.index(nxt["stage"]) != STAGES.index(prev["stage"]) + 1 and not (
                prev["stage"] == "offered" and nxt["stage"] == "retired" and _replaced_by(name, features)
            ):
                problems.append(f"{name}: '{prev['stage']}' -> '{nxt['stage']}' skips or reverses a stage")
            if _version(nxt["release"]) <= _version(prev["release"]):
                problems.append(f"{name}: '{nxt['stage']}' at {nxt['release']} is not after '{prev['stage']}' at {prev['release']} (one step per release)")
        for entry in history:
            if _version(entry["release"]) > current_v:
                problems.append(f"{name}: '{entry['stage']}' at {entry['release']} is later than the registry release {current}")
        if f.get("replaces") and f["replaces"] not in features:
            problems.append(f"{name}: replaces unknown feature '{f['replaces']}'")

        retired = next((e for e in history if e["stage"] == "retired"), None)
        if retired:
            successor = _replaced_by(name, features)
            source = features[successor] if successor else f
            label = f"its replacement {successor}" if successor else "it"
            required = next((e for e in source.get("history", []) if e["stage"] == "required"), None)
            if required is None or _version(required["release"]) >= _version(retired["release"]):
                problems.append(f"{name}: retired at {retired['release']} but {label} was not required in an earlier release")
    return problems


def _replaced_by(name: str, features: dict[str, Any]) -> str | None:
    return next((n for n, f in features.items() if f.get("replaces") == name), None)


def diff_registry(base: dict[str, Any], head: dict[str, Any]) -> list[str]:
    """Rules across base -> head: nothing deleted, histories append-only, versions never decrease."""
    problems = check_registry(head)
    if head.get("protocolVersion", 0) < base.get("protocolVersion", 0):
        problems.append(f"protocolVersion decreased from {base['protocolVersion']} to {head['protocolVersion']}")
    try:
        if _version(head.get("release", "0")) < _version(base.get("release", "0")):
            problems.append(f"release went backwards from {base['release']} to {head['release']}")
    except ValueError:
        pass

    head_features = {f["name"]: f for f in head.get("features", [])}
    for bf in base.get("features", []):
        hf = head_features.get(bf["name"])
        if hf is None:
            problems.append(f"{bf['name']}: deleted from the registry (retire it instead; entries are history)")
            continue
        if hf.get("history", [])[: len(bf.get("history", []))] != bf.get("history", []):
            problems.append(f"{bf['name']}: history rewritten (only append)")
        if bf.get("replaces") != hf.get("replaces"):
            problems.append(f"{bf['name']}: 'replaces' changed")
    return problems


# ── Contract storage layout ────────────────────────────────────────────────────

STRUCT = re.compile(r"\bstruct\s+(\w+)\s*\{(.*?)\}", re.S)
FIELD = re.compile(r"^([\w.\[\]]+(?:\s*\(.*\))?)\s+(?:(?:internal|private|public)\s+)?(\w+)$")
LOCATION = re.compile(r"bytes32\s+(?:private|internal)\s+constant\s+(\w*STORAGE\w*)\s*=\s*([^;]+);")


def solidity_layout(source: str) -> dict[str, Any]:
    """Storage-relevant shape of an upgradeable contract using ERC-7201 namespaced storage: every struct's
    fields in order, and the storage-location constants. (solc's linear layout is empty for these.)"""
    source = re.sub(r"//[^\n]*|/\*.*?\*/", "", source, flags=re.S)
    structs = {}
    for name, body in STRUCT.findall(source):
        fields = []
        for decl in (re.sub(r"\s+", " ", d).strip() for d in body.split(";")):
            m = FIELD.match(decl)
            if m:
                fields.append({"type": m.group(1), "name": m.group(2)})
        structs[name] = fields
    locations = {name: re.sub(r"\s+", " ", expr.strip()) for name, expr in LOCATION.findall(source)}
    return {"structs": structs, "locations": locations}


def diff_storage_layout(base: dict[str, Any], head: dict[str, Any], contract: str) -> list[str]:
    """Upgradeable contracts may only append state: struct fields keep position and type (renames are
    fine), new fields go at the end, and storage locations never change."""
    problems: list[str] = []
    for name, expr in base.get("locations", {}).items():
        if head.get("locations", {}).get(name) != expr:
            problems.append(f"{contract}: storage location {name} changed or removed (existing state would be orphaned)")
    for struct, fields in base.get("structs", {}).items():
        head_fields = head.get("structs", {}).get(struct)
        if head_fields is None:
            problems.append(f"{contract}: struct {struct} removed")
            continue
        for i, f in enumerate(fields):
            if i >= len(head_fields):
                problems.append(f"{contract}: {struct}.{f['name']} removed (fields may only be appended)")
            elif head_fields[i]["type"] != f["type"]:
                problems.append(f"{contract}: {struct} field {i} ({f['name']}) changed from {f['type']} to "
                                f"{head_fields[i]['type']} {head_fields[i]['name']} (fields may only be appended)")
    return problems


# ── Migrations ─────────────────────────────────────────────────────────────────

DESTRUCTIVE = re.compile(r"\b(DROP\s+(TABLE|COLUMN)|RENAME\s+(TO|COLUMN))\b", re.IGNORECASE)
MIGRATION_PATHS = re.compile(r"(/Metadata/|/Migrations?/|ManifestStore\.cs$|\.sql$)")


def scan_migrations(diff: str) -> list[str]:
    """Added lines in metadata/migration code that drop or rename schema objects."""
    problems: list[str] = []
    current = None
    for line in diff.splitlines():
        if line.startswith("+++ "):
            current = line[6:] if line.startswith("+++ b/") else None
        elif line.startswith("+") and current and MIGRATION_PATHS.search(current) and DESTRUCTIVE.search(line):
            problems.append(f"{current}: destructive schema change: {line[1:].strip()}")
    return problems


# ── CLI ────────────────────────────────────────────────────────────────────────

def _load(path: str) -> Any:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2
    cmd, args = argv[1], argv[2:]
    if cmd == "schema":
        problems = diff_schema(_load(args[0]), _load(args[1]))
    elif cmd == "validate":
        problems = validate(_load(args[0]), _load(args[1]))
    elif cmd == "registry":
        problems = diff_registry(_load(args[0]), _load(args[1])) if args[0] != "-" else check_registry(_load(args[1]))
    elif cmd == "storage-layout":
        problems = diff_storage_layout(_load(args[0]), _load(args[1]), args[2])
    elif cmd == "solidity-layout":
        with open(args[0], encoding="utf-8") as f:
            print(json.dumps(solidity_layout(f.read()), indent=2))
        return 0
    elif cmd == "migrations":
        problems = scan_migrations(sys.stdin.read())
    else:
        print(f"unknown command {cmd}")
        return 2
    for p in problems:
        print(f"::error::{p} (see {DOC})")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
