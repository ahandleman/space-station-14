"""Source-backed profile audit regressions; run with Python unittest."""
import copy
import json
import unittest
from pathlib import Path

from native_system_sources import audit_profiles, prototypes


class NativeProfileFactsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = Path(__file__).resolve().parents[3]
        cls.native = prototypes(root)
        cls.profiles = json.loads((root / "Tools/StationArchitect/Catalog/native-profiles.json").read_text(encoding="utf-8"))["profiles"]

    def test_real_library_matches_resolved_native_facts(self):
        self.assertEqual([], audit_profiles(self.native, self.profiles))

    def test_smes_input_cannot_be_redeclared_as_same_tile_cable(self):
        profile = copy.deepcopy(next(profile for profile in self.profiles if profile["id"] == "smesbasic"))
        profile["ports"][0]["connection"] = "cableDevice"
        self.assertTrue(any("native CableTerminalPortNode" in error for error in audit_profiles(self.native, [profile])))

    def test_pipe_layer_is_not_proved_by_its_own_declaration(self):
        profile = copy.deepcopy(next(profile for profile in self.profiles if profile["id"] == "gas-0-straight"))
        profile["ports"][0]["pipeLayer"] = 2
        self.assertTrue(any("native 0" in error for error in audit_profiles(self.native, [profile])))

    def test_battery_transfer_requires_its_native_transfer_components(self):
        profile = copy.deepcopy(next(profile for profile in self.profiles if profile["id"] == "apc"))
        profile["requiredComponents"].remove("BatteryCharger")
        self.assertTrue(any("BatteryCharger" in error for error in audit_profiles(self.native, [profile])))

    def test_an_ordinary_airlock_cannot_claim_firelock_function(self):
        profile = copy.deepcopy(next(profile for profile in self.profiles if profile["id"] == "firelock"))
        profile["prototype"] = "Airlock"
        self.assertTrue(any("lacks required Firelock" in error for error in audit_profiles(self.native, [profile])))

    def test_native_noncolliding_sensor_is_not_a_solid_floor_obstacle(self):
        profile = copy.deepcopy(next(profile for profile in self.profiles if profile["id"] == "air-sensor"))
        self.assertEqual("sensor", profile["occupancyLayer"])
        self.assertFalse(profile["blocksCrew"])
        profile["blocksCrew"] = True
        self.assertTrue(any("blocksCrew" in error for error in audit_profiles(self.native, [profile])))


if __name__ == "__main__":
    unittest.main()
