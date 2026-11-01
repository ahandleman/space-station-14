"""Independent serialized-data regressions for docking clearance audits."""
import copy
import unittest

from audit_design_export import audit_docking, preserve_native_shape_types, SourceLoader
import yaml


def fixture():
    point = lambda x, y: {"x": x, "y": y}
    anchor, normal = (5, 4), (1, 0)
    berth = [point(x, y) for x in range(6, 9) for y in range(3, 6)]
    approach = [point(x, y) for x in range(9, 13) for y in range(3, 6)]
    rule = {"id": "Dock", "contentId": "Hardware", "berthWidth": 3, "berthDepth": 3, "berthOffset": 0, "clearance": 0, "approachLength": 2, "approachWidth": 0, "directions": ["east"]}
    content = {"id": "Hardware", "prototypes": ["NativeDock"], "placement": "spaceBoundary", "interfaceType": "dockingPort", "count": {"min": 1, "target": 1, "max": 1}}
    equipment = {"id": "Team/Room#1/Hardware#1", "roomId": "Team/Room#1", "requirementId": "Hardware", "prototype": "NativeDock", "position": point(*anchor), "rotation": 90, "footprint": [point(*anchor)], "interfaceId": "port"}
    port = {"id": "port", "type": "dockingPort", "kind": "walking", "from": "Team/Room#1", "to": "$space", "cells": [point(*anchor)], "fromApproach": point(4, 4), "toApproach": point(6, 4)}
    dock = {"id": "planned-dock", "roomId": "Team/Room#1", "requirementId": "Dock", "contentId": "Hardware", "instance": 1, "boundaryCells": [point(*anchor)], "outward": point(*normal), "berthCells": berth, "approachCells": approach, "entityId": equipment["id"], "interfaceId": "port"}
    design = {"definition": {"departments": [{"id": "Team", "rooms": [{"id": "Room", "contents": [content], "exterior": {"docks": [rule]}}]}]}, "settings": {"width": 12, "height": 10}, "layout": {"rooms": [{"id": "Team/Room#1", "definitionPath": "Team/Room", "enabled": True, "orphaned": False, "cells": [point(4, 4)]}], "equipment": [equipment], "interfaces": [port], "docks": [dock]}}
    manifest = {"dockingReservations": [{**copy.deepcopy(dock), "nativeDockingComponentsMaterialized": True, "geometryAndHardwareAccepted": True}]}
    prototypes = {"NativeDock": {"components": {"Door": {}, "Docking": {"dockLegendCategory": "Arrivals"}, "DockingSignalControl": {}, "PriorityDock": {"tag": "DockArrivals"}, "Fixtures": {"fixtures": {"body": {"shape": {"bounds": "-0.49,-0.49,0.49,0.49"}}}}}}}
    entities = {2: {"prototype": "", "components": []}, 3: {"prototype": "NativeDock", "components": [{"type": "Transform", "parent": 2, "pos": "5.5,4.5", "rot": "90 deg"}, {"type": "Docking"}, {"type": "DockingSignalControl"}]}}
    return design, manifest, {(4, 4): "FloorSteel", (5, 4): "FloorSteel"}, entities, prototypes


class DockingExportAuditTests(unittest.TestCase):
    def check(self, values):
        errors, warnings = [], []
        result = audit_docking(*values, 2, errors, warnings)
        return result, errors

    def test_exact_native_hardware_clear_lane_and_inherited_priority_pass(self):
        result, errors = self.check(fixture())
        self.assertEqual(errors, [])
        self.assertTrue(result[0]["geometryAndHardwareVerified"])

    def test_serialized_plating_inside_a_saved_empty_berth_is_rejected(self):
        values = fixture(); values[2][(6, 4)] = "Lattice"
        self.assertTrue(any("non-space tiles" in error for error in self.check(values)[1]))

    def test_serialized_gear_in_approach_is_rejected(self):
        values = fixture(); values[3][4] = {"prototype": "Machine", "components": [{"type": "Transform", "parent": 2, "pos": "10.5,4.5"}]}
        self.assertTrue(any("serialized entities" in error for error in self.check(values)[1]))

    def test_adjacent_native_collider_cannot_intrude_from_outside_reserved_tiles(self):
        values = fixture(); values[4]["WideBody"] = {"components": {"Fixtures": {"fixtures": {"body": {"shape": {"bounds": "-0.9,-0.9,0.9,0.9"}}}}}}
        values[3][4] = {"prototype": "WideBody", "components": [{"type": "Transform", "parent": 2, "pos": "6.5,2.5"}]}
        self.assertTrue(any("native collider extents" in error for error in self.check(values)[1]))

    def test_wrong_docking_rotation_is_rejected(self):
        values = fixture(); values[3][3]["components"][0]["rot"] = "0 deg"
        self.assertTrue(any("prototype/position/rotation" in error for error in self.check(values)[1]))

    def test_narrowed_saved_masks_cannot_hide_an_authored_berth_obstacle(self):
        values = fixture(); values[0]["layout"]["docks"][0]["berthCells"] = [{"x": 6, "y": 3}]; values[2][(8, 5)] = "FloorSteel"
        errors = self.check(values)[1]
        self.assertTrue(any("independently reconstructed" in error for error in errors))
        self.assertTrue(any("non-space tiles" in error for error in errors))

    def test_an_exterior_wall_beyond_canvas_blocks_the_full_lane(self):
        values = fixture(); values[2][(15, 4)] = "FloorSteel"
        self.assertTrue(any("beyond the project canvas" in error for error in self.check(values)[1]))

    def test_noncolliding_beacon_does_not_obstruct_the_interior_standing_tile(self):
        values = fixture()
        values[4]["Beacon"] = {"components": {"Physics": {"canCollide": False}, "Fixtures": {"fixtures": {"body": {"shape": {"bounds": "-0.25,-0.25,0.25,0.25"}}}}}}
        values[3][4] = {"prototype": "Beacon", "components": [{"type": "Transform", "parent": 2, "pos": "4.5,4.5"}]}
        result, errors = self.check(values)
        self.assertEqual(errors, [])
        self.assertTrue(result[0]["geometryAndHardwareVerified"])

    def test_missing_or_tagged_default_unit_fixture_blocks_standing_space(self):
        preserve_native_shape_types()
        tagged = yaml.load("shape: !type:PhysShapeAabb {}", Loader=SourceLoader)
        for body in [{}, tagged]:
            with self.subTest(body=body):
                values = fixture()
                values[4]["BlockingBody"] = {"components": {"Physics": {"bodyType": "Static"}, "Fixtures": {"fixtures": {"body": {"layer": ["Impassable"], **body}}}}}
                values[3][4] = {"prototype": "BlockingBody", "components": [{"type": "Transform", "parent": 2, "pos": "4.5,4.5"}]}
                errors = self.check(values)[1]
                self.assertTrue(any("clear owned interior floor approach" in error for error in errors))
                self.assertFalse(any("unsupported native collider" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
