#!/usr/bin/env python3
"""Strict, independent V3 authoring validation and symbolic compilation.

No layout, game simulation, prototype loader, or earlier-tool dependency lives here.
Public API: load_program, resolve_document, flatten_relationships,
validate_document, compile_document.
"""
from __future__ import annotations

import argparse
import copy
import json
import math
from pathlib import Path
from typing import Any

import jsonschema
import yaml
from yaml.tokens import AliasToken, AnchorToken


SCHEMA_PATH = Path(__file__).parent / "schema" / "program.schema.json"
PHYSICAL = {"room", "passage"}


def diagnostic(path: str, code: str, message: str, severity: str = "error") -> dict:
    return {"path": path, "code": code, "message": message, "severity": severity}


class ProgramError(ValueError):
    def __init__(self, diagnostics: list[dict]):
        self.diagnostics = diagnostics
        super().__init__("; ".join(d["message"] for d in diagnostics))


class StrictLoader(yaml.SafeLoader):
    """SafeLoader with duplicate keys rejected before they can be overwritten."""

    def construct_mapping(self, node, deep=False):
        result = {}
        for key_node, value_node in node.value:
            key = self.construct_object(key_node, deep=deep)
            if not isinstance(key, str):
                raise yaml.constructor.ConstructorError(
                    None, None, "mapping keys must be strings", key_node.start_mark)
            if key in result:
                raise yaml.constructor.ConstructorError(
                    None, None, f"duplicate YAML key {key!r}", key_node.start_mark)
            result[key] = self.construct_object(value_node, deep=deep)
        return result


def load_program(path: str | Path) -> dict:
    """Load one YAML document; duplicate keys, aliases, anchors and unsafe tags fail."""
    try:
        text = Path(path).read_text(encoding="utf-8-sig")
        for token in yaml.scan(text):
            if isinstance(token, (AliasToken, AnchorToken)):
                raise ProgramError([diagnostic(
                    "$", "yaml-alias", "YAML anchors and aliases are not supported; write explicit values.")])
        value = yaml.load(text, Loader=StrictLoader)
    except ProgramError:
        raise
    except (OSError, UnicodeError, yaml.YAMLError) as exc:
        raise ProgramError([diagnostic("$", "input", str(exc))]) from exc
    if not isinstance(value, dict):
        raise ProgramError([diagnostic("$", "document", "The document root must be a mapping.")])
    return value


def _path(parts) -> str:
    return "$" + "".join(f"[{p}]" if isinstance(p, int) else "." + str(p) for p in parts)


def _non_json(value: Any, path="$", active=None) -> list[dict]:
    active = set() if active is None else active
    if isinstance(value, (dict, list)):
        if id(value) in active:
            return [diagnostic(path, "cyclic-value", "Cyclic objects are not authoring values.")]
        active.add(id(value))
        out = []
        values = value.items() if isinstance(value, dict) else enumerate(value)
        for key, child in values:
            if isinstance(value, dict) and not isinstance(key, str):
                out.append(diagnostic(path, "non-json", "Mapping keys must be strings."))
            out.extend(_non_json(child, path + f"[{key}]", active))
        active.remove(id(value))
        return out
    if isinstance(value, float) and not math.isfinite(value):
        return [diagnostic(path, "non-finite", "Numbers must be finite.")]
    if value is not None and not isinstance(value, (str, bool, int, float)):
        return [diagnostic(path, "non-json", "Only JSON-compatible YAML values are supported.")]
    return []


def _bounds_errors(value: dict, path: str, keys=("min", "preferred", "max")) -> list[dict]:
    found = [(key, value[key]) for key in keys if key in value]
    out = []
    for (ka, a), (kb, b) in zip(found, found[1:]):
        if a > b:
            out.append(diagnostic(path, "bounds", f"{ka} ({a}) must not exceed {kb} ({b})."))
    return out


def _resolve_reference(value: str, scope: str | None, location: str) -> str:
    """Resolve explicit relative paths; bare paths retain legacy root semantics."""
    space, marker, feature = value.partition("#")
    relative = space in (".", "..", "") or space.startswith(("./", "../"))
    parts = scope.split("/") if relative and scope else []
    for part in space.lstrip("/").split("/"):
        if part in ("", "."):
            continue
        if part == "..":
            if not parts:
                raise ProgramError([diagnostic(location, "reference-scope", "A relative path cannot traverse above the document root.")])
            parts.pop()
        else:
            parts.append(part)
    if not parts:
        raise ProgramError([diagnostic(location, "reference-scope", "The document root is not a space or feature owner.")])
    return "/".join(parts) + ("#" + feature if marker else "")


def resolve_document(document: dict) -> dict:
    """Return a canonical, flattened copy of a structurally valid authored program.

    Local relationships resolve against their declaration, not their source endpoint.
    Internal scoped IDs and provenance are added only to this copy. Opaque extension
    parameters are untouched; their evaluator owns any parameter-specific references.
    """
    result = copy.deepcopy(document)
    relationships, choices = [], []

    def collect_choices(values, scope, location):
        for i, choice in enumerate(values):
            loc = f"{location}[{i}]"
            local_id = choice["id"]
            choice["id"] = f"{scope}::{local_id}" if scope else local_id
            choice["authoredId"] = local_id
            choice["declarationScope"] = scope
            choice["_declarationLocation"] = loc
            choice["members"] = [_resolve_reference(p, scope, f"{loc}.members[{j}]")
                                 for j, p in enumerate(choice["members"])]
            choices.append(choice)

    def collect(edges, scope, location):
        for i, edge in enumerate(edges):
            loc = f"{location}[{i}]"
            local_id = edge["id"]
            edge["id"] = f"{scope}::{local_id}" if scope else local_id
            edge["authoredId"] = local_id
            edge["declarationScope"] = scope
            edge["_declarationLocation"] = loc
            for side in ("from", "to"):
                endpoint = edge[side]
                for key in ("space", "feature"):
                    if key in endpoint:
                        endpoint[key] = _resolve_reference(endpoint[key], scope, f"{loc}.{side}.{key}")
                if "selector" in endpoint and "within" in endpoint["selector"]:
                    endpoint["selector"]["within"] = _resolve_reference(
                        endpoint["selector"]["within"], scope, f"{loc}.{side}.selector.within")
            if edge["type"] == "route":
                for key in ("through", "avoid"):
                    edge["parameters"][key] = [_resolve_reference(p, scope, f"{loc}.parameters.{key}[{j}]")
                                                for j, p in enumerate(edge["parameters"][key])]
            if edge["type"] == "service_link":
                edge["parameters"].setdefault("flow", "forward")
            relationships.append(edge)

    def spaces(nodes, parent, location):
        for i, node in enumerate(nodes):
            path = f"{parent}/{node['id']}" if parent else node["id"]
            loc = f"{location}[{i}]"
            organization = node.get("organization", {})
            if "independent" in organization:
                organization["independent"] = [_resolve_reference(p, path, loc + ".organization.independent")
                                               for p in organization["independent"]]
            for suite in organization.get("suites", []):
                suite["members"] = [_resolve_reference(p, path, loc + ".organization.suites.members")
                                    for p in suite["members"]]
            for j, rule in enumerate(node.get("requirements", [])):
                if rule["type"] == "access_policy":
                    for key in ("entryFrom", "mustTraverse"):
                        rule["parameters"][key] = [_resolve_reference(p, path, f"{loc}.requirements[{j}].parameters.{key}[{k}]")
                                                   for k, p in enumerate(rule["parameters"][key])]
            collect(node.pop("relationships", []), path, loc + ".relationships")
            collect_choices(node.pop("choices", []), path, loc + ".choices")
            spaces(node.get("spaces", []), path, loc + ".spaces")

    collect(result.pop("relationships", []), None, "$.relationships")
    collect_choices(result.pop("choices", []), None, "$.choices")
    spaces(result.get("spaces", []), None, "$.spaces")
    result["relationships"] = relationships
    result["choices"] = choices
    return result


def flatten_relationships(document: dict) -> list[dict]:
    """Get canonical edges with declaration provenance, without changing the input."""
    return resolve_document(document)["relationships"]


class Index:
    def __init__(self, doc: dict):
        self.doc = doc
        self.errors: list[dict] = []
        self.tables = {}
        for key in ("accessProfiles", "environments", "grids", "domains", "interfaces",
                    "assemblies", "choices", "relationships", "extensions", "evidence"):
            table = {}
            for i, item in enumerate(doc.get(key, [])):
                if item["id"] in table:
                    location = item.get("_declarationLocation", f"$.{key}[{i}]")
                    self.error(location + ".id", "duplicate-id", f"Duplicate {key} ID {item['id']!r}.")
                else:
                    table[item["id"]] = item
            self.tables[key] = table
        self.nodes: dict[str, dict] = {}
        self.features: dict[str, dict] = {}
        self.locations: dict[str, str] = {}
        self.parents: dict[str, str | None] = {}
        self.owners: dict[str, str | None] = {}
        self.children: dict[str | None, list[str]] = {None: []}
        self._spaces(doc.get("spaces", []), None, None, "$.spaces")

    def error(self, path, code, message):
        self.errors.append(diagnostic(path, code, message))

    def _spaces(self, nodes, parent, owner, location):
        ids = set()
        for i, node in enumerate(nodes):
            p = (parent + "/" if parent else "") + node["id"]
            loc = f"{location}[{i}]"
            if node["id"] in ids:
                self.error(loc + ".id", "duplicate-id", f"Duplicate sibling space ID {node['id']!r}.")
                continue
            ids.add(node["id"])
            self.nodes[p], self.locations[p], self.parents[p] = node, loc, parent
            # Do not let one sibling's department become the following sibling's owner.
            actual_owner = p if node["kind"] == "department" else (
                self.owners.get(parent) if parent else None)
            self.owners[p] = actual_owner
            self.children.setdefault(parent, []).append(p)
            self.children[p] = []
            self._unique(node.get("requirements", []), loc + ".requirements")
            self._unique(node.get("features", []), loc + ".features")
            for j, feature in enumerate(node.get("features", [])):
                f = p + "#" + feature["id"]
                if f not in self.features:
                    self.features[f] = feature
                    self.locations[f] = f"{loc}.features[{j}]"
                    self.parents[f] = p
                    self.owners[f] = actual_owner
            self._spaces(node.get("spaces", []), p, actual_owner, loc + ".spaces")

    def _unique(self, values, loc):
        seen = set()
        for i, item in enumerate(values):
            if item["id"] in seen:
                self.error(f"{loc}[{i}].id", "duplicate-id", f"Duplicate local ID {item['id']!r}.")
            seen.add(item["id"])

    def require(self, collection, identifier, path):
        if identifier not in self.tables[collection]:
            self.error(path, "reference", f"Unknown {collection} ID {identifier!r}.")
            return None
        return self.tables[collection][identifier]

    def require_space(self, p, path):
        if p not in self.nodes:
            self.error(path, "reference", f"Unknown exact space path {p!r}.")
            return None
        return self.nodes[p]

    def endpoint(self, endpoint, path) -> list[str]:
        if "space" in endpoint:
            p = endpoint["space"]
            return [p] if self.require_space(p, path + ".space") else []
        if "feature" in endpoint:
            p = endpoint["feature"]
            if p not in self.features:
                self.error(path + ".feature", "reference", f"Unknown exact feature path {p!r}.")
                return []
            return [p]
        if "domain" in endpoint:
            p = endpoint["domain"]
            return ["@domain/" + p] if self.require("domains", p, path + ".domain") else []
        selector = endpoint["selector"]
        within = selector.get("within")
        if within and not self.require_space(within, path + ".selector.within"):
            return []
        return [p for p, n in self.nodes.items()
                if (not within or p == within or p.startswith(within + "/"))
                and (not selector.get("kinds") or n["kind"] in selector["kinds"])
                and set(selector.get("tagsAll", [])).issubset(n.get("tags", []))
                and (not selector.get("tagsAny") or set(selector["tagsAny"]) & set(n.get("tags", [])))]

    def ancestors(self, p):
        if p.startswith("@domain/"):
            return []
        space = p.split("#")[0]
        parts = space.split("/")
        return ["/".join(parts[:i]) for i in range(1, len(parts) + 1)]

    def common(self, a, b):
        aa, bb = self.ancestors(a), self.ancestors(b)
        common = [x for x, y in zip(aa, bb) if x == y]
        # A room endpoint's own instance is the containing scope of its features,
        # but a source room does not serve as its own parent for an unrelated peer.
        return common[-1] if common else None

    def compatible_scope(self, a, b, scope):
        if scope == "station":
            return True
        if a.startswith("@domain/") or b.startswith("@domain/"):
            return False
        if scope == "same_owner":
            return self.owners.get(a) is not None and self.owners.get(a) == self.owners.get(b)
        return self.common(a, b) is not None

    def count(self, p):
        if p.startswith("@domain/"):
            return {"min": 1, "preferred": 1, "max": 1}
        return (self.features.get(p) or self.nodes[p])["count"]

    def available(self, p):
        return all(self.count(a)["max"] > 0 for a in self.ancestors(p)) and self.count(p)["max"] > 0

    def rule(self, value):
        default = self.doc.get("defaults", {}).get("requirement", {})
        return {"priority": value.get("priority", default.get("priority", "mandatory")),
                "weight": value.get("weight", default.get("weight", 100)),
                "tier": value.get("tier", default.get("tier", 0))}

    def grid(self, p):
        if p.startswith("@domain/"):
            return None
        return self.nodes[p.split("#")[0]]["placement"]["grid"]

    def physical(self, p):
        return "#" not in p and p in self.nodes and self.nodes[p]["kind"] in PHYSICAL


def _evidence(index: Index, item, loc):
    for i, e in enumerate(item.get("evidence", [])):
        index.require("evidence", e, f"{loc}.evidence[{i}]")


def _extension(index: Index, rule, loc, applies):
    ext = index.require("extensions", rule["type"], loc + ".type")
    if ext is None:
        return
    if ext["appliesTo"] != applies:
        index.error(loc + ".type", "extension-scope", f"Extension applies to {ext['appliesTo']}, not {applies}.")
    try:
        for error in jsonschema.Draft202012Validator(ext["parametersSchema"]).iter_errors(rule["parameters"]):
            index.error(loc + ".parameters" + _path(error.absolute_path)[1:], "extension-parameters", error.message)
    except (jsonschema.exceptions.SchemaError, jsonschema.exceptions.RefResolutionError, RecursionError) as exc:
        index.error(loc + ".parameters", "extension-schema", str(exc))


def _check_extension_schemas(index: Index):
    for i, ext in enumerate(index.doc.get("extensions", [])):
        loc = f"$.extensions[{i}].parametersSchema"
        try:
            jsonschema.Draft202012Validator.check_schema(ext["parametersSchema"])
        except jsonschema.exceptions.SchemaError as exc:
            index.error(loc, "extension-schema", exc.message)
            continue
        def visit(value):
            if isinstance(value, dict):
                for key, item in value.items():
                    if key in ("$ref", "$dynamicRef", "$recursiveRef") and (
                            not isinstance(item, str) or not item.startswith("#")):
                        index.error(loc, "external-schema-reference", "Extension schemas may use local references only.")
                    visit(item)
            elif isinstance(value, list):
                for child in value:
                    visit(child)
        visit(ext["parametersSchema"])


def _space_semantics(index: Index):
    for p, n in index.nodes.items():
        loc, parent = index.locations[p], index.parents[p]
        index.errors.extend(_bounds_errors(n["count"], loc + ".count"))
        grid = index.require("grids", n["placement"]["grid"], loc + ".placement.grid")
        surface = n["placement"].get("surface", "any")
        if n["kind"] not in PHYSICAL and surface != "any":
            index.error(loc + ".placement.surface", "surface-subject", "An aggregate owns no physical surface; specify surfaces on its rooms/passages.")
        if grid and surface != "any" and grid["support"] != "mixed" and surface != {"solid": "floor", "lattice": "lattice"}[grid["support"]]:
            index.error(loc + ".placement.surface", "surface-grid", "Requested room surface conflicts with the grid's declared support.")
        if n["placement"]["nesting"] == "contained":
            if parent is None or index.nodes[parent]["kind"] != "room":
                index.error(loc + ".placement.nesting", "containment", "A contained space requires a physical room parent.")
            elif n["placement"]["grid"] != index.nodes[parent]["placement"]["grid"]:
                index.error(loc + ".placement.grid", "containment-grid", "Contained children must share their parent's engine grid.")
        if n["kind"] in PHYSICAL:
            index.require("environments", n["environment"], loc + ".environment")
            index.require("accessProfiles", n["access"], loc + ".access")
        elif n.get("features"):
            index.error(loc + ".features", "feature-subject", "Features require physical room or passage floor.")
        _evidence(index, n, loc)
        for j, rule in enumerate(n.get("requirements", [])):
            rloc = f"{loc}.requirements[{j}]"
            _evidence(index, rule, rloc)
            t, a = rule["type"], rule["parameters"]
            if t.startswith("x_"):
                _extension(index, rule, rloc, "node")
                continue
            if t == "metric":
                index.errors.extend(_bounds_errors(a, rloc + ".parameters"))
                metric = a["metric"]
                if metric == "area.exclusive" and n["kind"] not in PHYSICAL:
                    index.error(rloc, "metric-subject", f"{metric} requires a physical room or passage.")
                if metric == "core.components" and n["kind"] not in ("department", "group"):
                    index.error(rloc, "metric-subject", "core.components requires a logical owner or group.")
                if metric == "corridor.clear_width" and n["kind"] != "passage":
                    index.error(rloc, "metric-subject", "corridor.clear_width requires a passage.")
                for key in ("min", "preferred", "max"):
                    if key not in a:
                        continue
                    value = a[key]
                    if metric in ("shape.fill_ratio", "maintenance.wrap_fraction") and not 0 <= value <= 1:
                        index.error(rloc + ".parameters." + key, "metric-range", "A fraction must be between zero and one.")
                    if metric == "shape.aspect_ratio" and value < 1:
                        index.error(rloc + ".parameters." + key, "metric-range", "Aspect ratios must be at least one.")
                    minimum = 0 if metric == "area.aggregate" else 1
                    if metric in ("area.exclusive", "area.aggregate", "shape.components", "core.components", "shape.short_side", "corridor.clear_width") and (int(value) != value or value < minimum):
                        index.error(rloc + ".parameters." + key, "metric-range", "Floor, component and clear-width bounds must be positive integers.")
            elif t in ("perimeter", "docking", "environment", "access_policy"):
                if n["kind"] not in PHYSICAL:
                    index.error(rloc, "requirement-subject", f"{t} requires a physical room or passage.")
                if t == "environment":
                    profile = index.require("environments", a["profile"], rloc + ".parameters.profile")
                    existing = index.tables["environments"].get(n.get("environment"))
                    if (profile and existing and index.rule(rule)["priority"] == "mandatory"
                            and any(profile[k] != existing[k] for k in ("pressure", "enclosure", "temperature"))):
                        index.error(rloc, "environment-conflict", "Mandatory environment rule contradicts the space's profile.")
                elif t == "access_policy":
                    for key in ("entryFrom", "mustTraverse"):
                        for k, target in enumerate(a[key]):
                            target_node = index.require_space(target, f"{rloc}.parameters.{key}[{k}]")
                            if target_node and target_node["kind"] not in PHYSICAL:
                                index.error(rloc, "access-subject", "Access policy paths must identify physical spaces.")
                else:
                    interface = index.require("interfaces", a["interface"], rloc + ".parameters.interface")
                    if t == "perimeter":
                        index.require("domains", a["domain"], rloc + ".parameters.domain")
                        index.errors.extend(_bounds_errors(a, rloc + ".parameters", ("minLength", "preferredLength")))
                    else:
                        index.require("evidence", a["source"], rloc + ".parameters.source")
                        if interface and "docking" not in interface["capabilities"]:
                            index.error(rloc, "capability", "A docking requirement needs an interface with docking capability.")
                        if a["approachWidth"] < a["berthWidth"] + 2 * a["clearance"]:
                            index.error(rloc, "dock-width", "Approach width must clear the berth plus both lateral clearances.")
                        low = -(a["berthWidth"] // 2) + a["portOffset"]
                        if not low <= 0 < low + a["berthWidth"]:
                            index.error(rloc, "dock-port", "The docking port must fall inside the unpadded berth tangent span.")
        for f in n.get("features", []):
            fp, floc = p + "#" + f["id"], index.locations[p + "#" + f["id"]]
            index.errors.extend(_bounds_errors(f["count"], floc + ".count"))
            index._unique(f["ports"], floc + ".ports")
            _evidence(index, f, floc)
            if not f["prototypes"] and not f.get("assembly") and not f["ports"]:
                index.error(floc, "empty-feature", "An abstract feature needs declared service ports; otherwise supply prototypes or an assembly.")
            if f.get("assembly"):
                assembly = index.require("assemblies", f["assembly"], floc + ".assembly")
                if assembly:
                    exports = {x["id"]: x for x in assembly["ports"]}
                    for port in f["ports"]:
                        target = exports.get(port["id"])
                        if target is None or any(port[k] != target[k] for k in ("system", "medium", "direction")):
                            index.error(floc + ".ports", "assembly-port", "Feature ports must match exported assembly port IDs, systems, media and directions.")


def _assembly_semantics(index: Index):
    for i, a in enumerate(index.doc.get("assemblies", [])):
        loc = f"$.assemblies[{i}]"
        index._unique(a["parts"], loc + ".parts")
        index._unique(a["ports"], loc + ".ports")
        parts = {p["id"] for p in a["parts"]}
        for j, port in enumerate(a["ports"]):
            if port["part"] not in parts:
                index.error(f"{loc}.ports[{j}].part", "reference", f"Unknown assembly part {port['part']!r}.")
        _evidence(index, a, loc)


def _relationships(index: Index) -> list[dict]:
    output = []
    for i, edge in enumerate(index.doc.get("relationships", [])):
        loc, typ, a = edge.get("_declarationLocation", f"$.relationships[{i}]"), edge["type"], edge["parameters"]
        sources = index.endpoint(edge["from"], loc + ".from")
        targets = index.endpoint(edge["to"], loc + ".to")
        declaration = edge.get("declarationScope")
        if declaration and any(p.split("#")[0] != declaration and not p.startswith(declaration + "/") for p in sources):
            index.error(loc + ".from", "relationship-scope", "A locally declared relationship must originate at its declaring space or descendants.")
        _evidence(index, edge, loc)
        if typ.startswith("x_"):
            _extension(index, edge, loc, "relationship")
        if typ == "shared_boundary":
            interface = index.require("interfaces", a["interface"], loc + ".parameters.interface")
            index.errors.extend(_bounds_errors(a, loc + ".parameters", ("minLength", "preferredLength", "maxLength")))
            index.errors.extend(_bounds_errors(a, loc + ".parameters", ("minSegments", "maxSegments")))
            if a["continuous"] and (a.get("minSegments", 1) != 1 or a.get("maxSegments", 1) != 1):
                index.error(loc, "boundary-segments", "A continuous shared boundary must have exactly one segment.")
            if not all(index.physical(p) or p.startswith("@domain/") for p in sources + targets):
                index.error(loc, "endpoint-kind", "Shared boundaries require physical spaces or a boundary domain.")
            if sources and targets and all(p.startswith("@domain/") for p in sources + targets):
                index.error(loc, "endpoint-kind", "A domain is not physical room floor.")
        elif typ == "route":
            profile = index.require("accessProfiles", a["accessProfile"], loc + ".parameters.accessProfile")
            if not all(index.physical(p) for p in sources + targets):
                index.error(loc, "endpoint-kind", "Routes require physical room/passage endpoints, never aggregate or domain floor.")
            for key in ("through", "avoid"):
                for j, p in enumerate(a[key]):
                    node = index.require_space(p, f"{loc}.parameters.{key}[{j}]")
                    if node and node["kind"] not in PHYSICAL:
                        index.error(loc, "endpoint-kind", "Route through/avoid paths must identify physical spaces.")
            if set(a["through"]) & set(a["avoid"]):
                index.error(loc, "route-conflict", "A required waypoint cannot also be avoided.")
            vacuum = any(index.tables["environments"].get(index.nodes[p]["environment"], {}).get("pressure") == "vacuum"
                         for p in sources + targets + a["through"] if index.physical(p))
            if profile and (vacuum or a["mode"] == "eva") and not profile["canTraverseVacuum"]:
                index.error(loc, "vacuum-access", "Vacuum or EVA routes require a vacuum-capable access profile.")
        elif typ == "separation":
            index.errors.extend(_bounds_errors(a, loc + ".parameters", ("minDistance", "preferredDistance", "maxDistance")))
            if a.get("accessProfile"):
                index.require("accessProfiles", a["accessProfile"], loc + ".parameters.accessProfile")
            if not all(index.physical(p) for p in sources + targets):
                index.error(loc, "endpoint-kind", "Separation requires physical room or passage endpoints.")
        elif typ == "service_link":
            # 'from' owns the obligation; it need not be the physical supplier.
            # Reversing physical flow never reverses instance activation.
            source_direction, target_direction = ("in", "out") if a["flow"] == "reverse" else ("out", "in")
            for side, values, port_id, directions in (("from", sources, a["fromPort"], {source_direction, "bidirectional"}),
                                                      ("to", targets, a["toPort"], {target_direction, "bidirectional"})):
                for p in values:
                    if p not in index.features:
                        index.error(loc + "." + side, "endpoint-kind", "Service links require feature endpoints with declared ports.")
                        continue
                    port = next((x for x in index.features[p]["ports"] if x["id"] == port_id), None)
                    if port is None:
                        index.error(loc + ".parameters", "port-reference", f"Feature {p!r} has no port {port_id!r}.")
                    elif port["system"] != a["system"] or port["medium"] != a["medium"] or port["direction"] not in directions:
                        index.error(loc + ".parameters", "port-compatibility", f"Port {p}#{port_id} has an incompatible system, medium or direction.")
        elif typ == "grid_relation":
            if not all(index.physical(p) or p in index.features for p in sources + targets):
                index.error(loc, "endpoint-kind", "Grid relations require physical spaces or features.")
            if a.get("bridgeAssembly"):
                index.require("assemblies", a["bridgeAssembly"], loc + ".parameters.bridgeAssembly")
        bindings = []
        for source in sources:
            eligible = [target for target in targets
                        if index.compatible_scope(source, target, edge["matching"]["scope"])
                        and (source != target or typ not in ("shared_boundary", "separation"))]
            bindings.append({"source": source, "targets": eligible})
            for target in eligible:
                same = index.grid(source) == index.grid(target)
                if typ == "shared_boundary" and not target.startswith("@domain/") and not source.startswith("@domain/") and not same:
                    index.error(loc, "grid-conflict", "A shared physical border requires the same engine grid.")
                if typ == "grid_relation" and same != (a["relation"] == "same"):
                    index.error(loc, "grid-conflict", "Declared grid relation contradicts the explicit placement grids.")
                if typ == "service_link" and a["sameGrid"] and not same:
                    index.error(loc, "grid-conflict", "sameGrid service endpoints are assigned to different grids.")
        output.append({"edge": edge, "location": loc, "bindings": bindings})
    return output


def _selection(index: Index, relationships: list[dict]) -> dict[str, bool | None]:
    """Conservative fixed-point presence analysis; unresolved OR choices stay symbolic."""
    counts = {p: n["count"] for p, n in {**index.nodes, **index.features}.items()}
    status: dict[str, bool | None] = {p: False if c["max"] == 0 else None for p, c in counts.items()}
    choices = []
    for i, choice in enumerate(index.doc.get("choices", [])):
        loc = choice.get("_declarationLocation", f"$.choices[{i}]")
        index.errors.extend(_bounds_errors(choice, loc, ("minSelected", "preferredSelected", "maxSelected")))
        _evidence(index, choice, loc)
        known = [p for p in choice["members"] if index.require_space(p, loc + ".members")]
        declaration = choice.get("declarationScope")
        if declaration and any(not p.startswith(declaration + "/") for p in known):
            index.error(loc, "choice-scope", "A locally declared choice must select descendants of its declaring space.")
        if len(set(choice["members"])) != len(choice["members"]):
            index.error(loc, "choice-members", "Choice members must identify distinct template families after reference resolution.")
        if len({index.parents[p] for p in known}) > 1:
            index.error(loc, "choice-scope", "Choice members must have the same direct parent template.")
        if choice["maxSelected"] > len(choice["members"]):
            index.error(loc, "choice-count", "Choice maximum exceeds its number of template families.")
        if index.rule(choice)["priority"] == "mandatory":
            choices.append((choice, loc, known))
    reported = set()

    def set_status(p, state, loc, reason):
        if p.startswith("@domain/"):
            return False
        if status[p] is not None and status[p] != state:
            key = (loc, p, reason)
            if key not in reported:
                reported.add(key)
                index.error(loc, "selection-conflict", f"{reason}: {p!r} cannot be {'selected' if state else 'absent'} under its count/dependency bounds.")
            return False
        if status[p] is None:
            status[p] = state
            return True
        return False

    # A template's status is conditional on its parent instance existing. This is
    # deliberately not an assertion that every optional ancestor was selected.
    for p, count in counts.items():
        if count["min"] > 0:
            status[p] = True

    def disabled(p):
        return not p.startswith("@domain/") and (
            status.get(p) is False or any(status.get(a) is False for a in index.ancestors(p)))

    def forbid_source(p, loc):
        # A required child with an impossible dependency makes its nearest
        # optional ancestor unavailable, rather than forcing an absent suite.
        if p.startswith("@domain/"):
            index.error(loc, "selection-conflict", "A domain boundary requirement has no available physical target.")
            return False
        chain = [p] + list(reversed([a for a in index.ancestors(p) if a != p]))
        for a in chain:
            if index.count(a)["min"] == 0:
                return set_status(a, False, loc, "Required target has no eligible presence")
        return set_status(p, False, loc, "Required target has no eligible presence")
    for _ in range(len(status) + len(choices) + len(relationships) + 1):
        changed = False
        for choice, loc, members in choices:
            forced = [p for p in members if status[p] is True]
            possible = [p for p in members if status[p] is not False]
            if len(forced) > choice["maxSelected"] or len(possible) < choice["minSelected"]:
                key = (loc, "choice")
                if key not in reported:
                    reported.add(key)
                    index.error(loc, "choice-infeasible", "Mandatory choice is impossible under selected/available template counts.")
            if len(possible) == choice["minSelected"]:
                for p in possible:
                    changed |= set_status(p, True, loc, "Choice minimum")
            if len(forced) == choice["maxSelected"]:
                for p in possible:
                    if p not in forced:
                        changed |= set_status(p, False, loc, "Choice maximum")
        for r in relationships:
            edge = r["edge"]
            if index.rule(edge)["priority"] != "mandatory" or edge["matching"]["presence"] != "require_target":
                continue
            for binding in r["bindings"]:
                source = binding["source"]
                if disabled(source):
                    continue
                available = [t for t in binding["targets"] if not disabled(t)]
                if not available:
                    changed |= forbid_source(source, r["location"])
                elif (source.startswith("@domain/") or status.get(source) is True) and len(available) == 1:
                    target = available[0]
                    common = index.common(source, target)
                    source_chain = index.ancestors(source)
                    # A local implication can be conditional on the same parent
                    # suite. A stationwide implication fires only if the whole
                    # source ancestry is required. Otherwise preserve it symbolic.
                    conditional_chain = source_chain[source_chain.index(common) + 1:] if common in source_chain else source_chain
                    if all(status.get(a) is True for a in conditional_chain):
                        changed |= set_status(target, True, r["location"], "Mandatory dependency")
                        target_chain = index.ancestors(target)
                        target_chain = target_chain[target_chain.index(common) + 1:] if common in target_chain else target_chain
                        for ancestor in target_chain:
                            changed |= set_status(ancestor, True, r["location"], "Dependency target ancestor")
        if not changed:
            break
    # Injective capacity is arithmetic, not a choice of one arbitrary assignment.
    for r in relationships:
        edge = r["edge"]
        if index.rule(edge)["priority"] != "mandatory" or edge["matching"]["targets"] != "distinct":
            continue
        groups = {}
        for b in r["bindings"]:
            if disabled(b["source"]):
                continue
            targets = tuple(sorted(t for t in b["targets"] if not disabled(t)))
            if (edge["matching"]["presence"] == "if_both_present"
                    and not any(status.get(t) is True for t in targets)):
                continue
            scope = edge["matching"]["scope"]
            common = index.common(b["source"], targets[0]) if targets and scope == "same_parent" else None
            groups.setdefault((common, targets), []).append(b["source"])
        for (common, targets), sources in groups.items():
            if any(t.startswith("@domain/") for t in targets):
                index.error(r["location"], "domain-cardinality", "Domains have no instances and cannot participate in distinct matching.")
                continue
            def amount(p, lower):
                if status.get(p) is False:
                    return 0
                result = index.count(p)["min" if lower else "max"]
                if lower and status.get(p) is True:
                    result = max(1, result)
                chain = index.ancestors(p)
                # Per-common-parent counts; other scopes use stationwide products.
                chain = chain[chain.index(common) + 1:] if common in chain else chain
                for a in chain:
                    if a == p:
                        continue
                    result *= index.count(a)["min" if lower else "max"]
                return result
            if sum(amount(s, True) for s in sources) > sum(amount(t, False) for t in targets):
                index.error(r["location"], "distinct-capacity", "Mandatory distinct matching requires more source instances than compatible target capacity.")
    return status


def _area_analysis(index: Index, status: dict) -> dict:
    result = {}

    def interval(p):
        n = index.nodes[p]
        own_min, own_max = (0, None) if n["kind"] in PHYSICAL else (0, 0)
        aggregate_min, aggregate_max = 0, None
        for rule in n.get("requirements", []):
            if rule["type"] != "metric" or index.rule(rule)["priority"] != "mandatory":
                continue
            a = rule["parameters"]
            if a["metric"] not in ("area.exclusive", "area.aggregate"):
                continue
            if a["metric"] == "area.exclusive":
                own_min = max(own_min, a.get("min", 0))
                own_max = a.get("max") if own_max is None else min(own_max, a.get("max", own_max))
            else:
                aggregate_min = max(aggregate_min, a.get("min", 0))
                aggregate_max = a.get("max") if aggregate_max is None else min(aggregate_max, a.get("max", aggregate_max))
        low, high = own_min, own_max
        child_bounds = {}
        for child in index.children[p]:
            clow, chigh = interval(child)
            count = index.count(child)
            minimum = 0 if status.get(child) is False else max(count["min"], 1 if status.get(child) is True else 0)
            maximum = 0 if status.get(child) is False else count["max"]
            low += minimum * clow
            high = None if high is None or (chigh is None and maximum) else high + maximum * (chigh or 0)
            child_bounds[child] = (clow, chigh, minimum, maximum)
        # A choice counts families, not instances. Even optional family counts
        # contribute at least one subtree when a mandatory choice activates them.
        # Overlapping choice lower bounds use a maximum, avoiding double counting;
        # disjoint choice groups contribute independently.
        choice_groups = []
        for choice in index.doc.get("choices", []):
            if index.rule(choice)["priority"] != "mandatory" or not all(c in child_bounds for c in choice["members"]):
                continue
            members = set(choice["members"])
            forced = sum(child_bounds[c][2] > 0 for c in members)
            costs = sorted(child_bounds[c][0] for c in members
                           if child_bounds[c][2] == 0 and child_bounds[c][3] > 0)
            extra = sum(costs[:max(0, choice["minSelected"] - forced)])
            merged, contribution = members, extra
            remaining = list(choice_groups)
            while True:
                found = next((i for i, (old_members, _) in enumerate(remaining) if merged & old_members), None)
                if found is None:
                    break
                old_members, old_extra = remaining.pop(found)
                merged |= old_members
                contribution = max(contribution, old_extra)
            choice_groups = remaining + [(merged, contribution)]
            if high is not None:
                capacities = [child_bounds[c][1] * child_bounds[c][3] for c in members
                              if child_bounds[c][3] > 0 and child_bounds[c][1] is not None]
                if len(capacities) == sum(child_bounds[c][3] > 0 for c in members):
                    # This upper bound remains safe with overlapping choices:
                    # never remove another choice's capacity a second time.
                    fixed = [child_bounds[c][1] * child_bounds[c][3] for c in members if child_bounds[c][2] > 0]
                    optional = sorted([child_bounds[c][1] * child_bounds[c][3] for c in members
                                       if child_bounds[c][2] == 0 and child_bounds[c][3] > 0], reverse=True)
                    cap = sum(fixed) + sum(optional[:max(0, choice["maxSelected"] - forced)])
                    untouched = own_max
                    if untouched is not None and all(v[1] is not None or v[3] == 0 for c, v in child_bounds.items() if c not in members):
                        untouched += sum((v[1] or 0) * v[3] for c, v in child_bounds.items() if c not in members)
                        high = min(high, untouched + cap)
        low += sum(extra for _, extra in choice_groups)
        if (own_max is not None and own_min > own_max) or (aggregate_max is not None and low > aggregate_max) or (high is not None and high < aggregate_min):
            index.error(index.locations[p] + ".requirements", "area-infeasible", "Exclusive/descendant count budgets cannot fit the mandatory aggregate area interval.")
        low = max(low, aggregate_min)
        high = aggregate_max if high is None else min(high, aggregate_max if aggregate_max is not None else high)
        result[p] = {"exclusive": {"min": own_min, "max": own_max}, "aggregate": {"min": low, "max": high},
                     "unit": "tile_area", "basis": "symbolic_bounds_not_geometry"}
        return low, high

    for p in index.children[None]:
        interval(p)
    return result


def _analyse(document: dict):
    index = Index(resolve_document(document))
    for key, interface in index.tables['interfaces'].items():
        passage = interface.get('passage')
        if passage:
            if 'crew_passage' not in interface['capabilities']:
                index.error('interfaces.'+key, 'passage-interface', 'Passage width/direction requires crew passage capability.')
            if passage.get('maxWidth', float('inf')) < passage['minWidth']:
                index.error('interfaces.'+key, 'passage-width', 'Passage maximum width cannot be smaller than its minimum.')
            if passage['direction'] != 'bidirectional' and (
                    interface.get('geometry') == 'open_edge' or not interface['construction']):
                index.error('interfaces.'+key, 'passage-direction', 'A directional passage needs an actual controlling construction.')
        if interface.get('geometry') == 'open_edge' and (
                interface['construction'] or interface['normalState'] != 'open'
                or interface['access']['anyOf'] or interface['airtight']
                or 'crew_passage' not in interface['capabilities']
                or 'docking' in interface['capabilities']):
            index.error('interfaces.' + key, 'open-edge-interface',
                        'An open floor edge must be permanently open, unrestricted, non-airtight crew passage without construction or docking.')
    _check_extension_schemas(index)
    # Do not resolve invalid/external extension schemas, even on optional rules.
    if any(d["code"] in ("extension-schema", "external-schema-reference") for d in index.errors):
        return index, [], {}, {}
    _assembly_semantics(index)
    _space_semantics(index)
    relationships = _relationships(index)
    status = _selection(index, relationships)
    areas = _area_analysis(index, status)
    return index, relationships, status, areas


def validate_document(document: Any) -> list[dict]:
    """Return deterministic diagnostics. No geometry/native/runtime success is inferred."""
    errors = _non_json(document)
    if errors:
        return errors
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    validator = jsonschema.Draft202012Validator(schema)
    errors = [diagnostic(_path(e.absolute_path), "schema", e.message)
              for e in sorted(validator.iter_errors(document), key=lambda e: (_path(e.absolute_path), e.message))]
    if errors:
        return errors
    try:
        index, _, _, _ = _analyse(document)
        for path, node in index.nodes.items():
            config = node.get('organization')
            if not config:
                continue
            loc = index.locations[path] + '.organization'
            if node['kind'] != 'department':
                index.error(loc, 'organization', 'Placement organization currently belongs to department owners; child suites retain their nested definitions.')
            if config.get('independent') and config['mode'] != 'mixed':
                index.error(loc, 'organization', 'Independent members require mixed organization.')
            declared = set()
            ids = set()
            for suite in config.get('suites', []):
                if config['mode'] == 'mixed' and suite['id'] == 'core':
                    index.error(loc, 'organization', 'The core scope ID is reserved for mixed organization.')
                if suite['id'] in ids or suite['id'] in {index.nodes[k]['id'] for k in index.children[path]}:
                    index.error(loc, 'organization', 'Suite IDs must be unique and differ from child IDs.')
                ids.add(suite['id'])
                for member in suite['members']:
                    if member in declared:
                        index.error(loc, 'organization', 'A child cannot belong to multiple placement suites.')
                    declared.add(member)
            for member in [*config.get('independent', []), *declared]:
                if member not in index.children[path]:
                    index.error(loc, 'organization', f'Placement member {member!r} must name a direct child.')
            if declared & set(config.get('independent', [])):
                index.error(loc, 'organization', 'A placement suite member cannot also be independent.')
            if config['mode'] == 'clustered' and config.get('suites'):
                index.error(loc, 'organization', 'Explicit placement suites require distributed or mixed organization.')
    except ProgramError as exc:
        return exc.diagnostics
    # Fixed-point propagation can reveal the same conflict from more than one pass.
    unique = {(e["path"], e["code"], e["message"]): e for e in index.errors}
    return [unique[k] for k in sorted(unique)]


def compile_document(document: dict) -> dict:
    """Compile symbolic templates/bindings; deliberately do not select a layout."""
    diagnostics = validate_document(document)
    if any(d["severity"] == "error" for d in diagnostics):
        raise ProgramError(diagnostics)
    index, relations, status, areas = _analyse(document)

    def scope(p):
        if p.startswith("@domain/"):
            return []
        chain = index.ancestors(p) + ([p] if p in index.features else [])
        return [{"template": a, "index": f"i{j}", "count": index.count(a)} for j, a in enumerate(chain)]

    def tree(p):
        n = index.nodes[p]
        return {"path": p, "kind": n["kind"], "logicalOwner": index.owners[p],
                "parent": index.parents[p], "nesting": n["placement"]["nesting"],
                "placement": {**n["placement"], "surface": n["placement"].get("surface", "any")},
                "organization": n.get("organization", {"mode": "clustered"}),
                "grid": n["placement"]["grid"], "countPerParent": n["count"],
                "presence": "required_when_parent_present" if status[p] is True else "unavailable" if status[p] is False else "choice",
                "instanceVariables": scope(p), "areaBounds": areas[p],
                "requirements": [{**r, **index.rule(r)} for r in n.get("requirements", [])],
                "declaredRelationships": [r["edge"]["id"] for r in relations if r["edge"].get("declarationScope") == p],
                "declaredChoices": [c["id"] for c in index.doc.get("choices", []) if c.get("declarationScope") == p],
                "features": [{**f, "path": p + "#" + f["id"], "countPerRoom": f["count"],
                              "instanceVariables": scope(p + "#" + f["id"]), "abstract": not f["prototypes"] and not f.get("assembly"),
                              "assembly": f.get("assembly"), "ports": f["ports"]} for f in n.get("features", [])],
                "children": [tree(child) for child in index.children[p]]}

    bindings, implications, unresolved, external = [], [], [], []
    def available(p):
        return index.available(p) and status.get(p) is not False and all(status.get(a) is not False for a in index.ancestors(p))

    for r in relations:
        edge = r["edge"]
        compiled = {"id": edge["id"], "type": edge["type"], **index.rule(edge),
                    "authoredId": edge["authoredId"], "declarationScope": edge["declarationScope"],
                    "matching": edge["matching"], "parameters": edge["parameters"], "sourceBindings": []}
        for b in r["bindings"]:
            source = b["source"]
            candidates = [{"endpoint": t, "instanceVariables": scope(t),
                           "sharedAncestorInstance": index.common(source, t) if edge["matching"]["scope"] == "same_parent" else None,
                           "available": available(t)} for t in b["targets"]]
            compiled["sourceBindings"].append({"source": source, "available": available(source),
                                                "condition": "for_each_selected_source_instance",
                                                "instanceVariables": scope(source), "candidates": candidates})
            if index.rule(edge)["priority"] == "mandatory" and edge["matching"]["presence"] == "require_target":
                implications.append({"relationship": edge["id"], "ifSelected": source,
                                     "requiresAtLeastOneOf": [t["endpoint"] for t in candidates if t["available"]],
                                     "scope": edge["matching"]["scope"]})
            if edge["matching"]["targets"] in ("one", "distinct") and any(not t["endpoint"].startswith("@domain/") for t in candidates):
                unresolved.append({"relationship": edge["id"], "source": source,
                                   "decision": "injective_instance_matching" if edge["matching"]["targets"] == "distinct" else "target_instance_selection"})
        bindings.append(compiled)
    for p, n in index.nodes.items():
        if n["count"]["min"] != n["count"]["max"]:
            unresolved.append({"subject": p, "decision": "instance_count_selection", "countPerParent": n["count"]})
        for r in n.get("requirements", []):
            if r["type"].startswith("x_"):
                ext = index.tables["extensions"][r["type"]]
                external.append({"subject": p, "rule": r["id"], "type": r["type"],
                                 "priority": index.rule(r)["priority"], "evaluator": ext["evaluator"]})
    for edge in index.doc.get("relationships", []):
        if edge["type"].startswith("x_"):
            ext = index.tables["extensions"][edge["type"]]
            external.append({"relationship": edge["id"], "type": edge["type"],
                             "authoredId": edge["authoredId"], "declarationScope": edge["declarationScope"],
                             "priority": index.rule(edge)["priority"], "evaluator": ext["evaluator"]})
    return {"format": "ss14-station-architect-symbolic", "schemaVersion": 1,
            "sourceProgram": {"id": document["id"], "schemaVersion": 3}, "authoringValid": True,
            "certification": {"geometry": "not_evaluated", "nativePrototypeExistence": "not_evaluated",
                              "physicalInterfaces": "not_evaluated", "runtimeOperation": "not_evaluated",
                              "selectionFeasibility": "conservative_interval_checks_only",
                              "instanceMatching": "symbolic_candidates_only"},
            "tree": [tree(p) for p in index.children[None]], "relationships": bindings,
            "definitions": {key: document.get(key, []) for key in ("accessProfiles", "environments", "grids", "domains", "interfaces", "assemblies", "extensions", "evidence")},
            "activationImplications": implications,
            "choices": [{k: v for k, v in c.items() if not k.startswith("_")} for c in index.doc.get("choices", [])],
            "remainingDecisions": unresolved, "externalEvaluatorsRequired": external,
            "diagnostics": diagnostics}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("program", type=Path)
    parser.add_argument("--compile", dest="output", type=Path, metavar="JSON", help="write a symbolic compilation, never a generated map")
    parser.add_argument("--json", action="store_true", help="print machine-readable diagnostics")
    args = parser.parse_args(argv)
    try:
        doc = load_program(args.program)
        errors = validate_document(doc)
        if errors:
            raise ProgramError(errors)
        compiled = compile_document(doc)
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(compiled, indent=2, allow_nan=False) + "\n", encoding="utf-8")
        summary = {"authoringValid": True, "diagnostics": [], "geometryCertified": False,
                   "runtimeCertified": False, "compiled": str(args.output) if args.output else None}
        summary["externalEvaluatorsRequired"] = compiled["externalEvaluatorsRequired"]
        print(json.dumps(summary, indent=2) if args.json else
              f"Authoring valid: {doc['id']}. Geometry and runtime operation are not certified."
              + (f" {len(compiled['externalEvaluatorsRequired'])} rule(s) require external evaluators." if compiled["externalEvaluatorsRequired"] else "")
              + (f" Symbolic output: {args.output}" if args.output else ""))
        return 0
    except ProgramError as exc:
        if args.json:
            print(json.dumps({"authoringValid": False, "diagnostics": exc.diagnostics}, indent=2))
        else:
            for d in exc.diagnostics:
                print(f"{d['severity']}: {d['path']}: [{d['code']}] {d['message']}")
        return 1
    except OSError as exc:
        print(json.dumps({"authoringValid": False, "diagnostics": [diagnostic("$", "output", str(exc))]}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
