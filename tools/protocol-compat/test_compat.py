"""Tests for compat.py (issue #146 QA TC-7..TC-27). Run: python3 -m unittest discover tools/protocol-compat"""

import copy
import unittest

import compat

SCHEMA = {
    "type": "object",
    "required": ["node"],
    "properties": {
        "node": {"type": "string"},
        "storage": {"type": ["object", "null"], "properties": {"grpc": {"type": ["string", "null"]}, "tlsNotAfter": {"type": ["integer", "null"]}}},
        "features": {"type": "array", "items": {"type": "string"}},
    },
}


def registry(*features, release="0.2.0", version=1):
    return {"release": release, "protocolVersion": version, "features": list(features)}


def feature(name, *history, replaces=None):
    f = {"name": name, "history": [{"stage": s, "release": r} for s, r in history]}
    if replaces:
        f["replaces"] = replaces
    return f


class SchemaTests(unittest.TestCase):
    def diff(self, mutate):
        head = copy.deepcopy(SCHEMA)
        mutate(head)
        return compat.diff_schema(SCHEMA, head)

    def test_removed_property_fails_with_its_path(self):
        problems = self.diff(lambda s: s["properties"]["storage"]["properties"].pop("tlsNotAfter"))
        self.assertEqual(problems, ["$.storage.tlsNotAfter: property removed"])

    def test_retyped_or_non_nullable_fails(self):
        self.assertTrue(self.diff(lambda s: s["properties"]["node"].update(type="integer")))
        self.assertTrue(self.diff(lambda s: s["properties"]["storage"]["properties"]["grpc"].update(type="string")))

    def test_newly_required_fails(self):
        self.assertEqual(self.diff(lambda s: s["required"].append("storage")), ["$.storage: became required"])

    def test_closing_the_object_fails(self):
        self.assertTrue(self.diff(lambda s: s.update(additionalProperties=False)))

    def test_array_item_change_detected(self):
        self.assertTrue(self.diff(lambda s: s["properties"]["features"]["items"].update(type="integer")))

    def test_adding_an_optional_property_or_widening_a_type_passes(self):
        self.assertEqual(self.diff(lambda s: s["properties"].update(protocolVersion={"type": "integer"})), [])
        self.assertEqual(self.diff(lambda s: s["properties"]["node"].update(type=["string", "null"])), [])

    def test_validate(self):
        self.assertEqual(compat.validate({"node": "0x1", "storage": None}, SCHEMA), [])
        self.assertTrue(compat.validate({"storage": None}, SCHEMA))
        self.assertTrue(compat.validate({"node": "0x1", "storage": {"tlsNotAfter": "soon"}}, SCHEMA))
        self.assertEqual(compat.validate({"node": "0x1", "unknown": 1}, SCHEMA), [])  # unknown fields are tolerated


class FixtureTests(unittest.TestCase):
    def test_deleted_or_edited_fixture_fails_added_passes(self):
        base = {"a.json": '{"x": 1}'}
        self.assertEqual(compat.diff_fixtures(base, {}), ["a.json: fixture deleted"])
        self.assertTrue(compat.diff_fixtures(base, {"a.json": '{"x": 2}'}))
        self.assertEqual(compat.diff_fixtures(base, {"a.json": '{ "x": 1 }', "b.json": "{}"}), [])


class RegistryTests(unittest.TestCase):
    TLS = feature("storage.grpc.tls", ("offered", "0.1.0"), replaces="storage.grpc.plaintext")
    PLAIN = feature("storage.grpc.plaintext", ("offered", "0.1.0"))
    BASE = registry(PLAIN, TLS, release="0.1.0")

    def test_current_registry_passes(self):
        import json, os
        path = os.path.join(os.path.dirname(__file__), "..", "..", "protocol", "features.json")
        with open(path) as f:
            self.assertEqual(compat.check_registry(json.load(f)), [])

    def test_deleting_a_feature_fails(self):
        self.assertIn("deleted", " ".join(compat.diff_registry(self.BASE, registry(self.TLS))))

    def test_stage_backwards_or_rewritten_fails(self):
        head = registry(feature("storage.grpc.plaintext", ("required", "0.1.0")), self.TLS)
        self.assertTrue(compat.diff_registry(self.BASE, head))

    def test_skipping_required_without_a_replacement_fails(self):
        head = registry(self.PLAIN, feature("storage.grpc.tls", ("offered", "0.1.0"), ("retired", "0.2.0"), replaces="storage.grpc.plaintext"))
        self.assertTrue(any("skips" in p for p in compat.diff_registry(self.BASE, head)))

    def test_two_steps_in_one_release_fails(self):
        head = registry(self.PLAIN, feature("storage.grpc.tls", ("offered", "0.1.0"), ("required", "0.1.0"), replaces="storage.grpc.plaintext"))
        self.assertTrue(any("one step per release" in p for p in compat.diff_registry(self.BASE, head)))

    def test_retiring_legacy_in_the_release_its_replacement_became_required_fails(self):
        tls = feature("storage.grpc.tls", ("offered", "0.1.0"), ("required", "0.2.0"), replaces="storage.grpc.plaintext")
        plain = feature("storage.grpc.plaintext", ("offered", "0.1.0"), ("retired", "0.2.0"))
        problems = compat.diff_registry(self.BASE, registry(plain, tls))
        self.assertTrue(any("not required in an earlier release" in p for p in problems))

    def test_retiring_legacy_a_release_after_its_replacement_became_required_passes(self):
        tls = feature("storage.grpc.tls", ("offered", "0.1.0"), ("required", "0.2.0"), replaces="storage.grpc.plaintext")
        plain = feature("storage.grpc.plaintext", ("offered", "0.1.0"), ("retired", "0.3.0"))
        self.assertEqual(compat.diff_registry(self.BASE, registry(plain, tls, release="0.3.0")), [])

    def test_valid_one_step_promotion_passes(self):
        tls = feature("storage.grpc.tls", ("offered", "0.1.0"), ("required", "0.2.0"), replaces="storage.grpc.plaintext")
        self.assertEqual(compat.diff_registry(self.BASE, registry(self.PLAIN, tls)), [])

    def test_version_or_release_decrease_fails(self):
        self.assertTrue(compat.diff_registry(registry(version=2), registry(version=1)))
        self.assertTrue(compat.diff_registry(registry(release="0.2.0"), registry(release="0.1.0")))

    def test_stage_after_the_registry_release_fails(self):
        head = registry(feature("x.y", ("offered", "0.9.0")), release="0.2.0")
        self.assertTrue(compat.check_registry(head))


class StorageLayoutTests(unittest.TestCase):
    SOURCE = """
    contract C is GovernedUpgradeable {
        /// @custom:storage-location erc7201:symposia.C
        struct CStorage {
            address owner; // who
            mapping(bytes32 => Rec) recs;
        }
        struct Rec { uint64 at; bool done; }
        bytes32 private constant C_STORAGE_LOCATION = keccak256("symposia.storage.C");
    }"""

    def diff(self, old, new):
        return compat.diff_storage_layout(compat.solidity_layout(self.SOURCE.replace(old, old)), compat.solidity_layout(self.SOURCE.replace(old, new)), "C")

    def test_parses_structs_and_locations(self):
        layout = compat.solidity_layout(self.SOURCE)
        self.assertEqual([f["name"] for f in layout["structs"]["CStorage"]], ["owner", "recs"])
        self.assertEqual(layout["structs"]["Rec"][0]["type"], "uint64")
        self.assertIn("C_STORAGE_LOCATION", layout["locations"])

    def test_appending_and_renaming_pass(self):
        self.assertEqual(self.diff("bool done; }", "bool done; uint8 extra; }"), [])
        self.assertEqual(self.diff("address owner;", "address admin;"), [])

    def test_retyping_inserting_removing_or_moving_location_fails(self):
        self.assertTrue(self.diff("uint64 at;", "uint128 at;"))
        self.assertTrue(self.diff("struct Rec { uint64 at;", "struct Rec { bool first; uint64 at;"))
        self.assertTrue(self.diff(" bool done; }", " }"))
        self.assertTrue(self.diff('"symposia.storage.C"', '"symposia.storage.C2"'))


class MigrationTests(unittest.TestCase):
    def test_added_destructive_statement_in_metadata_code_fails(self):
        diff = "+++ b/BlobStorage/Gw/Metadata/Store.cs\n+    cmd.CommandText = \"ALTER TABLE objects DROP COLUMN etag\";\n"
        self.assertEqual(len(compat.scan_migrations(diff)), 1)

    def test_removed_lines_other_paths_and_safe_sql_pass(self):
        self.assertEqual(compat.scan_migrations("+++ b/BlobStorage/Gw/Metadata/Store.cs\n-DROP TABLE old\n"), [])
        self.assertEqual(compat.scan_migrations("+++ b/docs/x.md\n+DROP TABLE old\n"), [])
        self.assertEqual(compat.scan_migrations("+++ b/BlobStorage/Gw/Metadata/Store.cs\n+ALTER TABLE t ADD COLUMN x TEXT\n"), [])


if __name__ == "__main__":
    unittest.main()
