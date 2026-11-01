"""Authoring tests use small invented names, never a built-in station vocabulary."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import jsonschema
import yaml


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("architect3_validate", ROOT / "validate.py")
V = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(V)


def count(low=1, preferred=1, high=1):
    return {"min": low, "preferred": preferred, "max": high}


def metric(identifier, kind="area.exclusive", low=8, preferred=12, high=20, **kw):
    return {"id": identifier, "type": "metric", "parameters": {
        "metric": kind, "min": low, "preferred": preferred, "max": high, "scale": 1}, **kw}


def room(identifier, **overrides):
    n = {"id": identifier, "name": identifier, "kind": "room", "count": count(),
         "placement": {"nesting": "owned", "zone": "interior", "grid": "main"},
         "environment": "breathable", "access": "crew", "tags": [],
         "requirements": [metric("floor")], "features": [], "spaces": []}
    n.update(overrides)
    return n


def feature(identifier, direction="bidirectional", system="gas", medium="loop"):
    return {"id": identifier, "description": "Abstract named service endpoint.", "count": count(),
            "prototypes": [], "ports": [{"id": "port", "system": system, "medium": medium,
                                         "direction": direction, "grid": "host"}],
            "clearance": {"workingTiles": 0, "approachWidth": 0}}


def edge(identifier="link", typ="route", source=None, target=None, **overrides):
    params = {
        "route": {"mode": "crew", "accessProfile": "crew", "minWidth": 2,
                  "through": [], "within": "owner", "avoid": []},
        "shared_boundary": {"interface": "counter", "minLength": 2, "continuous": True},
        "dependency": {"reason": "Selected source needs support."},
        "service_link": {"system": "gas", "medium": "loop", "fromPort": "port", "toPort": "port",
                         "sameGrid": True, "support": "floor_or_lattice", "distinctCircuit": True},
        "grid_relation": {"relation": "same"},
        "separation": {"metric": "boundary_manhattan", "minDistance": 2},
    }
    result = {"id": identifier, "type": typ,
              "from": source or {"space": "division/a"}, "to": target or {"space": "division/b"},
              "matching": {"targets": "one", "scope": "same_owner", "presence": "require_target"},
              "parameters": copy.deepcopy(params.get(typ, {}))}
    result.update(overrides)
    return result


def program():
    return {"format": "ss14-station-architect", "schemaVersion": 3, "id": "test_program", "name": "Test",
            "defaults": {"requirement": {"priority": "mandatory", "weight": 100}},
            "accessProfiles": [{"id": "crew", "description": "Ordinary crew.", "levels": [], "canTraverseVacuum": False},
                               {"id": "suited", "description": "EVA crew.", "levels": ["Engineering"], "canTraverseVacuum": True}],
            "environments": [{"id": "breathable", "description": "Air.", "pressure": "standard", "enclosure": "sealed", "temperature": "ambient"},
                             {"id": "vacuum", "description": "Space.", "pressure": "vacuum", "enclosure": "open", "temperature": "cold"}],
            "grids": [{"id": "main", "description": "Main grid.", "kind": "station", "support": "mixed"},
                      {"id": "remote", "description": "Another grid.", "kind": "detached", "support": "solid"}],
            "domains": [{"id": "space", "description": "Exterior vacuum.", "kind": "vacuum", "connectedToExterior": True}],
            "interfaces": [
                {"id": "counter", "description": "Service counter.", "capabilities": ["item_transfer", "speech"],
                 "construction": ["SomeCounter"], "normalState": "closed", "access": {"anyOf": []}, "airtight": False},
                {"id": "dock", "description": "Docking interface.", "capabilities": ["crew_passage", "docking"],
                 "construction": ["SomeDock"], "normalState": "controlled", "access": {"anyOf": [["Engineering"], ["Command", "Cargo"]]}, "airtight": True}],
            "spaces": [{"id": "division", "name": "Division", "kind": "department", "count": count(),
                        "placement": {"nesting": "owned", "zone": "any", "grid": "main"},
                        "requirements": [metric("budget", "area.aggregate", 16, 24, 40)],
                        "spaces": [room("a"), room("b")]}],
            "relationships": [edge()], "choices": [], "assemblies": [], "extensions": [],
            "evidence": [{"id": "authored", "kind": "policy", "source": "test policy", "description": "Authored bounds."}]}


class ProgramTests(unittest.TestCase):
    def valid(self, doc):
        self.assertEqual([], V.validate_document(doc))

    def invalid(self, doc, code=None):
        ds = V.validate_document(doc)
        self.assertTrue(ds, "Expected an invalid document")
        if code:
            self.assertIn(code, {d["code"] for d in ds}, ds)
        return ds

    def test_schema_is_valid_and_minimal_generic_program_compiles(self):
        jsonschema.Draft202012Validator.check_schema(json.loads(V.SCHEMA_PATH.read_text()))
        d = program()
        self.valid(d)
        compiled = V.compile_document(d)
        self.assertTrue(compiled["authoringValid"])
        self.assertEqual("not_evaluated", compiled["certification"]["geometry"])
        self.assertEqual("not_evaluated", compiled["certification"]["runtimeOperation"])
        self.assertEqual("division", compiled["tree"][0]["children"][0]["logicalOwner"])
        self.assertEqual("division/b", compiled["relationships"][0]["sourceBindings"][0]["candidates"][0]["endpoint"])

    def test_compilation_is_deterministic_and_does_not_mutate_authoring(self):
        d = program(); before = copy.deepcopy(d)
        self.assertEqual(V.compile_document(d), V.compile_document(d))
        self.assertEqual(before, d)

    def test_empty_collections_and_default_rules_are_explicitly_supported(self):
        d = {"format": "ss14-station-architect", "schemaVersion": 3, "id": "empty", "name": "Empty architecture"}
        self.valid(d)
        self.assertEqual([], V.compile_document(d)["tree"])

    def test_unknown_root_and_nested_fields_are_rejected(self):
        for mutate in (lambda d: d.update({"layout": {}}),
                       lambda d: d["spaces"][0]["spaces"][0]["placement"].update({"rectangle": True}),
                       lambda d: d["relationships"][0]["parameters"].update({"magic": 1})):
            with self.subTest(mutate=mutate):
                d = program(); mutate(d); self.invalid(d, "schema")

    def test_types_booleans_and_nonfinite_values_fail(self):
        for value in (True, -1, 1.5, "1"):
            d = program(); d["spaces"][0]["count"]["min"] = value
            self.invalid(d, "schema")
        d = program(); d["relationships"][0]["weight"] = float("nan")
        self.invalid(d, "non-finite")

    def test_count_and_metric_bounds_are_ordered(self):
        d = program(); d["spaces"][0]["count"] = count(2, 1, 3)
        self.invalid(d, "bounds")
        d = program(); d["spaces"][0]["spaces"][0]["requirements"][0]["parameters"]["max"] = 4
        self.invalid(d, "bounds")

    def test_duplicates_rejected_in_every_identity_scope(self):
        for collection in ("grids", "interfaces", "accessProfiles", "relationships", "evidence"):
            d = program(); d[collection].append(copy.deepcopy(d[collection][0]))
            self.invalid(d, "duplicate-id")
        for key in ("spaces", "requirements", "features"):
            d = program(); n = d["spaces"][0]
            if key == "features":
                n = n["spaces"][0]; n[key] = [feature("machine")]
            n[key].append(copy.deepcopy(n[key][0])); self.invalid(d, "duplicate-id")

    def test_repeated_local_names_in_different_suites_are_valid(self):
        d = program(); dep = d["spaces"][0]
        for n in dep["spaces"]:
            n["spaces"] = [room("inside", placement={"nesting": "contained", "zone": "interior", "grid": "main"})]
        dep["requirements"][0]["parameters"]["max"] = 100
        self.valid(d)

    def test_unknown_paths_rejected_even_when_optional(self):
        d = program(); d["relationships"][0]["to"] = {"space": "division/absent"}
        d["relationships"][0]["matching"]["presence"] = "if_both_present"
        self.invalid(d, "reference")

    def test_endpoint_has_exactly_one_kind(self):
        d = program(); d["relationships"][0]["from"]["domain"] = "space"
        self.invalid(d, "schema")

    def test_selector_filters_intersect_and_preserve_all_candidates(self):
        d = program(); nodes = d["spaces"][0]["spaces"]
        nodes[0]["tags"] = ["staff", "service"]; nodes[1]["tags"] = ["staff", "public"]
        d["relationships"][0]["from"] = {"selector": {"tagsAll": ["staff"], "tagsAny": ["service"], "kinds": ["room"], "within": "division"}}
        self.valid(d)
        self.assertEqual("division/a", V.compile_document(d)["relationships"][0]["sourceBindings"][0]["source"])
        d["relationships"][0]["from"] = {"selector": {}}
        self.invalid(d, "schema")

    def test_aggregate_is_not_a_physical_endpoint(self):
        for typ in ("route", "shared_boundary", "grid_relation"):
            d = program(); d["relationships"] = [edge(typ=typ, source={"space": "division"})]
            self.invalid(d, "endpoint-kind")

    def test_domain_has_no_crew_floor_or_injective_instances(self):
        d = program(); d["relationships"][0]["to"] = {"domain": "space"}
        self.invalid(d, "endpoint-kind")
        d["relationships"] = [edge(typ="shared_boundary", target={"domain": "space"})]
        d["relationships"][0]["matching"]["scope"] = "station"
        self.valid(d)
        d["relationships"][0]["matching"]["targets"] = "distinct"
        self.invalid(d, "domain-cardinality")

    def test_nested_containment_requires_room_parent_and_same_grid(self):
        d = program(); child = d["spaces"][0]["spaces"][0]
        child["placement"]["nesting"] = "contained"
        self.invalid(d, "containment")
        d = program(); parent = d["spaces"][0]["spaces"][0]
        parent["spaces"] = [room("inside", placement={"nesting": "contained", "zone": "interior", "grid": "remote"})]
        self.invalid(d, "containment-grid")

    def test_nested_area_is_exclusive_plus_children_once_and_counts_per_parent(self):
        d = program(); parent = d["spaces"][0]["spaces"][0]
        parent["count"] = count(2, 2, 2)
        parent["spaces"] = [room("inside", count=count(3, 3, 3))]
        d["spaces"][0]["requirements"] = [metric("budget", "area.aggregate", 1, 100, 1000)]
        self.valid(d)
        c = V.compile_document(d)
        self.assertEqual(32, c["tree"][0]["children"][0]["areaBounds"]["aggregate"]["min"])
        self.assertEqual(72, c["tree"][0]["areaBounds"]["aggregate"]["min"])
        self.assertEqual([2, 3], [v["count"]["min"] for v in c["tree"][0]["children"][0]["children"][0]["instanceVariables"][-2:]])

    def test_ancestor_budget_cannot_hide_child_floor(self):
        d = program(); a = d["spaces"][0]["spaces"][0]
        a["spaces"] = [room("inside")]
        d["spaces"][0]["requirements"][0]["parameters"] = {"metric": "area.aggregate", "min": 16, "max": 20, "scale": 1}
        self.invalid(d, "area-infeasible")

    def test_multiple_mandatory_area_rules_intersect(self):
        d = program(); a = d["spaces"][0]["spaces"][0]
        a["requirements"].append(metric("other", low=30, preferred=35, high=40))
        self.invalid(d, "area-infeasible")

    def test_desired_bounds_do_not_become_mandatory_area_constraints(self):
        d = program(); a = d["spaces"][0]["spaces"][0]
        a["requirements"].append(metric("wish", low=30, preferred=35, high=40, priority="desired"))
        self.valid(d)

    def test_aggregate_shape_and_empty_blocking_interface_are_valid(self):
        d = program()
        d["spaces"][0]["requirements"].append(metric("split", "shape.components", 1, 1, 3))
        d["interfaces"][0]["capabilities"] = []
        self.valid(d)

    def test_supported_surface_is_independent_from_vacuum_and_grid_identity(self):
        d = program(); n = d["spaces"][0]["spaces"][1]
        n["environment"] = "vacuum"; n["placement"]["surface"] = "lattice"
        d["relationships"][0]["parameters"]["accessProfile"] = "suited"
        self.valid(d)
        c = V.compile_document(d)
        self.assertEqual("lattice", c["tree"][0]["children"][1]["placement"]["surface"])
        self.assertEqual("main", c["tree"][0]["children"][1]["grid"])
        n["placement"]["surface"] = "space"
        self.invalid(d, "schema")
        n["placement"]["surface"] = "floor"; d["grids"][0]["support"] = "lattice"
        self.invalid(d, "surface-grid")

    def test_choice_minimum_contributes_real_descendant_area(self):
        d = program(); dep = d["spaces"][0]
        for n in dep["spaces"]: n["count"] = count(0, 1, 1)
        d["relationships"] = []
        d["choices"] = [{"id": "family", "members": ["division/a", "division/b"], "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
        dep["requirements"] = [metric("tiny", "area.aggregate", 1, 3, 4)]
        self.invalid(d, "area-infeasible")
        dep["requirements"] = [metric("fit", "area.aggregate", 1, 10, 20)]
        self.valid(d)
        self.assertEqual(8, V.compile_document(d)["tree"][0]["areaBounds"]["aggregate"]["min"])

    def test_choice_maximum_caps_possible_aggregate_area(self):
        d = program(); dep = d["spaces"][0]
        for n in dep["spaces"]: n["count"] = count(0, 1, 1)
        d["relationships"] = []
        d["choices"] = [{"id": "family", "members": ["division/a", "division/b"], "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
        dep["requirements"] = [metric("too_large", "area.aggregate", 30, 35, 40)]
        self.invalid(d, "area-infeasible")

    def test_overlapping_choices_do_not_double_count_selected_floor(self):
        d = program(); dep = d["spaces"][0]; dep["spaces"] += [room("c"), room("d")]
        for n in dep["spaces"]: n["count"] = count(0, 1, 1)
        dep["requirements"] = [metric("fit", "area.aggregate", 1, 10, 20)]
        d["relationships"] = []
        d["choices"] = [{"id": ident, "members": ["division/" + m for m in members], "minSelected": 1, "preferredSelected": 1, "maxSelected": 2}
                        for ident, members in (("left", ["a", "b"]), ("right", ["c", "d"]), ("bridge", ["b", "c"]))]
        # Select b+c: 16 minimum floor satisfies all three overlapping choices.
        self.valid(d)

    def test_shared_border_lengths_and_segments_are_not_path_lengths(self):
        d = program(); d["relationships"] = [edge(typ="shared_boundary")]
        self.valid(d)
        c = V.compile_document(d)
        self.assertEqual(2, c["relationships"][0]["parameters"]["minLength"])
        d["relationships"][0]["parameters"]["maxSegments"] = 2
        self.invalid(d, "boundary-segments")
        d["relationships"][0]["parameters"].update(continuous=False, minLength=3, maxLength=2)
        self.invalid(d, "bounds")

    def test_crew_route_cannot_use_vacuum_or_eva_without_capability(self):
        d = program(); d["spaces"][0]["spaces"][1]["environment"] = "vacuum"
        self.invalid(d, "vacuum-access")
        d["relationships"][0]["parameters"]["accessProfile"] = "suited"
        self.valid(d)

    def test_route_waypoints_cannot_be_unknown_or_simultaneously_avoided(self):
        d = program(); a = d["relationships"][0]["parameters"]
        a["through"] = ["division/a"]; a["avoid"] = ["division/a"]
        self.invalid(d, "route-conflict")
        a["avoid"] = []; a["through"] = ["division/missing"]
        self.invalid(d, "reference")

    def test_service_links_require_real_declared_typed_directed_ports(self):
        d = program(); nodes = d["spaces"][0]["spaces"]
        nodes[0]["features"] = [feature("supply", "out")]; nodes[1]["features"] = [feature("load", "in")]
        d["relationships"] = [edge(typ="service_link", source={"feature": "division/a#supply"}, target={"feature": "division/b#load"})]
        self.valid(d)
        for key, value in (("medium", "wrong_loop"), ("direction", "out"), ("system", "power")):
            bad = copy.deepcopy(d); bad["spaces"][0]["spaces"][1]["features"][0]["ports"][0][key] = value
            self.invalid(bad, "port-compatibility")
        d["relationships"][0]["parameters"]["toPort"] = "missing"
        self.invalid(d, "port-reference")

    def test_shared_border_and_same_grid_service_cannot_cross_engine_grids(self):
        d = program(); d["spaces"][0]["spaces"][1]["placement"]["grid"] = "remote"
        d["relationships"] = [edge(typ="shared_boundary")]
        self.invalid(d, "grid-conflict")

    def test_grid_relation_checks_explicit_grids(self):
        d = program(); d["relationships"] = [edge(typ="grid_relation")]
        self.valid(d)
        d["relationships"][0]["parameters"]["relation"] = "different"
        self.invalid(d, "grid-conflict")

    def test_same_parent_binding_retains_repeated_common_instance_scope(self):
        d = program(); suite = d["spaces"][0]["spaces"][0]
        suite["count"] = count(2, 2, 2); suite["spaces"] = [room("child"), room("peer")]
        d["spaces"][0]["requirements"] = []
        e = edge(source={"space": "division/a/child"}, target={"space": "division/a/peer"})
        e["matching"]["scope"] = "same_parent"; d["relationships"] = [e]
        self.valid(d)
        b = V.compile_document(d)["relationships"][0]["sourceBindings"][0]
        self.assertEqual("division/a", b["candidates"][0]["sharedAncestorInstance"])
        self.assertEqual(2, b["instanceVariables"][1]["count"]["max"])

    def test_one_matching_never_selects_an_arbitrary_candidate(self):
        d = program(); dep = d["spaces"][0]
        dep["spaces"].append(room("c")); dep["requirements"] = []
        d["relationships"][0]["to"] = {"selector": {"tagsAll": ["target"]}}
        for n in dep["spaces"][1:]: n["tags"] = ["target"]
        c = V.compile_document(d)
        self.assertEqual(2, len(c["relationships"][0]["sourceBindings"][0]["candidates"]))
        self.assertEqual("target_instance_selection", c["remainingDecisions"][0]["decision"])

    def test_distinct_matching_checks_source_target_count_capacity(self):
        d = program(); d["spaces"][0]["spaces"][0]["count"] = count(3, 3, 3)
        d["spaces"][0]["requirements"] = []
        d["relationships"][0]["matching"]["targets"] = "distinct"
        self.invalid(d, "distinct-capacity")
        d["spaces"][0]["spaces"][1]["count"] = count(3, 3, 3)
        self.valid(d)

    def test_required_dependency_forces_optional_target_and_respects_choice_max(self):
        d = program(); nodes = d["spaces"][0]["spaces"]
        nodes[1]["count"] = count(0, 1, 1)
        d["relationships"] = [edge(typ="dependency")]
        self.valid(d)
        self.assertEqual("required_when_parent_present", V.compile_document(d)["tree"][0]["children"][1]["presence"])
        d["choices"] = [{"id": "exclusive", "members": ["division/a", "division/b"], "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
        self.invalid(d, "selection-conflict")

    def test_absent_optional_source_has_no_forced_target_edge(self):
        d = program(); nodes = d["spaces"][0]["spaces"]
        nodes[0]["count"] = count(0, 0, 0); nodes[1]["count"] = count(0, 0, 0)
        d["spaces"][0]["requirements"] = []
        self.valid(d)

    def test_optional_suite_children_do_not_force_stationwide_support(self):
        d = program(); nodes = d["spaces"][0]["spaces"]
        nodes[0]["count"] = count(0, 0, 1)
        nodes[0]["spaces"] = [room("inside")]
        nodes[1]["count"] = count(0, 0, 1)
        d["spaces"][0]["requirements"] = []
        d["relationships"] = [edge(typ="dependency", source={"space": "division/a/inside"})]
        c = V.compile_document(d)
        self.assertEqual("choice", c["tree"][0]["children"][1]["presence"])

    def test_disabled_parent_does_not_create_phantom_mandatory_children(self):
        d = program(); dep = d["spaces"][0]; dep["requirements"] = []
        a = dep["spaces"][0]; a["count"] = count(0, 0, 0); a["spaces"] = [room("inside")]
        dep["spaces"][1]["count"] = count(0, 0, 0)
        d["relationships"] = [edge(typ="dependency", source={"space": "division/a/inside"})]
        self.valid(d)

    def test_conditional_distinct_matching_cannot_ignore_present_target_capacity(self):
        d = program(); d["spaces"][0]["requirements"] = []
        d["spaces"][0]["spaces"][0]["count"] = count(3, 3, 3)
        d["relationships"][0]["matching"].update(targets="distinct", presence="if_both_present")
        self.invalid(d, "distinct-capacity")

    def test_choice_excluded_target_is_not_an_available_compiler_candidate(self):
        d = program(); dep = d["spaces"][0]
        dep["spaces"][1]["count"] = count(0, 1, 1)
        d["choices"] = [{"id": "family", "members": ["division/a", "division/b"], "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
        d["relationships"][0]["matching"]["presence"] = "if_both_present"
        c = V.compile_document(d)
        self.assertFalse(c["relationships"][0]["sourceBindings"][0]["candidates"][0]["available"])

    def test_default_tier_priority_and_feature_clearance_are_preserved(self):
        d = program(); d["defaults"]["requirement"] = {"priority": "desired", "weight": 7, "tier": 2}
        d["spaces"][0]["spaces"][0]["features"] = [feature("adapter")]
        c = V.compile_document(d)
        self.assertEqual(("desired", 7, 2), tuple(c["relationships"][0][key] for key in ("priority", "weight", "tier")))
        self.assertEqual({"workingTiles": 0, "approachWidth": 0}, c["tree"][0]["children"][0]["features"][0]["clearance"])

    def test_choices_count_template_families_not_repeated_instances(self):
        d = program(); dep = d["spaces"][0]; dep["requirements"] = []
        dep["spaces"][0]["count"] = count(5, 5, 5)
        dep["spaces"][1]["count"] = count(0, 0, 1)
        d["relationships"] = []
        d["choices"] = [{"id": "families", "members": ["division/a", "division/b"], "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
        self.valid(d)
        d["spaces"][0]["spaces"][1]["count"] = count()
        self.invalid(d, "choice-infeasible")

    def test_choices_cannot_mix_different_direct_parents(self):
        d = program(); a = d["spaces"][0]["spaces"][0]; a["spaces"] = [room("inside")]
        d["choices"] = [{"id": "bad_scope", "members": ["division/a/inside", "division/b"], "minSelected": 1, "preferredSelected": 1, "maxSelected": 2}]
        self.invalid(d, "choice-scope")

    def test_mandatory_dependency_to_unavailable_target_is_not_vacuous(self):
        d = program(); d["spaces"][0]["spaces"][1]["count"] = count(0, 0, 0)
        self.invalid(d, "selection-conflict")
        d["relationships"][0]["matching"]["presence"] = "if_both_present"
        d["spaces"][0]["requirements"] = []
        self.valid(d)

    def test_extensions_validate_parameters_and_require_external_evaluator(self):
        d = program()
        d["extensions"] = [{"id": "x_custom_heat", "appliesTo": "node", "description": "Custom thermal requirement.",
                            "evaluator": "custom.thermal.v1", "parametersSchema": {"type": "object", "properties": {"watts": {"type": "number", "minimum": 1}}, "required": ["watts"], "additionalProperties": False}}]
        d["spaces"][0]["spaces"][0]["requirements"].append({"id": "custom", "type": "x_custom_heat", "parameters": {"watts": 10}})
        self.valid(d)
        c = V.compile_document(d)
        self.assertEqual("mandatory", c["externalEvaluatorsRequired"][0]["priority"])
        d["spaces"][0]["spaces"][0]["requirements"][-1]["parameters"]["secret"] = True
        self.invalid(d, "extension-parameters")

    def test_unknown_extension_scope_and_remote_schema_reference_fail_closed(self):
        d = program(); d["spaces"][0]["requirements"].append({"id": "custom", "type": "x_missing_type", "parameters": {}})
        self.invalid(d, "reference")
        d["extensions"] = [{"id": "x_missing_type", "appliesTo": "relationship", "description": "Custom.", "evaluator": "custom.v1", "parametersSchema": {"$ref": "https://invalid.example/schema"}}]
        self.invalid(d, "external-schema-reference")
        d["extensions"][0]["parametersSchema"] = {"type": "object", "additionalProperties": False}
        self.invalid(d, "extension-scope")

    def test_assembly_native_component_and_node_are_exact_alternatives(self):
        d = program()
        d["assemblies"] = [{"id": "machine", "description": "Local pose in native tiles, CCW degrees.", "sameGrid": True,
                            "parts": [{"id": "center", "prototypes": ["Center"], "x": 0, "y": 0, "rotation": 90}],
                            "ports": [{"id": "port", "part": "center", "nativeComponent": "ApcPowerReceiver", "system": "power", "medium": "lv", "direction": "in"}]}]
        f = feature("machine", "in", "power", "lv"); f["assembly"] = "machine"
        d["spaces"][0]["spaces"][0]["features"] = [f]
        self.valid(d)
        d["assemblies"][0]["ports"][0]["nativeNode"] = "invented"
        self.invalid(d, "schema")
        del d["assemblies"][0]["ports"][0]["nativeNode"]
        f["ports"][0]["medium"] = "hv"
        self.invalid(d, "assembly-port")

    def test_docking_needs_real_capability_full_clearance_and_evidence(self):
        d = program(); a = d["spaces"][0]["spaces"][0]
        dock = {"id": "berth", "type": "docking", "parameters": {"interface": "dock", "berthWidth": 9, "berthDepth": 5,
                "portOffset": 1, "clearance": 1, "approachWidth": 11, "approachLength": 16, "approachToExterior": True, "source": "authored"}}
        a["requirements"].append(dock)
        self.valid(d)
        dock["parameters"]["approachWidth"] = 9
        self.invalid(d, "dock-width")
        dock["parameters"]["approachWidth"] = 11; dock["parameters"]["interface"] = "counter"
        self.invalid(d, "capability")

    def test_fraction_and_shape_units_are_checked(self):
        for kind, low, high in (("shape.aspect_ratio", .5, 3), ("shape.fill_ratio", .5, 2),
                                ("maintenance.wrap_fraction", .5, 2), ("shape.components", 1.5, 3)):
            d = program(); d["spaces"][0]["spaces"][0]["requirements"].append(metric("shape", kind, low, low, high))
            self.invalid(d, "metric-range")

    def test_yaml_duplicate_alias_unsafe_tag_and_multiple_documents_fail(self):
        for text in ("id: first\nid: second\n", "x: &same []\ny: *same\n", "x: !!python/object:bad {}\n", "a: 1\n---\na: 2\n"):
            with tempfile.TemporaryDirectory() as temp:
                p = Path(temp) / "bad.yaml"; p.write_text(text)
                with self.assertRaises(V.ProgramError): V.load_program(p)

    def test_cli_compiles_symbolic_json_and_rejects_invalid_without_output(self):
        with tempfile.TemporaryDirectory() as temp:
            p, output = Path(temp) / "program.yaml", Path(temp) / "symbolic.json"
            p.write_text(yaml.safe_dump(program()), encoding="utf8")
            command = [sys.executable, "-B", str(ROOT / "validate.py"), str(p), "--compile", str(output), "--json"]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertFalse(json.loads(result.stdout)["geometryCertified"])
            self.assertEqual("symbolic_candidates_only", json.loads(output.read_text())["certification"]["instanceMatching"])
            output.unlink(); p.write_text("id: duplicate\nid: duplicate\n")
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(1, result.returncode)
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
