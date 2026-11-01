"""Observe strategic landmarks in local maps without claiming simulated gameplay balance.

Distances are an all-doors-open, furniture-free tile approximation. They inform
what the configurable gameflow rules should measure, not universal target values.
"""
import collections
import json
import math
from pathlib import Path

import analyze_maps as maps


LABELS = {
    "Bridge": "bridge", "Vault": "vault", "Armory": "armory", "AI Core": "ai",
    "Arrivals": "arrivals", "Evac": "evac", "evac": "evac", "Security": "security",
    "Medbay": "medical", "Medical": "medical",
}


def distances(floor, source):
    found = {source: 0}
    queue = collections.deque([source])
    while queue:
        x, y = queue.popleft()
        for dx, dy in maps.DIRECTIONS:
            neighbor = x + dx, y + dy
            if neighbor in floor and neighbor not in found:
                found[neighbor] = found[x, y] + 1
                queue.append(neighbor)
    return found


def extract(repo):
    evidence = json.loads((repo / "Tools/StationArchitect/Analysis/map-evidence.json").read_text())
    prototypes, migrations = maps.prototype_index(repo), maps.migration_index(repo)
    output = []
    for record in evidence["maps"]:
        if record["id"] not in {"Bagel", "Box", "Packed", "Saltern", "Marathon"}:
            continue
        data = maps.load(repo / record["sourcePath"])
        entities = maps.map_entities(data, prototypes, migrations)
        grids = maps.decode_grids(data, entities)
        positions = maps.positions(entities, grids)
        grid = record["mainGrid"]
        _, regions, boundaries = maps.grid_geometry(grids[grid], entities, positions, grid)
        floor = set(grids[grid])
        for entity in entities.values():
            fields = entity["components"]
            location = positions.get(entity["uid"])
            if not location or location[0] != grid or fields.get("Transform", {}).get("parent") != grid:
                continue
            if "Door" not in fields and ("Wall" in fields or "Window" in fields.get("Tag", {}).get("tags", []) or "Grille" in fields):
                floor.discard((math.floor(location[1]), math.floor(location[2])))
        landmarks = []
        for marker in record["beacons"]:
            if not marker["onMainGrid"] or marker["label"] not in LABELS:
                continue
            cell = tuple(math.floor(value) for value in marker["position"])
            candidates = sorted((point for point in floor if sum(abs(a-b) for a,b in zip(cell,point)) <= 4),
                                key=lambda point: (sum(abs(a-b) for a,b in zip(cell,point)), point))
            region = regions.get(cell)
            landmarks.append({"label": marker["label"], "role": LABELS[marker["label"]],
                              "uid": marker["uid"], "prototype": marker["prototype"], "position": marker["position"],
                              "observedFloorCell": list(candidates[0]) if candidates else None,
                              "closedDoorComponent": region,
                              "componentDoorBoundaries": [boundary for boundary in boundaries if region in boundary["components"]] if region is not None else []})
        routes = []
        for left in landmarks:
            if left["observedFloorCell"] is None:
                continue
            reached = distances(floor, tuple(left["observedFloorCell"]))
            for right in landmarks:
                if right["uid"] <= left["uid"] or right["observedFloorCell"] is None:
                    continue
                endpoint = tuple(right["observedFloorCell"])
                routes.append({"fromUid": left["uid"], "toUid": right["uid"], "from": left["role"], "to": right["role"],
                               "manhattanTiles": sum(abs(a-b) for a,b in zip(left["observedFloorCell"],endpoint)),
                               "allDoorsOpenFloorDistance": reached.get(endpoint)})
        output.append({"id": record["id"], "sourcePath": record["sourcePath"], "sha256": maps.sha256(repo / record["sourcePath"]),
                       "mainGrid": grid, "landmarks": landmarks, "pairDistances": routes})
    return {"evidenceVersion": 1, "prototypeInputHash": evidence["inputHashes"]["entityPrototypeInputs"],
            "method": "Native map tiles and inherited wall/window/grille/door components; exact mapped navigation beacons and closed-door component boundaries.",
            "assumptions": ["Every door is traversable regardless of access, bolts, power or pressure.",
                            "Furniture, equipment, collision-fixture subtleties, combat and player tactics are ignored.",
                            "Directional walls/windows occupy the whole tile in this approximation; space and lattice tiles are excluded.",
                            "A beacon may snap at most four Manhattan tiles to observed floor; both coordinates are recorded.",
                            "Door boundaries are component observations, not independent escape-route proofs.",
                            "These measurements do not prescribe generator bounds or certify nuclear-operative balance."],
            "maps": output}


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[3]
    destination = Path(__file__).with_name("gameflow-evidence.json")
    result = extract(root)
    destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"maps": len(result["maps"]), "landmarks": sum(len(item["landmarks"]) for item in result["maps"]), "output": str(destination)}))
