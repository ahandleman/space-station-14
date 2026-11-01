"""Reproduce geometry and landmark evidence used for compact/nested layout defaults.

Run from the repository root: python Tools/StationArchitect/Analysis/layout_evidence.py
This observes map files; closed-door components are conservative geometry, not gameplay paths.
"""
import json
import math
from pathlib import Path

import analyze_maps as maps


def extract(repo):
    source = json.loads((repo / "Tools/StationArchitect/Analysis/map-evidence.json").read_text())
    prototypes = maps.prototype_index(repo)
    migrations = maps.migration_index(repo)
    output = []
    for record in source["maps"]:
        if record["id"] not in {"Bagel", "Box", "Packed", "Saltern", "Marathon"}:
            continue
        data = maps.load(repo / record["sourcePath"])
        entities = maps.map_entities(data, prototypes, migrations)
        grids = maps.decode_grids(data, entities)
        positions = maps.positions(entities, grids)
        grid = record["mainGrid"]
        regions, by_cell, boundaries = maps.grid_geometry(grids[grid], entities, positions, grid)
        labels = {"Arrivals", "Cryosleep", "Cryopods", "Evac", "evac", "Chapel", "Kitchen", "Botany", "Bar", "Library", "Service", "Theatre"}
        beacons = [beacon for beacon in record["beacons"] if beacon["onMainGrid"] and beacon["label"] in labels]
        chapel = next((beacon for beacon in beacons if beacon["label"] == "Chapel"), None)
        chapel_component = chapel.get("closedDoorFloorComponent") if chapel else None
        connected = {chapel_component}
        chapel_boundaries = [boundary for boundary in boundaries if chapel_component in boundary["components"]]
        for boundary in chapel_boundaries:
            connected.update(boundary["components"])
        features = []
        for entity in entities.values():
            position = positions.get(entity["uid"])
            if not position or position[0] != grid:
                continue
            cell = math.floor(position[1]), math.floor(position[2])
            component = by_cell.get(cell)
            text = (entity["prototype"] + " " + entity["name"]).lower()
            if "cremat" in text or "confess" in text or component in connected and any(word in text for word in ["chair", "altar", "curtain", "bookshelf"]):
                features.append({"uid": entity["uid"], "prototype": entity["prototype"], "name": entity["name"], "position": list(position[1:]), "closedDoorFloorComponent": component})
        selected = connected | {beacon.get("closedDoorFloorComponent") for beacon in beacons}
        selected.update(feature["closedDoorFloorComponent"] for feature in features)
        tiles = grids[grid]
        min_x, max_x = min(x for x, _ in tiles), max(x for x, _ in tiles)
        min_y, max_y = min(y for _, y in tiles), max(y for _, y in tiles)
        bbox_area = (max_x - min_x + 1) * (max_y - min_y + 1)
        output.append({"id": record["id"], "sourcePath": record["sourcePath"], "sha256": maps.sha256(repo / record["sourcePath"]), "mainGrid": grid,
                       "mainGridFloorTiles": len(tiles), "floorBounds": {"x": min_x, "y": min_y, "width": max_x - min_x + 1, "height": max_y - min_y + 1},
                       "floorBoundingFill": round(len(tiles) / bbox_area, 4), "beacons": beacons,
                       "selectedClosedDoorComponents": [region for region in regions if region["componentId"] in selected],
                       "chapelDoorBoundaries": chapel_boundaries, "chapelRelatedFeatures": features})
    return {"evidenceVersion": 1, "method": "Decoded main-grid floor tiles, inherited entity components, mapped names/positions, and conservative closed-door floor components.",
            "limitations": source["limitations"], "prototypeInputHash": source["inputHashes"]["entityPrototypeInputs"], "maps": output}


if __name__ == "__main__":
    repo = Path(__file__).resolve().parents[3]
    destination = Path(__file__).with_name("layout-evidence.json")
    destination.write_text(json.dumps(extract(repo), indent=2) + "\n", encoding="utf-8")
    print(destination)
