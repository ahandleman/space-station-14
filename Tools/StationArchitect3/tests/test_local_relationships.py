"""Local declarations keep relationships beside the requirements that need them."""
import copy
import unittest

from test_program import V, count, edge, feature, program, room


def local_program():
    document = program()
    document.pop("relationships")
    return document


def local_edge(identifier="link", typ="route", source=None, target=None, **overrides):
    return edge(identifier, typ, source or {"space": "."}, target or {"space": "../b"}, **overrides)


class LocalRelationshipTests(unittest.TestCase):
    def valid(self, doc):
        self.assertEqual([], V.validate_document(doc))
        return V.compile_document(doc)

    def invalid(self, doc, code):
        self.assertIn(code, {d["code"] for d in V.validate_document(doc)})

    def test_local_feature_link_is_canonical_and_does_not_mutate(self):
        d = local_program(); a = d["spaces"][0]["spaces"][0]
        a["features"] = [feature("console", direction="out"), feature("analyzer", direction="in")]
        a["relationships"] = [local_edge(typ="service_link", source={"feature": "#console"}, target={"feature": "#analyzer"})]
        before = copy.deepcopy(d); c = self.valid(d)
        r = c["relationships"][0]
        self.assertEqual("division/a::link", r["id"])
        self.assertEqual("link", r["authoredId"])
        self.assertEqual("division/a", r["declarationScope"])
        self.assertEqual("division/a#console", r["sourceBindings"][0]["source"])
        self.assertEqual("division/a#analyzer", r["sourceBindings"][0]["candidates"][0]["endpoint"])
        self.assertEqual([r["id"]], c["tree"][0]["children"][0]["declaredRelationships"])
        self.assertEqual(before, d)
        self.assertEqual(c, V.compile_document(d))

    def test_sibling_service_interface_is_declared_at_source(self):
        d = local_program(); a = d["spaces"][0]["spaces"][0]
        a["relationships"] = [local_edge("serves_queue", "shared_boundary")]
        c = self.valid(d)
        b = c["relationships"][0]["sourceBindings"][0]
        self.assertEqual("division/a", b["source"])
        self.assertEqual("division/b", b["candidates"][0]["endpoint"])

    def test_repeated_suite_child_edges_retain_instance_binding(self):
        d = local_program(); dep = d["spaces"][0]
        dep["requirements"][0]["parameters"]["max"] = 200
        suite = {"id": "suite", "name": "Suite", "kind": "group", "count": count(2, 2, 2),
                 "placement": {"nesting": "owned", "zone": "any", "grid": "main"},
                 "spaces": dep["spaces"], "requirements": []}
        dep["spaces"] = [suite]
        suite["relationships"] = [local_edge("counter", "shared_boundary", {"space": "./a"}, {"space": "./b"},
            matching={"targets": "one", "scope": "same_parent", "presence": "require_target"})]
        c = self.valid(d)
        candidate = c["relationships"][0]["sourceBindings"][0]["candidates"][0]
        self.assertEqual("division/suite", candidate["sharedAncestorInstance"])
        self.assertEqual(2, candidate["instanceVariables"][1]["count"]["min"])
        self.assertEqual("not_evaluated", c["certification"]["geometry"])

    def test_duplicate_local_ids_in_different_scopes_are_independent(self):
        d = local_program(); a, b = d["spaces"][0]["spaces"]
        a["relationships"] = [local_edge()]
        b["relationships"] = [local_edge(target={"space": "../a"})]
        c = self.valid(d)
        self.assertEqual({"division/a::link", "division/b::link"}, {r["id"] for r in c["relationships"]})
        self.assertEqual(2, len(c["activationImplications"]))

    def test_duplicate_ids_in_one_scope_are_rejected_with_authored_location(self):
        d = local_program(); a = d["spaces"][0]["spaces"][0]
        a["relationships"] = [local_edge(), local_edge()]
        ds = V.validate_document(d)
        duplicate = next(e for e in ds if e["code"] == "duplicate-id")
        self.assertEqual("$.spaces[0].spaces[0].relationships[1].id", duplicate["path"])

    def test_legacy_root_edges_and_local_edges_coexist_without_id_collision(self):
        d = program(); d["spaces"][0]["spaces"][0]["relationships"] = [local_edge()]
        c = self.valid(d)
        self.assertEqual(["link", "division/a::link"], [r["id"] for r in c["relationships"]])
        self.assertIsNone(c["relationships"][0]["declarationScope"])

    def test_above_root_and_document_root_are_not_spaces(self):
        for target in ("../../../missing", "../.."):
            with self.subTest(target=target):
                d = local_program(); d["spaces"][0]["spaces"][0]["relationships"] = [local_edge(target={"space": target})]
                self.invalid(d, "reference-scope")

    def test_relative_typo_is_not_an_empty_optional_success(self):
        d = local_program()
        d["spaces"][0]["spaces"][0]["relationships"] = [local_edge(target={"space": "../missing"},
            matching={"targets": "one", "scope": "same_owner", "presence": "if_both_present"})]
        self.invalid(d, "reference")

    def test_cross_department_dependency_is_declared_at_source(self):
        d = local_program(); d["spaces"].append(room("support"))
        d["spaces"][0]["spaces"][0]["relationships"] = [local_edge("needs_support", "dependency", target={"space": "/support"},
            matching={"targets": "one", "scope": "station", "presence": "require_target"})]
        c = self.valid(d)
        self.assertEqual("support", c["relationships"][0]["sourceBindings"][0]["candidates"][0]["endpoint"])

    def test_local_source_cannot_silently_originate_outside_its_scope(self):
        d = local_program()
        d["spaces"][0]["spaces"][0]["relationships"] = [local_edge(source={"space": "../b"}, target={"space": "."})]
        self.invalid(d, "relationship-scope")

    def test_selector_and_route_waypoints_resolve_from_declaration_scope(self):
        d = local_program(); dep = d["spaces"][0]
        dep["relationships"] = [local_edge(source={"selector": {"within": "./a"}}, target={"space": "./b"})]
        dep["relationships"][0]["parameters"].update({"through": ["./a"], "avoid": []})
        c = self.valid(d)
        self.assertEqual(["division/a"], c["relationships"][0]["parameters"]["through"])
        self.assertEqual("division/a", c["relationships"][0]["sourceBindings"][0]["source"])

    def test_alias_paths_cannot_hide_a_waypoint_avoidance_conflict(self):
        d = local_program(); a = d["spaces"][0]["spaces"][0]
        a["relationships"] = [local_edge()]
        a["relationships"][0]["parameters"].update({"through": ["../b"], "avoid": ["/division/b"]})
        self.invalid(d, "route-conflict")

    def test_parent_and_child_requirements_survive_resolution(self):
        d = local_program(); dep = d["spaces"][0]; a = dep["spaces"][0]
        a["requirements"].append({"id": "approach", "type": "access_policy", "parameters": {
            "entryFrom": ["../b"], "mustTraverse": ["../b"], "allowPublicEntry": True,
            "allowMaintenanceEntry": False, "allowExteriorEntry": False, "throughTraffic": False}})
        a["relationships"] = [local_edge()]
        before = copy.deepcopy(d); resolved = V.resolve_document(d); c = self.valid(d)
        self.assertEqual(before, d)
        self.assertEqual("area.aggregate", c["tree"][0]["requirements"][0]["parameters"]["metric"])
        self.assertEqual("area.exclusive", c["tree"][0]["children"][0]["requirements"][0]["parameters"]["metric"])
        self.assertEqual(["division/b"], resolved["spaces"][0]["spaces"][0]["requirements"][1]["parameters"]["entryFrom"])
        self.assertNotIn("relationships", resolved["spaces"][0]["spaces"][0])

    def test_local_extensions_remain_required_in_compilation(self):
        d = local_program()
        d["extensions"] = [{"id": "x_binding", "appliesTo": "relationship", "description": "A real machine association.",
            "evaluator": "example.binding/v1", "parametersSchema": {"type": "object", "properties": {}, "additionalProperties": False}}]
        d["spaces"][0]["spaces"][0]["relationships"] = [local_edge(typ="x_binding")]
        c = self.valid(d)
        self.assertEqual("division/a::link", c["externalEvaluatorsRequired"][0]["relationship"])
        self.assertEqual("division/a", c["externalEvaluatorsRequired"][0]["declarationScope"])

    def test_local_choices_resolve_members_and_preserve_provenance(self):
        d = local_program(); dep = d["spaces"][0]
        dep["requirements"][0]["parameters"]["min"] = 8
        for child in dep["spaces"]:
            child["count"] = count(0, 1, 1)
        dep["choices"] = [{"id": "engine", "members": ["./a", "./b"],
                           "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
        d.pop("choices")
        before = copy.deepcopy(d); c = self.valid(d)
        self.assertEqual(before, d)
        self.assertEqual(["division::engine"], c["tree"][0]["declaredChoices"])
        choice = c["choices"][0]
        self.assertEqual("division::engine", choice["id"])
        self.assertEqual("engine", choice["authoredId"])
        self.assertEqual("division", choice["declarationScope"])
        self.assertEqual(["division/a", "division/b"], choice["members"])
        self.assertNotIn("_declarationLocation", choice)
        self.assertEqual(choice["members"], V.resolve_document(d)["choices"][0]["members"])

    def test_local_choice_ids_are_unique_only_within_their_scope(self):
        d = local_program(); dep = d["spaces"][0]
        dep["requirements"][0]["parameters"].update({"min": 16, "preferred": 32, "max": 80})
        for child in dep["spaces"]:
            child["spaces"] = [room("inside")]
            child["choices"] = [{"id": "facility", "members": ["./inside"],
                                 "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
        c = self.valid(d)
        self.assertEqual({"division/a::facility", "division/b::facility"}, {x["id"] for x in c["choices"]})
        dep["spaces"][0]["choices"].append(copy.deepcopy(dep["spaces"][0]["choices"][0]))
        self.invalid(d, "duplicate-id")

    def test_local_choice_cannot_select_outside_its_declaring_subtree(self):
        d = local_program(); a = d["spaces"][0]["spaces"][0]
        a["choices"] = [{"id": "elsewhere", "members": ["../b"],
                         "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
        self.invalid(d, "choice-scope")

    def test_local_choice_relative_aliases_cannot_double_count_one_family(self):
        d = local_program(); dep = d["spaces"][0]
        dep["choices"] = [{"id": "duplicate", "members": ["./a", "/division/a"],
                           "minSelected": 1, "preferredSelected": 1, "maxSelected": 2}]
        self.invalid(d, "choice-members")

    def test_local_choice_missing_or_above_root_members_fail(self):
        for member, code in (("./missing", "reference"), ("../../missing", "reference-scope")):
            with self.subTest(member=member):
                d = local_program(); dep = d["spaces"][0]
                dep["choices"] = [{"id": "bad", "members": [member],
                                   "minSelected": 1, "preferredSelected": 1, "maxSelected": 1}]
                self.invalid(d, code)

    def reverse_supply(self):
        d = local_program(); consumer, provider = d["spaces"][0]["spaces"]
        consumer["features"] = [feature("converter", direction="in", system="power", medium="HVPower")]
        provider["features"] = [feature("bus", direction="out", system="power", medium="HVPower")]
        relationship = local_edge("supply", "service_link", source={"feature": "#converter"}, target={"feature": "../b#bus"})
        relationship["parameters"].update({"flow": "reverse", "system": "power", "medium": "HVPower"})
        consumer["relationships"] = [relationship]
        return d

    def test_reverse_flow_preserves_consumer_obligation_and_authored_input(self):
        d = self.reverse_supply(); before = copy.deepcopy(d)
        c = self.valid(d); normalized = V.flatten_relationships(d)[0]
        self.assertEqual(before, d)
        self.assertEqual("reverse", normalized["parameters"]["flow"])
        self.assertEqual("reverse", c["relationships"][0]["parameters"]["flow"])
        self.assertEqual("division/a#converter", c["relationships"][0]["sourceBindings"][0]["source"])
        self.assertEqual("division/a#converter", c["activationImplications"][0]["ifSelected"])
        self.assertEqual(["division/b#bus"], c["activationImplications"][0]["requiresAtLeastOneOf"])

    def test_reverse_flow_requires_input_at_source_and_output_at_target(self):
        for which, bad_direction in ((0, "out"), (1, "in")):
            with self.subTest(which=which):
                d = self.reverse_supply()
                d["spaces"][0]["spaces"][which]["features"][0]["ports"][0]["direction"] = bad_direction
                self.invalid(d, "port-compatibility")
        d = self.reverse_supply()
        for node in d["spaces"][0]["spaces"]:
            node["features"][0]["ports"][0]["direction"] = "bidirectional"
        self.valid(d)

    def test_reverse_flow_required_consumer_activates_optional_provider(self):
        d = self.reverse_supply(); d["spaces"][0]["spaces"][1]["count"] = count(0, 1, 1)
        c = self.valid(d)
        self.assertEqual("required_when_parent_present", c["tree"][0]["children"][1]["presence"])

    def test_reverse_flow_optional_consumer_does_not_force_itself_or_provider(self):
        d = self.reverse_supply(); dep = d["spaces"][0]
        dep["requirements"][0]["parameters"].update({"min": 0, "preferred": 16})
        for node in dep["spaces"]:
            node["count"] = count(0, 1, 1)
        c = self.valid(d)
        self.assertEqual(["choice", "choice"], [n["presence"] for n in c["tree"][0]["children"]])
        dep["spaces"][0]["count"] = count(0, 0, 0)
        c = self.valid(d)
        self.assertEqual("choice", c["tree"][0]["children"][1]["presence"])
        self.assertFalse(c["relationships"][0]["sourceBindings"][0]["available"])

    def test_omitted_flow_keeps_forward_semantics_and_unknown_flow_fails(self):
        d = local_program(); a = d["spaces"][0]["spaces"][0]
        a["features"] = [feature("source", direction="out"), feature("sink", direction="in")]
        a["relationships"] = [local_edge("service", "service_link", {"feature": "#source"}, {"feature": "#sink"})]
        c = self.valid(d)
        self.assertEqual("forward", c["relationships"][0]["parameters"]["flow"])
        self.assertNotIn("flow", a["relationships"][0]["parameters"])
        a["relationships"][0]["parameters"]["flow"] = "sideways"
        self.invalid(d, "schema")


if __name__ == "__main__":
    unittest.main()
