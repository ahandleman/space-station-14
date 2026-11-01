"""Reproducible, read-only evidence from this checkout's station maps.

Requires Python 3 + PyYAML. Run from any working directory. It does not load the
game or evaluate arbitrary YAML tags. Geometric observations are conservative
closed-door floor components, not claimed game rooms or pathfinding results.
"""
from __future__ import annotations

import argparse
import base64
import collections
import csv
import hashlib
import json
import math
import pathlib
import re
import struct

import yaml


class SourceLoader(getattr(yaml, "CSafeLoader", yaml.SafeLoader)):
    pass


class VariantNode(dict):
    """Data marker for the one YAML tag whose expansion affects prototype IDs."""


def create_variants(loader, node):
    if not isinstance(node, yaml.MappingNode):
        raise ValueError("CreateVariants requires a mapping")
    return VariantNode(loader.construct_mapping(node, deep=True))


def ignore_tag(loader, tag_suffix, node):
    # SS14 !type tags are data in source prototypes. Never instantiate named types.
    if isinstance(node, yaml.MappingNode):
        return loader.construct_mapping(node, deep=True)
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node, deep=True)
    return loader.construct_scalar(node)


SourceLoader.add_multi_constructor("!", ignore_tag)
SourceLoader.add_constructor("!type:CreateVariants", create_variants)


def load(path):
    return yaml.load(path.read_text(encoding="utf-8-sig"), Loader=SourceLoader)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prototype_inputs_hash(repo):
    digest = hashlib.sha256()
    for path in sorted((repo / "Resources/Prototypes").rglob("*.yml")):
        digest.update(path.relative_to(repo).as_posix().encode("utf-8") + b"\0")
        digest.update(bytes.fromhex(sha256(path)))
    return digest.hexdigest()


def components(items):
    return {item["type"]: {key: value for key, value in item.items() if key != "type"}
            for item in items or [] if isinstance(item, dict) and "type" in item}


def merge_components(base, override):
    result = dict(base)
    for name, data in override.items():
        result[name] = dict(base.get(name, {})) | data
    return result


def expand_variants(prototype):
    """Expand only the fixed CreateVariants data transform used by the engine.

    IDs select a variant index from values; tagged scalar and sequence fields
    select the same index recursively. This never instantiates a named YAML type.
    """
    id_node = prototype.get("id")
    if not isinstance(id_node, VariantNode):
        return [prototype]
    ids = id_node.get("values")
    if not isinstance(ids, list) or not ids or not all(isinstance(value, str) for value in ids):
        raise ValueError("A CreateVariants prototype ID needs a nonempty values list of IDs")

    def select(value, index):
        if isinstance(value, VariantNode):
            values = value.get("sequences", value.get("values"))
            if not isinstance(values, list) or index >= len(values):
                raise ValueError(f"CreateVariants has no value for variant index {index}")
            return select(values[index], index)
        if isinstance(value, dict):
            return {key: select(child, index) for key, child in value.items()}
        if isinstance(value, list):
            return [select(child, index) for child in value]
        return value

    return [select(prototype, index) for index in range(len(ids))]


def migration_index(repo):
    """Map-load aliases/deletions from Content.Server.Maps.MapMigrationSystem."""
    path = repo / "Resources/migration.yml"
    return load(path) if path.is_file() else {}


def effective_prototype(raw_id, migrations):
    # EntityDeserializer applies the explicit map alias once, not recursively.
    value = migrations.get(raw_id, raw_id)
    return None if value is None or str(value).strip() in ("", "null") else value


def prototype_index(repo):
    index = {}
    # Entities also live in Catalog/Fills and other prototype folders.
    paths = sorted((repo / "Resources/Prototypes").rglob("*.yml"))
    for path in paths:
        for prototype in load(path) or []:
            if not isinstance(prototype, dict) or prototype.get("type") != "entity":
                continue
            for expanded in expand_variants(prototype):
                if isinstance(expanded.get("id"), str):
                    index[expanded["id"]] = expanded | {"sourcePath": path.relative_to(repo).as_posix()}
    resolved = {}
    active = set()

    def resolve(name):
        if name in resolved:
            return resolved[name]
        if name not in index or name in active:
            return {"components": {}, "name": "", "ancestors": []}
        active.add(name)
        source = index[name]
        parents = source.get("parent", [])
        if isinstance(parents, str):
            parents = [parents]
        current = {"components": {}, "name": "", "ancestors": []}
        for parent in parents:
            inherited = resolve(parent)
            # SS14 first parent takes precedence where parents supply the same value.
            current["components"] = merge_components(inherited["components"], current["components"])
            current["ancestors"].extend([parent] + inherited["ancestors"])
            if not current["name"]:
                current["name"] = inherited["name"]
        current["components"] = merge_components(current["components"], components(source.get("components")))
        current["name"] = source.get("name", current["name"])
        current["sourcePath"] = source["sourcePath"]
        current["ancestors"] = list(dict.fromkeys(current["ancestors"]))
        active.remove(name)
        resolved[name] = current
        return current

    for name in index:
        resolve(name)
    return resolved


def map_descriptors(repo):
    default_pool = next(item for item in load(repo / "Resources/Prototypes/Maps/Pools/default.yml")
                        if item.get("id") == "DefaultMapPool")["maps"]
    descriptors = []
    for path in sorted((repo / "Resources/Prototypes/Maps").glob("*.yml")):
        for item in load(path) or []:
            if not isinstance(item, dict) or item.get("type") != "gameMap":
                continue
            map_path = pathlib.PurePosixPath(item.get("mapPath", "").lstrip("/"))
            # Root-level station maps only. CentComm, arenas, tests, ruins, shuttles
            # are not station examples. Retain all other authored root stations.
            if len(map_path.parts) != 2 or map_path.parts[0] != "Maps" or map_path.stem == "centcomm":
                continue
            if not any(station.get("stationProto") == "StandardNanotrasenStation"
                       for station in item.get("stations", {}).values()):
                continue
            descriptors.append(item | {"descriptorPath": path.relative_to(repo).as_posix(),
                                       "sourcePath": (pathlib.PurePosixPath("Resources") / map_path).as_posix(),
                                       "inDefaultPool": item["id"] in default_pool})
    return sorted(descriptors, key=lambda item: item["id"])


def localized_beacons(repo):
    path = repo / "Resources/Locale/en-US/navmap-beacons/station-beacons.ftl"
    return dict(re.findall(r"^([\w-]+)\s*=\s*(.*?)\s*$", path.read_text(encoding="utf-8"), re.M))


def decode_grids(data, entities):
    result = {}
    tilemap = data.get("tilemap", {})
    for entity in entities.values():
        component = entity["components"].get("MapGrid")
        if component is None:
            continue
        tiles = {}
        for chunk in component.get("chunks", {}).values():
            size = int(chunk.get("size", 16))
            cx, cy = map(int, str(chunk["ind"]).split(","))
            version = int(chunk.get("version", 1))
            packed = base64.b64decode(chunk["tiles"])
            format_ = "<iBBB" if version >= 7 else "<iBB" if version >= 6 else "<HBB"
            stride = struct.calcsize(format_)
            if len(packed) != stride * size * size:
                raise ValueError(f"Unexpected chunk byte length in version {version}")
            for i, (tile_id, *_) in enumerate(struct.iter_unpack(format_, packed)):
                tile_name = tilemap[tile_id]
                if tile_name not in ("Space", "Lattice"):
                    tiles[cx * size + i % size, cy * size + i // size] = tile_name
        result[entity["uid"]] = tiles
    return result


def pair(value):
    try:
        return tuple(map(float, str(value).split(",")))
    except (TypeError, ValueError):
        return (0., 0.)


def map_entities(data, prototypes, migrations=None):
    entities = {}
    for group in data.get("entities", []):
        raw_prototype = group.get("proto", "")
        prototype = effective_prototype(raw_prototype, migrations or {}) if raw_prototype else ""
        if prototype is None:
            continue
        defaults = prototypes.get(prototype, {"components": {}, "name": "", "ancestors": []})
        for item in group.get("entities", []):
            entity = {"uid": item["uid"], "prototype": prototype, "rawPrototype": raw_prototype,
                      "name": defaults["name"],
                      "ancestors": defaults["ancestors"],
                      "components": merge_components(defaults["components"], components(item.get("components")))}
            entity["name"] = entity["components"].get("MetaData", {}).get("name", entity["name"])
            entities[item["uid"]] = entity
    return entities


def positions(entities, grids):
    cached = {}
    active = set()

    def get(uid):
        if uid in cached:
            return cached[uid]
        if uid in grids:
            return uid, 0., 0.
        if uid not in entities or uid in active:
            return None
        active.add(uid)
        transform = entities[uid]["components"].get("Transform", {})
        local_x, local_y = pair(transform.get("pos", "0,0"))
        parent = get(transform.get("parent"))
        active.remove(uid)
        # Room markers and structural objects are grid children. Nested entities
        # are located by parent translation for audit only; not room sizing inputs.
        cached[uid] = None if parent is None else (parent[0], parent[1] + local_x, parent[2] + local_y)
        return cached[uid]

    for uid in entities:
        get(uid)
    return cached


DIRECTIONS = ((1, 0), (-1, 0), (0, 1), (0, -1))


def grid_geometry(tiles, entities, locations, grid_id):
    blocked = set()
    doors = []
    for entity in entities.values():
        location = locations.get(entity["uid"])
        transform = entity["components"].get("Transform", {})
        if not location or location[0] != grid_id or transform.get("parent") != grid_id:
            continue
        cell = math.floor(location[1]), math.floor(location[2])
        fields = entity["components"]
        tags = fields.get("Tag", {}).get("tags", [])
        # Full-grid approximation deliberately closes doors and treats directional
        # walls/windows as whole blocked tiles. No equipment/furniture obstruction.
        if "Wall" in fields or "Window" in tags or "Door" in fields or "Grille" in fields:
            blocked.add(cell)
        if "Door" in fields:
            doors.append((entity, cell))
    available = set(tiles) - blocked
    regions = []
    region_by_cell = {}
    for cell in sorted(available):
        if cell in region_by_cell:
            continue
        region_id = len(regions)
        queue = [cell]
        region_by_cell[cell] = region_id
        cells = []
        open_edges = 0
        while queue:
            current = queue.pop()
            cells.append(current)
            for dx, dy in DIRECTIONS:
                neighbor = current[0] + dx, current[1] + dy
                if neighbor in available and neighbor not in region_by_cell:
                    region_by_cell[neighbor] = region_id
                    queue.append(neighbor)
                elif neighbor not in tiles and neighbor not in blocked:
                    open_edges += 1
        xs = [point[0] for point in cells]
        ys = [point[1] for point in cells]
        width, height = max(xs) - min(xs) + 1, max(ys) - min(ys) + 1
        regions.append({"componentId": region_id, "area": len(cells),
                        "bounds": {"x": min(xs), "y": min(ys), "width": width, "height": height},
                        "aspectRatio": round(max(width, height) / min(width, height), 4),
                        "fillRatio": round(len(cells) / (width * height), 4),
                        "openEdgesToMissingFloor": open_edges, "labels": []})
    door_boundaries = []
    for entity, cell in doors:
        neighbors = sorted({region_by_cell[p] for dx, dy in DIRECTIONS
                            if (p := (cell[0] + dx, cell[1] + dy)) in region_by_cell})
        if len(neighbors) > 1:
            reader = entity["components"].get("AccessReader", {})
            door_boundaries.append({"uid": entity["uid"], "prototype": entity["prototype"],
                                    "cell": list(cell), "components": neighbors,
                                    "access": reader.get("access", []),
                                    "externalAccess": reader.get("externalAccess", [])})
    return regions, region_by_cell, door_boundaries


def inspect_map(repo, descriptor, prototypes, localization, feature_rules, migrations=None):
    path = repo / descriptor["sourcePath"]
    data = load(path)
    entities = map_entities(data, prototypes, migrations)
    grids = decode_grids(data, entities)
    locations = positions(entities, grids)
    main_grid = max(grids, key=lambda key: len(grids[key]))
    regions, region_by_cell, boundaries = grid_geometry(grids[main_grid], entities, locations, main_grid)
    beacons, landmarks, systems = [], [], collections.defaultdict(list)
    for entity in entities.values():
        fields, uid = entity["components"], entity["uid"]
        location = locations.get(uid)
        if not location:
            continue
        base = {"uid": uid, "prototype": entity["prototype"], "rawPrototype": entity["rawPrototype"], "grid": location[0],
                "position": [round(location[1], 4), round(location[2], 4)],
                "onMainGrid": location[0] == main_grid}
        if "NavMapBeacon" in fields:
            beacon = fields["NavMapBeacon"]
            label = beacon.get("text") or localization.get(beacon.get("defaultText"), beacon.get("defaultText", ""))
            marker = base | {"label": str(label), "defaultText": beacon.get("defaultText", ""),
                             "color": beacon.get("color", "")}
            if location[0] == main_grid and fields.get("Transform", {}).get("parent") == main_grid:
                cell = math.floor(location[1]), math.floor(location[2])
                region_id = region_by_cell.get(cell)
                if region_id is not None:
                    marker["closedDoorFloorComponent"] = region_id
                    regions[region_id]["labels"].append(str(label))
            beacons.append(marker)
        if "WarpPoint" in fields and "NavMapBeacon" not in fields:
            warp = fields["WarpPoint"]
            label = warp.get("location", "") or entity["name"]
            if label:
                landmarks.append(base | {"kind": "warp", "label": str(label)})
        if fields.get("MetaData", {}).get("name") and ("APC" in fields or "AirAlarm" in fields):
            landmarks.append(base | {"kind": "apc" if "APC" in fields else "airAlarm", "label": str(entity["name"])})
        for feature, rule in feature_rules.items():
            if rule["pattern"].search(entity["prototype"]):
                systems[feature].append(base)
    for region in regions:
        region["labels"] = sorted(set(region["labels"]))
    prototype_counts = collections.Counter(entity["prototype"] for entity in entities.values()
                                           if (locations.get(entity["uid"]) or (None,))[0] == main_grid)
    raw_counts = collections.Counter(entity["rawPrototype"] for entity in entities.values()
                                     if (locations.get(entity["uid"]) or (None,))[0] == main_grid)
    applied_migrations = [{"rawPrototype": group.get("proto", ""),
                          "effectivePrototype": effective_prototype(group.get("proto", ""), migrations or {}),
                          "entityCount": len(group.get("entities", []))}
                         for group in data.get("entities", [])
                         if group.get("proto") and group.get("proto") in (migrations or {})]
    jobs = {}
    for station in descriptor.get("stations", {}).values():
        for component in station.get("components", []):
            if component.get("type") == "StationJobs":
                jobs.update(component.get("availableJobs", {}))
    return {"id": descriptor["id"], "name": descriptor.get("mapName", descriptor["id"]),
            "sourcePath": descriptor["sourcePath"], "descriptorPath": descriptor["descriptorPath"],
            "sha256": sha256(path), "descriptorSha256": sha256(repo / descriptor["descriptorPath"]),
            "inDefaultPool": descriptor["inDefaultPool"], "fallback": descriptor.get("fallback", False),
            "minPlayers": descriptor.get("minPlayers"), "maxPlayers": descriptor.get("maxPlayers"),
            "entityCount": len(entities), "rawEntityCount": sum(len(g.get("entities", [])) for g in data.get("entities", [])),
            "appliedPrototypeMigrations": applied_migrations,
            "mainGrid": main_grid, "mainGridFloorTiles": len(grids[main_grid]),
            "gridCount": len(grids), "availableJobs": jobs,
            "beacons": sorted(beacons, key=lambda item: (item["label"], item["uid"])),
            "corroboratingLandmarks": sorted(landmarks, key=lambda item: (item["kind"], item["label"], item["uid"])),
            "features": dict(sorted(systems.items())),
            "mainGridPrototypeCounts": dict(sorted(prototype_counts.items())),
            "mainGridRawPrototypeCounts": dict(sorted(raw_counts.items())),
            "closedDoorFloorComponents": [region for region in regions if region["labels"]],
            "closedDoorBoundaryAdjacencies": boundaries}


def aggregate(maps):
    result = {}
    labels = sorted({beacon["label"] for item in maps for beacon in item["beacons"] if beacon["onMainGrid"]})
    for label in labels:
        observations = []
        for item in maps:
            beacons = [beacon for beacon in item["beacons"] if beacon["label"] == label and beacon["onMainGrid"]]
            if not beacons:
                continue
            region_ids = sorted({beacon["closedDoorFloorComponent"] for beacon in beacons if "closedDoorFloorComponent" in beacon})
            regions = [region for region in item["closedDoorFloorComponents"] if region["componentId"] in region_ids]
            observations.append({"map": item["id"], "inDefaultPool": item["inDefaultPool"], "markerCount": len(beacons),
                                 "componentEstimates": [{"area": region["area"], "aspectRatio": region["aspectRatio"],
                                                         "fillRatio": region["fillRatio"], "labels": region["labels"],
                                                         "openEdgesToMissingFloor": region["openEdgesToMissingFloor"]}
                                                        for region in regions]})
        result[label] = {"mapsPresent": len(observations), "defaultPoolMapsPresent": sum(row["inDefaultPool"] for row in observations),
                         "observations": observations}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[3])
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parent / "map-evidence.json")
    parser.add_argument("--include-boundaries", action="store_true", help="Include every closed-door boundary adjacency for deeper geometry investigation.")
    parser.add_argument("--pretty", action="store_true", help="Indent the JSON report instead of the compact checked-in representation.")
    arguments = parser.parse_args()
    repo = arguments.repo.resolve()
    feature_path = pathlib.Path(__file__).with_name("map_features.json")
    feature_rules = json.loads(feature_path.read_text(encoding="utf-8"))
    for rule in feature_rules.values():
        rule["pattern"] = re.compile(rule["prototypePattern"])
    prototypes = prototype_index(repo)
    migrations = migration_index(repo)
    descriptors = map_descriptors(repo)
    localization = localized_beacons(repo)
    maps = []
    for descriptor in descriptors:
        item = inspect_map(repo, descriptor, prototypes, localization, feature_rules, migrations)
        if not arguments.include_boundaries:
            item.pop("closedDoorBoundaryAdjacencies")
        maps.append(item)
        print(f"{item['id']}: {item['entityCount']} entities, {len(item['beacons'])} beacons", flush=True)
    feature_counts = {}
    for name in feature_rules:
        observations = {item["id"]: sum(value["onMainGrid"] for value in item["features"].get(name, [])) for item in maps}
        feature_counts[name] = {"prototypePattern": feature_rules[name]["prototypePattern"],
                                "mapsPresent": sum(count > 0 for count in observations.values()),
                                "defaultPoolMapsPresent": sum(observations[item["id"]] > 0 for item in maps if item["inDefaultPool"]),
                                "mainGridCounts": observations}
    report = {"evidenceVersion": 2,
              "method": "Entity prototypes from all prototype folders, safe CreateVariants expansion, map-load migrations, inherited defaults plus map component overrides; markers, explicit system prototypes, and conservative closed-door floor components.",
              "limitations": ["Marker labels denote named destinations, not an exhaustive room partition or room-count census.",
                              "Geometric estimates close every door and treat directional walls/windows as whole tiles; furniture is ignored.",
                              "Several labels may occupy one component, and generic department labels often mark circulation; do not treat every estimate as a room's exact size.",
                              "Nested entity positions use parent translations; only direct main-grid marker children receive component sizing estimates.",
                              "Boundary adjacencies are neighboring closed-door components, not verified gameplay paths or required design rules.",
                              "Feature counts cover explicit mapped prototypes on the largest grid, not runtime-spawned contents, optional construction, or guaranteed operation."],
              "defaultPoolIds": [item["id"] for item in maps if item["inDefaultPool"]],
              "mapCount": len(maps), "defaultPoolMapCount": sum(item["inDefaultPool"] for item in maps),
              "inputHashes": {"defaultPool": sha256(repo / "Resources/Prototypes/Maps/Pools/default.yml"),
                              "beaconPrototypes": sha256(repo / "Resources/Prototypes/Entities/Objects/Devices/station_beacon.yml"),
                              "beaconLocalization": sha256(repo / "Resources/Locale/en-US/navmap-beacons/station-beacons.ftl"),
                              "featureQueries": sha256(feature_path),
                              "entityPrototypeInputs": prototype_inputs_hash(repo),
                              "mapPrototypeMigrations": sha256(repo / "Resources/migration.yml"),
                              "prototypeVariantExpansion": sha256(repo / "RobustToolbox/Robust.Shared/Prototypes/PrototypeManager.Variantization.cs"),
                              "tileSerializer": sha256(repo / "RobustToolbox/Robust.Shared/EntitySerialization/MapChunkSerializer.cs")},
              "labelFrequency": aggregate(maps), "featureFrequency": feature_counts, "maps": maps}
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(json.dumps(report, indent=2 if arguments.pretty else None, separators=None if arguments.pretty else (",", ":"), ensure_ascii=False) + "\n", encoding="utf-8")
    csv_path = arguments.output.with_name("room-marker-frequency.csv")
    with csv_path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["label", "defaultPoolMapsPresent", "allStationMapsPresent", "maps", "closedDoorAreas"])
        for label, entry in report["labelFrequency"].items():
            writer.writerow([label, entry["defaultPoolMapsPresent"], entry["mapsPresent"],
                             "; ".join(row["map"] for row in entry["observations"]),
                             "; ".join(f"{row['map']}:{estimate['area']}" for row in entry["observations"] for estimate in row["componentEstimates"])])
    print(f"Wrote {arguments.output} ({len(maps)} station maps; {report['defaultPoolMapCount']} default pool)")


if __name__ == "__main__":
    main()
