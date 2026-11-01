"""Measure actual saved masks; the unbuilt planning envelope is not station floor."""
import argparse
import json
import hashlib
from collections import defaultdict
from pathlib import Path


def normalized_signature(cells):
    """Ignore translation, rotation and reflection; retain optional room-type labels."""
    points = list(cells)
    if not points:
        return hashlib.sha256(b"[]").hexdigest()
    variants = []
    for reflection in (1, -1):
        for turns in range(4):
            transformed = []
            for x, y, label in points:
                x *= reflection
                for _ in range(turns):
                    x, y = -y, x
                transformed.append((x, y, label))
            min_x = min(x for x, y, label in transformed)
            min_y = min(y for x, y, label in transformed)
            variants.append(sorted((x-min_x, y-min_y, label) for x, y, label in transformed))
    return hashlib.sha256(json.dumps(min(variants), separators=(",", ":")).encode()).hexdigest()


def owner_signatures(project):
    groups = defaultdict(list)
    for room in project["layout"]["rooms"]:
        if room["enabled"] and not room["orphaned"]:
            groups[room["ownerId"]].append(room)
    return {owner: {"floor": sum(len(room["cells"]) for room in rooms),
                    "silhouette": normalized_signature((cell["x"], cell["y"], "") for room in rooms for cell in room["cells"]),
                    "roomArrangement": normalized_signature((cell["x"], cell["y"], room["definitionPath"]) for room in rooms for cell in room["cells"])}
            for owner, rooms in sorted(groups.items())}


def measure(project):
    project = project.get("project", project)
    layout = project["layout"]
    definitions = {}

    def visit(rooms, parent):
        for room in rooms:
            path = parent + "/" + room["id"]
            definitions[path] = room
            visit(room.get("rooms", []), path)

    for owner in project["definition"]["departments"]:
        visit(owner["rooms"], owner["id"])
    visit(project["definition"]["maintenance"]["rooms"], "_maintenance")
    rooms = [room for room in layout["rooms"] if room["enabled"] and not room["orphaned"]]
    interior = [room for room in rooms if definitions[room["definitionPath"]]["environment"]["placement"] != "exterior"]

    def cells(items):
        return {(cell["x"], cell["y"]) for cell in items}

    def metric(points):
        if not points:
            return {"cells": 0, "bounds": {"width": 0, "height": 0}, "bboxArea": 0, "density": 0}
        width = max(x for x, y in points) - min(x for x, y in points) + 1
        height = max(y for x, y in points) - min(y for x, y in points) + 1
        return {"cells": len(points), "bounds": {"width": width, "height": height},
                "bboxArea": width * height, "density": len(points) / (width * height)}

    owned = cells(cell for room in rooms for cell in room["cells"])
    material = owned.copy()
    for key in ["publicCorridors", "maintenance", "walls", "hull", "windows"]:
        material.update(cells(layout[key]))
    material.update(cells(cell for port in layout["interfaces"] for cell in port["cells"]))
    return {"rooms": len(rooms), "canvas": [project["settings"]["width"], project["settings"]["height"]],
            "allOwnedFloor": metric(owned),
            "interiorOwnedFloor": metric(cells(cell for room in interior for cell in room["cells"])),
            "coreOwnedFloor": metric(cells(cell for room in interior if room["ownerId"] != "_maintenance" for cell in room["cells"])),
            "allMaterialized": metric(material),
            "ownerSignatures": owner_signatures(project),
            "hardErrors": [item for item in (project.get("report") or {}).get("diagnostics", []) if item["severity"] == "error"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = {"source": args.project.as_posix(), **measure(json.loads(args.project.read_text(encoding="utf-8-sig")))}
    encoded = json.dumps(result, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(encoded, encoding="utf-8")
    print(encoded, end="")
