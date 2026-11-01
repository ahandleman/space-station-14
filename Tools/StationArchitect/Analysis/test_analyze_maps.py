"""Focused tests of evidence transformations; no game or full-map parse required."""
import pathlib
import sys
import unittest
import base64
import struct
import tempfile
import yaml

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from analyze_maps import (SourceLoader, decode_grids, effective_prototype, expand_variants,
                          grid_geometry, map_entities, merge_components, positions,
                          prototype_index)


class EvidenceTransforms(unittest.TestCase):
    def test_chunk_v7_uses_engine_row_order_and_seven_bytes(self):
        packed = b"".join(struct.pack("<iBBB", tile_id, 0, 0, 0) for tile_id in [4, 0, 4, 5])
        data = {"tilemap": {0: "Space", 4: "FloorSteel", 5: "Lattice"}}
        entities = {1: {"uid": 1, "components": {"MapGrid": {"chunks": {
            "-1,2": {"ind": "-1,2", "size": 2, "version": 7,
                       "tiles": base64.b64encode(packed).decode()}}}}}}
        self.assertEqual(decode_grids(data, entities), {1: {(-2, 4): "FloorSteel", (-2, 5): "FloorSteel"}})

    def test_component_override_preserves_inherited_beacon_default(self):
        base = {"NavMapBeacon": {"defaultText": "station-beacon-bridge", "color": "#ffff00"}}
        overridden = merge_components(base, {"NavMapBeacon": {"text": "Bridge Office"}})
        self.assertEqual(overridden["NavMapBeacon"]["defaultText"], "station-beacon-bridge")
        self.assertEqual(overridden["NavMapBeacon"]["text"], "Bridge Office")
        self.assertNotIn("text", base["NavMapBeacon"])

    def test_parent_cycles_and_nullspace_are_not_assigned_to_station(self):
        entities = {
            1: {"uid": 1, "components": {"Transform": {"parent": 2}}},
            2: {"uid": 2, "components": {"Transform": {"parent": 1}}},
            3: {"uid": 3, "components": {"Transform": {"parent": 99, "pos": "3.5,4.5"}}},
            4: {"uid": 4, "components": {"Transform": {"parent": 1234}}}}
        result = positions(entities, {99: {}})
        self.assertIsNone(result[1])
        self.assertIsNone(result[2])
        self.assertIsNone(result[4])
        self.assertEqual(result[3], (99, 3.5, 4.5))

    def test_closed_door_components_are_separate_and_reference_real_door(self):
        tiles = {(x, 0): "FloorSteel" for x in range(5)}
        door = {"uid": 10, "prototype": "Airlock", "components": {
            "Transform": {"parent": 99}, "Door": {}, "AccessReader": {"access": [["Engineering"]]}}}
        regions, by_cell, boundaries = grid_geometry(tiles, {10: door}, {10: (99, 2.5, .5)}, 99)
        self.assertEqual(sorted(region["area"] for region in regions), [2, 2])
        self.assertNotEqual(by_cell[0, 0], by_cell[4, 0])
        self.assertEqual(boundaries[0]["uid"], 10)
        self.assertEqual(boundaries[0]["access"], [["Engineering"]])

    def test_tagged_variants_expand_ids_parent_and_nested_sequences_together(self):
        source = yaml.load("""
type: entity
id: !type:CreateVariants
  values: [GasMachine, GasMachineAlt]
parent: !type:CreateVariants
  values: [GasBase, GasBaseAlt]
components:
  - type: Test
    number: !type:CreateVariants
      values: [0, 1]
    layers: !type:CreateVariants
      sequences: [[base], [alternate, overlay]]
""", Loader=SourceLoader)
        first, second = expand_variants(source)
        self.assertEqual(first["id"], "GasMachine")
        self.assertEqual(second["id"], "GasMachineAlt")
        self.assertEqual(second["parent"], "GasBaseAlt")
        self.assertEqual(second["components"][0]["number"], 1)
        self.assertEqual(second["components"][0]["layers"], ["alternate", "overlay"])
        self.assertEqual(source["id"]["values"], ["GasMachine", "GasMachineAlt"])

    def test_variant_shaped_untagged_mapping_is_not_guessed_as_runtime_instruction(self):
        source = {"type": "entity", "id": {"values": ["One", "Two"]}}
        self.assertEqual(expand_variants(source), [source])

    def test_variant_fields_must_cover_all_id_indices(self):
        source = yaml.load("""
type: entity
id: !type:CreateVariants
  values: [One, Two]
name: !type:CreateVariants
  values: [First]
""", Loader=SourceLoader)
        with self.assertRaises(ValueError):
            expand_variants(source)

    def test_entity_index_includes_catalog_fills_and_generated_variants(self):
        with tempfile.TemporaryDirectory(prefix="station-evidence-test-") as temporary:
            repo = pathlib.Path(temporary)
            entities = repo / "Resources/Prototypes/Entities"
            fills = repo / "Resources/Prototypes/Catalog/Fills"
            entities.mkdir(parents=True)
            fills.mkdir(parents=True)
            (entities / "base.yml").write_text("""
- type: entity
  id: BaseLocker
  components:
  - type: Storage
    grid: 4
""", encoding="utf-8")
            (fills / "filled.yml").write_text("""
- type: entity
  parent: BaseLocker
  id: FilledLocker
  components:
  - type: ContainerFill
- type: entity
  parent: BaseLocker
  id: !type:CreateVariants
    values: [VariantLocker, VariantLockerAlt]
""", encoding="utf-8")
            result = prototype_index(repo)
            self.assertIn("FilledLocker", result)
            self.assertIn("VariantLockerAlt", result)
            self.assertEqual(result["FilledLocker"]["components"]["Storage"]["grid"], 4)
            self.assertEqual(result["VariantLockerAlt"]["sourcePath"], "Resources/Prototypes/Catalog/Fills/filled.yml")

    def test_map_migration_uses_effective_defaults_and_preserves_raw_id(self):
        prototypes = {"NewTray": {"components": {"PlantHolder": {}}, "name": "tray", "ancestors": []}}
        data = {"entities": [{"proto": "oldTray", "entities": [{"uid": 1}]},
                             {"proto": "Deleted", "entities": [{"uid": 2}]}]}
        entities = map_entities(data, prototypes, {"oldTray": "NewTray", "Deleted": None})
        self.assertEqual(entities[1]["prototype"], "NewTray")
        self.assertEqual(entities[1]["rawPrototype"], "oldTray")
        self.assertIn("PlantHolder", entities[1]["components"])
        self.assertNotIn(2, entities)
        self.assertEqual(effective_prototype("A", {"A": "B", "B": "C"}), "B")


if __name__ == "__main__":
    unittest.main()
