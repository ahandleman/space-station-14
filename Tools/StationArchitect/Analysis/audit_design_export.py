"""Read an exported bundle independently and check references and serialized grid data.

This checks file/data consistency. It does not execute SS14 or certify a round.
Requires PyYAML; uses the same repository prototype resolver as the map survey.
"""
import argparse
import base64
import collections
import json
import math
from pathlib import Path
import re
import struct
import zipfile

from analyze_maps import SourceLoader, expand_variants, prototype_index
from audit_native_systems import audit_native_systems, preserve_native_node_types, crew_collision
import yaml


def preserve_native_shape_types():
    # The general map survey discards YAML tags. Here the tag identifies the
    # native default dimensions of an empty AABB/circle fixture mapping.
    def constructor(shape_type):
        def read(loader, node):
            return {**loader.construct_mapping(node, deep=True), "_dockShapeType": shape_type}
        return read
    for shape_type in ("PhysShapeAabb", "PhysShapeCircle", "PhysShapePolygon", "PolygonShape", "EdgeShape", "ChainShape", "PhysShapeGrid"):
        SourceLoader.add_constructor("!type:" + shape_type, constructor(shape_type))


def finite(value, minimum=0):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= minimum


def decode_atmosphere(component, gases, errors, context):
    """Read TileAtmosCollectionSerializer's v2 mapping: chunk -> mix -> uint bits."""
    if not isinstance(component, dict) or component.get("version") != 2:
        errors.append(context + ": missing native atmosphere version 2.")
        return {}, [], 0
    data = component.get("data")
    if not isinstance(data, dict):
        errors.append(context + ": atmosphere data must be a mapping, not a scalar blob.")
        return {}, [], 0
    size, mixes, chunks = data.get("chunkSize"), data.get("uniqueMixes"), data.get("tiles")
    if not isinstance(size, int) or not 1 <= size <= 5 or not isinstance(mixes, list) or not isinstance(chunks, dict):
        errors.append(context + ": malformed atmosphere chunk size, mixes or tiles mapping.")
        return {}, [], 0
    for index, mix in enumerate(mixes):
        if not isinstance(mix, dict) or not finite(mix.get("temperature"), 1e-12) or not finite(mix.get("volume"), 1e-12) or not isinstance(mix.get("moles"), dict):
            errors.append(f"{context}: invalid mixture {index}.")
            continue
        if any(gas not in gases or not finite(amount) for gas, amount in mix["moles"].items()):
            errors.append(f"{context}: mixture {index} has unknown gas names or invalid moles.")
    result = {}
    for origin, flags in chunks.items():
        try:
            cx, cy = map(int, str(origin).split(","))
            if not isinstance(flags, dict):
                raise ValueError("flags are not a mapping")
            occupied = 0
            for raw_index, mask in flags.items():
                index = int(raw_index)
                if not 0 <= index < len(mixes) or not isinstance(mask, int) or isinstance(mask, bool) or not 0 <= mask < 1 << (size * size):
                    raise ValueError("mixture index or unsigned bit mask is out of range")
                if occupied & mask:
                    errors.append(f"{context}: overlapping gas assignments in atmosphere chunk {origin}.")
                occupied |= mask
                for bit in range(size * size):
                    if mask & (1 << bit):
                        result[(cx * size + bit % size, cy * size + bit // size)] = mixes[index]
        except (TypeError, ValueError) as exception:
            errors.append(f"{context}: invalid atmosphere chunk {origin}: {exception}.")
    return result, mixes, len(chunks)


def audit_docking(design, manifest, tiles, entities, prototypes, grid_uid, errors, warnings):
    """Reconstruct authored volumes, then inspect decoded map tiles and native hardware."""
    layout = design["layout"]
    cells = lambda values: {(cell["x"], cell["y"]) for cell in values}
    definitions = {}
    def walk(children, prefix):
        for room in children:
            path = prefix + "/" + room["id"]
            definitions[path] = room
            walk(room.get("rooms", []), path)
    for department in design["definition"]["departments"]:
        walk(department["rooms"], department["id"])
    walk(design["definition"].get("maintenance", {}).get("rooms", []), "_maintenance")
    rooms = {room["id"]: room for room in layout["rooms"] if room["enabled"] and not room["orphaned"]}
    equipment = {entity["id"]: entity for entity in layout["equipment"]}
    ports = {port["id"]: port for port in layout["interfaces"]}
    declared = {(room["id"], rule["id"]): (room, rule, next((item for item in definitions.get(room["definitionPath"], {}).get("contents", []) if item["id"] == rule["contentId"]), None))
                for room in rooms.values() for rule in definitions.get(room["definitionPath"], {}).get("exterior", {}).get("docks", [])}
    saved_docks = layout.get("docks", [])
    manifest_rows = manifest.get("dockingReservations", [])
    if {item["id"] for item in manifest_rows} != {dock["id"] for dock in saved_docks} or len(manifest_rows) != len(saved_docks):
        errors.append("Dock manifest records differ from actual project reservations.")
    records = {item["id"]: item for item in manifest_rows}
    positions, native = {}, {}
    def rotation(value):
        return float(str(value or "0").removesuffix(" deg")) % 360
    def position(uid, active=None):
        if uid == grid_uid:
            return 0., 0., 0.
        if uid in positions:
            return positions[uid]
        active = set() if active is None else active
        if uid in active or uid not in entities:
            return None
        active.add(uid)
        transform = next((component for component in entities[uid].get("components", []) if component["type"] == "Transform"), {})
        parent = position(transform.get("parent"), active)
        if parent is None:
            return None
        x, y = map(float, str(transform.get("pos", "0,0")).split(","))
        angle = math.radians(parent[2]); cosine, sine = math.cos(angle), math.sin(angle)
        positions[uid] = (parent[0] + cosine*x - sine*y, parent[1] + sine*x + cosine*y, (parent[2] + rotation(transform.get("rot"))) % 360)
        return positions[uid]
    occupied_by_entities = collections.defaultdict(set)
    colliders = collections.defaultdict(set)
    standing_colliders = collections.defaultdict(set)
    for uid, entity in entities.items():
        point = position(uid)
        if point is None or uid == grid_uid:
            continue
        components = {name: dict(value) for name, value in prototypes.get(entity["prototype"], {}).get("components", {}).items()}
        explicit = {component["type"]: component for component in entity.get("components", [])}
        for name, value in explicit.items():
            components[name] = {**components.get(name, {}), **value}
        if not all(math.isfinite(value) for value in point):
            errors.append(f"Entity {uid} has a non-finite docking-audit transform.")
            continue
        native[uid] = (point, components, explicit)
        occupied_by_entities[(math.floor(point[0]), math.floor(point[1]))].add(uid)
        # Native collider extents catch bodies that intrude from an adjacent tile.
        if components.get("Physics", {}).get("canCollide") is False:
            continue
        for fixture in components.get("Fixtures", {}).get("fixtures", {}).values():
            if fixture.get("hard") is False:
                continue
            # Fixture.Shape itself defaults to a unit-centered PhysShapeAabb.
            shape = fixture.get("shape", {"_dockShapeType": "PhysShapeAabb"})
            if shape.get("_dockShapeType") == "PhysShapeAabb" and "bounds" not in shape:
                shape = {**shape, "bounds": "-0.5,-0.5,0.5,0.5"}
            elif shape.get("_dockShapeType") == "PhysShapeCircle" and "radius" not in shape:
                shape = {**shape, "radius": .5}
            vertices = []
            if "bounds" in shape:
                left, bottom, right, top = map(float, str(shape["bounds"]).split(","))
                vertices = [(left, bottom), (left, top), (right, bottom), (right, top)]
            elif "vertices" in shape:
                vertices = [tuple(map(float, str(vertex).split(","))) for vertex in shape["vertices"]]
            elif any(key in shape for key in ("vertex0", "vertex1", "vertex2", "vertex3")):
                vertices = [tuple(map(float, str(shape[key]).split(","))) for key in ("vertex0", "vertex1", "vertex2", "vertex3") if key in shape]
            elif "radius" in shape:
                radius = float(shape["radius"])
                center = tuple(map(float, str(shape.get("position", "0,0")).split(",")))
                vertices = [(center[0]-radius, center[1]-radius), (center[0]+radius, center[1]+radius)]
            if not vertices:
                errors.append(f"Entity {uid} has an unsupported native collider shape; its docking clearance cannot be proven.")
                continue
            angle = math.radians(point[2]); cosine, sine = math.cos(angle), math.sin(angle)
            corners = [(point[0]+cosine*x-sine*y, point[1]+sine*x+cosine*y) for x, y in vertices]
            minimum_x, maximum_x = math.floor(min(x for x, _ in corners)+1e-7), math.ceil(max(x for x, _ in corners)-1e-7)
            minimum_y, maximum_y = math.floor(min(y for _, y in corners)+1e-7), math.ceil(max(y for _, y in corners)-1e-7)
            if (maximum_x-minimum_x)*(maximum_y-minimum_y) > 262144:
                errors.append(f"Entity {uid} has collider extents beyond the bounded docking audit.")
                continue
            for x in range(minimum_x, maximum_x):
                for y in range(minimum_y, maximum_y):
                    occupied_by_entities[(x, y)].add(uid)
                    colliders[(x, y)].add(uid)
                    if crew_collision(components) is not False:
                        standing_colliders[(x, y)].add(uid)
    verified, used_volume, checked = collections.Counter(), set(), []
    directions = {(0, 1): "north", (1, 0): "east", (0, -1): "south", (-1, 0): "west"}
    for dock in saved_docks:
        context = "Dock " + dock["id"]
        before = len(errors)
        source = declared.get((dock["roomId"], dock["requirementId"]))
        if source is None or source[2] is None:
            errors.append(context + ": missing authored room/rule/content binding.")
            continue
        room, rule, content = source
        boundary = cells(dock["boundaryCells"]); outward = (dock["outward"]["x"], dock["outward"]["y"])
        if len(boundary) != 1 or len(dock["boundaryCells"]) != 1 or outward not in directions or directions[outward] not in rule["directions"]:
            errors.append(context + ": invalid native dock anchor or cardinal orientation.")
            continue
        anchor = next(iter(boundary)); tangent = (-outward[1], outward[0]); width, depth = rule["berthWidth"], rule["berthDepth"]
        clearance, offset = rule.get("clearance", 1), rule.get("berthOffset", 0)
        approach_width = rule.get("approachWidth", 0) or width+2*clearance
        if not 1 <= width <= 128 or not 1 <= depth <= 128 or not 0 <= clearance <= 8 or not width+2*clearance <= approach_width <= 256 or not 1 <= rule["approachLength"] <= 512:
            errors.append(context + ": unbounded authored dimensions.")
            continue
        if not -(width//2)+offset <= 0 < -(width//2)+offset+width:
            errors.append(context + ": the actual docking airlock is outside the unpadded shuttle envelope.")
        inside = anchor[0]-outward[0], anchor[1]-outward[1]
        outside = anchor[0]+outward[0], anchor[1]+outward[1]
        if inside not in cells(room["cells"]) or tiles.get(inside, "Space") == "Space" or standing_colliders.get(inside):
            blockers = ", ".join(str(uid) + ":" + entities[uid]["prototype"] for uid in sorted(standing_colliders.get(inside, [])))
            errors.append(context + ": the serialized dock has no clear owned interior floor approach" + (" (" + blockers + ")" if blockers else "") + ".")
        if tiles.get(outside, "Space") != "Space" or occupied_by_entities.get(outside):
            errors.append(context + ": the docking face does not open into actual empty exterior space.")
        far = depth+clearance
        exit_distance = design["settings"]["width"]-anchor[0] if outward == (1, 0) else anchor[0]+1 if outward == (-1, 0) else design["settings"]["height"]-anchor[1] if outward == (0, 1) else anchor[1]+1
        end = max(far+rule["approachLength"], exit_distance)
        point = lambda normal, lateral: (anchor[0]+outward[0]*normal+tangent[0]*lateral, anchor[1]+outward[1]*normal+tangent[1]*lateral)
        berth = {point(normal, lateral) for normal in range(1, far+1) for lateral in range(-(width//2)+offset-clearance, -(width//2)+offset+width+clearance)}
        approach = {point(normal, lateral) for normal in range(far+1, end+1) for lateral in range(-(approach_width//2)+offset, -(approach_width//2)+offset+approach_width)}
        volume = berth | approach
        if cells(dock["berthCells"]) != berth or len(dock["berthCells"]) != len(berth) or cells(dock["approachCells"]) != approach or len(dock["approachCells"]) != len(approach):
            errors.append(context + ": saved masks differ from independently reconstructed authored clearance.")
        if volume & used_volume:
            errors.append(context + ": clearance overlaps another declared berth or approach.")
        used_volume |= volume
        if any(tiles.get(cell, "Space") != "Space" for cell in volume):
            errors.append(context + ": serialized non-space tiles occupy the berth or approach.")
        if any(occupied_by_entities.get(cell) for cell in volume):
            errors.append(context + ": serialized entities or native collider extents occupy the berth or approach.")
        # Verify the complete lane to actual unbounded exterior, rather than a small vacuum courtyard.
        for cell in {cell for cell, tile in tiles.items() if tile != "Space"} | set(occupied_by_entities):
            delta = cell[0]-anchor[0], cell[1]-anchor[1]
            normal, lateral = delta[0]*outward[0]+delta[1]*outward[1], delta[0]*tangent[0]+delta[1]*tangent[1]
            if normal > end and -(approach_width//2)+offset <= lateral < -(approach_width//2)+offset+approach_width:
                errors.append(context + ": the straight approach is blocked beyond the project canvas.")
                break
        planned = equipment.get(dock["entityId"]); port = ports.get(dock["interfaceId"]); materialized = False
        record = records.get(dock["id"], {})
        if planned is None:
            warnings.append(context + ": preferred hardware has not been materialized.")
        else:
            expected_rotation = {(0, -1): 0, (1, 0): 90, (0, 1): 180, (-1, 0): 270}[outward]
            matches = [(uid, value) for uid, value in native.items() if entities[uid]["prototype"] == planned["prototype"] and math.isclose(value[0][0], anchor[0]+.5) and math.isclose(value[0][1], anchor[1]+.5) and math.isclose(value[0][2], expected_rotation, abs_tol=1e-6)]
            if planned["prototype"] not in content["prototypes"] or planned["roomId"] != dock["roomId"] or planned["requirementId"] != rule["contentId"] or planned["interfaceId"] != dock["interfaceId"] or cells(planned["footprint"]) != boundary or planned["position"] != {"x": anchor[0], "y": anchor[1]} or planned["rotation"] % 360 != expected_rotation:
                errors.append(context + ": planned equipment is not the exact bound native docking hardware.")
            if not port or port["type"] != "dockingPort" or port["kind"] != "walking" or port["from"] != room["id"] or port["to"] != "$space" or cells(port["cells"]) != boundary or port["fromApproach"] != {"x": inside[0], "y": inside[1]} or port["toApproach"] != {"x": outside[0], "y": outside[1]}:
                errors.append(context + ": native hardware is not aligned with its actual room-to-space docking interface.")
            if len(matches) != 1:
                errors.append(context + ": serialized exact prototype/position/rotation does not match the bound docking hardware.")
            else:
                uid, (_position, components, explicit) = matches[0]
                if colliders.get(anchor, set()) - {uid}:
                    errors.append(context + ": another native collider occupies the docking hardware boundary.")
                materialized = "Docking" in explicit and "DockingSignalControl" in explicit
                if not all(name in components for name in ("Docking", "DockingSignalControl", "Door")):
                    errors.append(context + ": exported hardware lacks native Docking, DockingSignalControl or Door components.")
                inherited = prototypes.get(planned["prototype"], {}).get("components", {})
                for name, field in [("PriorityDock", "tag"), ("Docking", "dockLegendCategory")]:
                    if field in inherited.get(name, {}) and components.get(name, {}).get(field) != inherited[name][field]:
                        errors.append(context + f": inherited native {name}.{field} was lost during export.")
        for field in ("roomId", "requirementId", "contentId", "entityId", "interfaceId", "outward", "boundaryCells", "berthCells", "approachCells"):
            if record.get(field) != dock.get(field):
                errors.append(context + ": manifest/project mismatch for " + field + ".")
        if record.get("nativeDockingComponentsMaterialized", False) != materialized:
            errors.append(context + ": manifest native-component claim differs from actual serialized component mappings.")
        accepted = planned is not None and len(errors) == before
        if accepted:
            verified[(dock["roomId"], dock["requirementId"])] += 1
        if record.get("geometryAndHardwareAccepted") and not accepted:
            errors.append(context + ": manifest claims a clearance/hardware proof contradicted by serialized map data.")
        checked.append({"id": dock["id"], "clearBerthCells": len(berth), "clearApproachCells": len(approach), "geometryAndHardwareVerified": accepted})
    for key, (_room, rule, content) in declared.items():
        if content is not None and not content["count"]["min"] <= verified[key] <= content["count"]["max"]:
            errors.append(f"Dock {key[0]}/{rule['id']}: serialized verified hardware count {verified[key]} violates its authored minimum/maximum.")
    return checked


def audit(repo, bundle):
    preserve_native_shape_types()
    preserve_native_node_types()
    prototypes = prototype_index(repo)
    known = collections.defaultdict(set)
    for path in sorted((repo / "Resources/Prototypes").rglob("*.yml")):
        for item in yaml.load(path.read_text(encoding="utf-8-sig"), Loader=SourceLoader) or []:
            if isinstance(item, dict):
                for expanded in expand_variants(item):
                    if isinstance(expanded.get("id"), str):
                        known[expanded.get("type")].add(expanded["id"])
    gas_enum = (repo / "Content.Shared/Atmos/Atmospherics.cs").read_text(encoding="utf-8-sig")
    gases = set(re.findall(r"\b(\w+)\s*=\s*\d+", re.search(r"public enum Gas[^\{]*\{([^}]+)\}", gas_enum).group(1)))
    errors, warnings = [], []
    with zipfile.ZipFile(bundle) as archive:
        if len(archive.namelist()) != len(set(archive.namelist())):
            errors.append("The bundle contains duplicate archive paths.")
        manifest = json.loads(archive.read("systems-manifest.json"))
        design = json.loads(archive.read("project.station.json"))
        map_path = "Resources/Maps/Generated/" + manifest["mapFile"]
        descriptor_path = "Resources/Prototypes/Maps/Generated/" + manifest["descriptorFile"]
        data = yaml.load(archive.read(map_path), Loader=SourceLoader)
        descriptors = yaml.load(archive.read(descriptor_path), Loader=SourceLoader)
    if data.get("meta", {}).get("format") != 7 or data.get("meta", {}).get("category") != "Map":
        errors.append("The map is not format 7.")
    entities, used, at_cell = {}, collections.Counter(), collections.defaultdict(set)
    for group in data["entities"]:
        prototype = group["proto"]
        if prototype and prototype not in prototypes:
            errors.append("Unknown exported entity prototype: " + prototype)
        used[prototype] += len(group["entities"])
        for entity in group["entities"]:
            uid = entity["uid"]
            if uid in entities:
                errors.append("Duplicate entity UID: " + str(uid))
            entities[uid] = entity | {"prototype": prototype}
            transform = next((component for component in entity.get("components", []) if component["type"] == "Transform"), {})
            if "pos" in transform:
                x, y = map(float, transform["pos"].split(","))
                if math.isfinite(x) and math.isfinite(y):
                    at_cell[(transform.get("parent"), math.floor(x), math.floor(y))].add(prototype)
                else:
                    errors.append("An entity has a non-finite transform position.")
    if len(entities) != data["meta"]["entityCount"] or len(entities) != manifest["entities"]:
        errors.append("The metadata entity count differs from actual entities.")
    for entity in entities.values():
        component_names = [component["type"] for component in entity.get("components", [])]
        if len(set(component_names)) != len(component_names):
            errors.append("An entity has duplicate component types.")
        for component in entity.get("components", []):
            if component["type"] == "Transform" and component.get("parent") not in (None, 0) and component["parent"] not in entities:
                errors.append("An entity's transform parent is missing.")
            if component["type"] == "AccessReader" and any(tag not in known["accessLevel"] for tags in component.get("access", []) for tag in tags):
                errors.append("An access reader references an unknown access-level prototype.")
    if set(data["maps"]) & set(data["grids"]):
        errors.append("Map and grid UIDs must be distinct.")
    for uid in [*data["maps"], *data["grids"]]:
        if uid not in entities:
            errors.append("A declared map/grid entity is missing.")
    for uid in data["maps"]:
        if not any(component["type"] == "Map" for component in entities[uid].get("components", [])):
            errors.append("A declared map entity is missing its Map component.")
    parents = {uid: next((component.get("parent") for component in entity.get("components", []) if component["type"] == "Transform"), None) for uid, entity in entities.items()}
    for uid in entities:
        current, seen = uid, set()
        while current not in (0, None):
            if current in seen:
                errors.append("An entity's transform ancestry contains a cycle.")
                break
            if current not in parents:
                break  # The direct missing-parent check above already reports this.
            seen.add(current)
            current = parents[current]
    tiles, atmospheres, chunk_count, mixture_count, atmosphere_chunks = {}, {}, 0, 0, 0
    palette = {int(key): value for key, value in data["tilemap"].items()}
    if palette.get(0) != "Space" or len(palette) != len(data["tilemap"]):
        errors.append("The tile palette has duplicate numeric keys or no Space entry at zero.")
    for name in palette.values():
        if name not in known["tile"]:
            errors.append("Unknown exported tile prototype: " + name)
    for grid_uid in data["grids"]:
        components = {component["type"]: component for component in entities[grid_uid]["components"]}
        if components.get("Transform", {}).get("parent") not in data["maps"]:
            errors.append("A grid is not parented to the bundled map.")
        for key, chunk in components["MapGrid"]["chunks"].items():
            chunk_count += 1
            if chunk["version"] != 7 or str(chunk["ind"]) != str(key):
                errors.append("Unexpected chunk version or mismatched chunk index.")
            chunk_x, chunk_y = map(int, key.split(","))
            size = int(chunk.get("size", 16))
            raw = base64.b64decode(chunk["tiles"], validate=True)
            if len(raw) != size * size * 7:
                errors.append("A chunk has an invalid tile byte count.")
                continue
            for index, (tile_id, _flags, _variant, _rotation) in enumerate(struct.iter_unpack("<iBBB", raw)):
                if tile_id not in palette:
                    errors.append("A tile ID is absent from the tile palette.")
                if tile_id:
                    tiles[(chunk_x*size+index%size, chunk_y*size+index//size)] = palette.get(tile_id)
        atmosphere, mixes, count = decode_atmosphere(components.get("GridAtmosphere"), gases, errors, f"Grid {grid_uid}")
        atmospheres.update(atmosphere)
        mixture_count += len(mixes)
        atmosphere_chunks += count
    layout = design["layout"]
    expected_floor = {(cell["x"], cell["y"]) for room in layout["rooms"] if room["enabled"] and not room["orphaned"] for cell in room["cells"]}
    expected_floor |= {(cell["x"], cell["y"]) for key in ("publicCorridors", "maintenance") for cell in layout[key]}
    expected_floor |= {(cell["x"], cell["y"]) for interface in layout["interfaces"] for cell in interface["cells"]}
    if not expected_floor.issubset(tiles):
        errors.append("Some planned walkable floor or interface tiles are missing from the serialized map.")
    expected_materialized = expected_floor | {(cell["x"], cell["y"]) for key in ("walls", "hull", "windows") for cell in layout[key]}
    if set(tiles) != expected_materialized or len(tiles) != manifest["floorTiles"]:
        errors.append("Serialized tile coverage differs from project masks or manifest.")
    grid_uid, profile = data["grids"][0], design["settings"]["export"]
    layer_cells = lambda key: {(cell["x"], cell["y"]) for cell in layout[key]}
    openings = {(cell["x"], cell["y"]) for port in layout["interfaces"] if port["kind"] != "utility" for cell in port["cells"]}
    checked_hardware = 0

    def require_hardware(prototype, cell, context):
        nonlocal checked_hardware
        checked_hardware += 1
        if prototype not in at_cell[(grid_uid, *cell)]:
            errors.append(f"Planned {context} lacks actual {prototype} hardware at {cell}.")

    for cell in (layer_cells("walls") | layer_cells("hull")) - layer_cells("windows") - openings:
        require_hardware(profile["wallPrototype"], cell, "pressure wall")
    for cell in layer_cells("windows") - openings:
        require_hardware(profile["windowPrototype"], cell, "window")
    equipment_ports = {entity["interfaceId"] for entity in layout["equipment"] if entity["interfaceId"]}
    for port in layout["interfaces"]:
        if port["id"] in equipment_ports:
            continue
        systems = design.get("definition", {}).get("systems", {})
        native_kind = {"pipe": "gas", "cable": "power"}.get(port["type"])
        if port["kind"] == "utility" and systems.get("enabled") and any(network.get("kind") == native_kind for network in systems.get("networks", [])):
            continue
        prototype = {"counter": profile["counterPrototype"], "window": profile["windowPrototype"], "pipe": profile["pipePrototype"], "cable": profile["cablePrototype"], "dockingPort": profile.get("dockingPortPrototype", "AirlockShuttle")}.get(port["type"])
        if prototype is None:
            prototype = profile["spaceAirlockPrototype"] if port["from"] in ("$space", "$exterior-maintenance") or port["to"] in ("$space", "$exterior-maintenance") else profile["doorPrototype"]
        for cell in port["cells"]:
            require_hardware(prototype, (cell["x"], cell["y"]), "interface " + port["id"])
    for entity in layout["equipment"]:
        cell = entity["position"]
        require_hardware(entity["prototype"], (cell["x"], cell["y"]), "equipment " + entity["id"])
    docking = audit_docking(design, manifest, tiles, entities, prototypes, grid_uid, errors, warnings)
    for network in layout["transportNetworks"]:
        for entity in [*network["terminals"], *network["segments"]]:
            cell = entity["position"]
            require_hardware(entity["prototype"], (cell["x"], cell["y"]), "transport " + entity["id"])
    unresolved_zones, checked_zone_tiles = [], 0
    for zone in layout["environmentZones"]:
        if zone["pressureKpa"] is None:
            unresolved_zones.append(zone["roomId"])
            if not any(item["code"] == "export-atmosphere" and item["path"] == zone["roomId"] for item in manifest["diagnostics"]):
                errors.append("An unconfigured gas zone is missing its manifest warning: " + zone["roomId"])
            continue
        for cell in zone["cells"]:
            mix = atmospheres.get((cell["x"], cell["y"]))
            if mix is None:
                errors.append("An explicit initial gas zone lacks serialized atmosphere: " + zone["roomId"])
                continue
            checked_zone_tiles += 1
            temperature = zone["temperatureKelvin"] if zone["temperatureKelvin"] is not None else 293.15
            total = sum(mix["moles"].values())
            pressure = total * 8.314462618 * mix["temperature"] / mix["volume"]
            if not math.isclose(pressure, zone["pressureKpa"], rel_tol=1e-6, abs_tol=1e-6) or not math.isclose(temperature, mix["temperature"], rel_tol=1e-6):
                errors.append("An initial zone differs from serialized pressure/temperature: " + zone["roomId"])
            if total > 0 and any(not math.isclose(mix["moles"].get(gas, 0) / total, fraction, rel_tol=1e-6, abs_tol=1e-8) for gas, fraction in zone["gasFractions"].items()):
                errors.append("An initial zone differs from serialized gas fractions: " + zone["roomId"])
    if not set(atmospheres).issubset(tiles):
        errors.append("Serialized atmosphere references cells without materialized grid tiles.")
    if unresolved_zones:
        warnings.append("Initial custom gas mixtures remain explicitly unconfigured for: " + ", ".join(sorted(unresolved_zones)))
    if len(descriptors) != 1 or descriptors[0].get("type") != "gameMap" or descriptors[0]["mapPath"] != "/Maps/Generated/" + manifest["mapFile"]:
        errors.append("The map descriptor does not refer to the bundled map filename.")
    station_ids = {component["id"] for entity in entities.values() for component in entity.get("components", []) if component["type"] == "BecomesStation"}
    if station_ids != set(descriptors[0]["stations"]):
        errors.append("Descriptor station IDs and actual grid BecomesStation IDs differ.")
    for station in descriptors[0]["stations"].values():
        if station["stationProto"] not in prototypes:
            errors.append("Unknown descriptor station prototype: " + station["stationProto"])
        for component in station.get("components", []):
            if component["type"] == "StationJobs" and any(job not in known["job"] for job in component.get("availableJobs", {})):
                errors.append("Descriptor job slots reference an unknown job prototype.")
    if manifest.get("simulationVerified") or manifest.get("operational"):
        errors.append("The draft claims runtime verification that this audit cannot provide.")
    native = audit_native_systems(design, manifest, entities, prototypes, grid_uid, errors, warnings)
    return {"success": not errors, "bundle": str(bundle), "entityCount": len(entities),
            "distinctEntityPrototypes": len([key for key in used if key]), "tilePalette": palette,
            "materializedTiles": len(tiles), "mapChunks": chunk_count, "plannedRoomInstances": len(layout["rooms"]),
            "verifiedHardwarePlacements": checked_hardware,
            "dockingClearanceAudit": docking,
            "nativeSystemsAudit": native,
            "atmosphereEncoding": "GridAtmosphere-v2-mapping", "atmosphereUniqueMixtures": mixture_count,
            "atmosphereChunks": atmosphere_chunks, "atmosphereTiles": len(atmospheres),
            "verifiedExplicitZoneTiles": checked_zone_tiles, "unconfiguredCustomZones": sorted(unresolved_zones),
            "geometryAcceptedByDesigner": manifest.get("geometryAccepted"), "engineLoadTested": False,
            "simulationVerified": False, "warnings": warnings, "errors": sorted(set(errors)),
            "serializerReferences": ["RobustToolbox/Robust.Shared/EntitySerialization/MapChunkSerializer.cs",
                                     "Content.Shared/Atmos/Serialization/TileAtmosCollectionSerializer.cs",
                                     "Content.Shared/Atmos/GasArraySerializer.cs"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = audit(args.repo.resolve(), args.bundle)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 0 if result["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
