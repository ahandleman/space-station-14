"""Native disposal profile evidence must preserve preferred exit order."""
import unittest

from native_system_sources import audit_profiles, native_occupancy, native_carries_crew


class NativeProfileOrderTests(unittest.TestCase):
    def fixture(self):
        components = {"DisposalTube": {"exits": ["South", "West", "East"]}, "SubFloorHide": {}, "Physics": {"canCollide": False}}
        profile = {"id": "y", "prototype": "DisposalYJunction", "requiredComponents": ["DisposalTube"],
                   "ports": [{"id": "tube", "connection": "disposal", "directions": ["South", "West", "East"]}],
                   **native_occupancy(components)}
        return {"DisposalYJunction": {"components": components}}, profile

    def test_native_preferred_exit_and_fallback_order_are_preserved(self):
        native, profile = self.fixture()
        self.assertEqual(audit_profiles(native, [profile]), [])

    def test_swapping_the_same_direction_set_cannot_fabricate_a_native_preferred_exit(self):
        native, profile = self.fixture()
        profile["ports"][0]["directions"] = ["West", "South", "East"]
        self.assertTrue(any("ordered disposal directions" in error for error in audit_profiles(native, [profile])))

    def test_duplicate_exits_cannot_change_native_random_fallback_weights(self):
        native, profile = self.fixture()
        profile["ports"][0]["directions"] = ["South", "West", "East", "East"]
        self.assertTrue(any("ordered disposal directions" in error for error in audit_profiles(native, [profile])))

    def test_disposal_unit_component_facts_identify_crew_without_prototype_names(self):
        self.assertTrue(native_carries_crew({"DisposalUnit": {}}))
        self.assertFalse(native_carries_crew({"DisposalTube": {}}))

    def test_item_only_native_mail_filters_never_become_crew_travel(self):
        self.assertFalse(native_carries_crew({"DisposalUnit": {"whitelist": {"components": ["Item"]}}}))
        self.assertFalse(native_carries_crew({"DisposalUnit": {}, "MailingUnit": {}}))

    def test_declared_noncrew_metadata_cannot_hide_an_unrestricted_native_unit(self):
        components = {"DisposalUnit": {}}
        native = {"Unit": {"components": components}}
        profile = {"id": "unit", "prototype": "Unit", "requiredComponents": ["DisposalUnit"], "carriesCrew": False,
                   "ports": [{"id": "entry", "connection": "disposalUnit", "directions": []}], **native_occupancy(components)}
        self.assertTrue(any("crew carrier declaration" in error for error in audit_profiles(native, [profile])))
        profile["carriesCrew"] = True
        self.assertEqual(audit_profiles(native, [profile]), [])


if __name__ == "__main__":
    unittest.main()
